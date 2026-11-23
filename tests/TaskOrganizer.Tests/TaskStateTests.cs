using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Tests;

/// <summary>On hold, done for today, delete, and where each state shows up.</summary>
public class TaskStateTests
{
    private static readonly DateTime Monday9am = new(2026, 11, 16, 9, 0, 0);
    private static readonly DateOnly Monday = new(2026, 11, 16);
    private static readonly SuggestionRequest NoFocus = new(Array.Empty<int>(), null);

    private static Task<WorkTask> AddAsync(TestHost host, string title, TaskPriority priority = TaskPriority.Medium, double? hours = null, bool planForToday = false, int? projectId = null)
    {
        return host.Tasks.AddAsync(new TaskDetails(title, projectId, priority, null, hours), planForToday: planForToday);
    }

    private static IEnumerable<string> Titles(IEnumerable<WorkTask> tasks) => tasks.Select(t => t.Title);

    // ---- On hold ----

    [Fact]
    public async Task A_task_put_on_hold_keeps_the_reason_in_my_own_words_and_records_it_in_the_history()
    {
        // The reason is the whole point of the status: it says who or what the task is waiting on.
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Review the pull request");
        await host.Tasks.StartAsync(task.Id);
        host.Time.Advance(TimeSpan.FromHours(2));

        await host.Tasks.HoldAsync(task.Id, "  AWAITING FEEDBACK FROM ALICE  ", Monday.AddDays(2));

        using AppDbContext fresh = host.NewContext();
        WorkTask saved = await fresh.Tasks.SingleAsync();
        Assert.Equal(WorkTaskStatus.OnHold, saved.Status);
        Assert.Equal("AWAITING FEEDBACK FROM ALICE", saved.HoldReason);
        Assert.Equal(Monday.AddDays(2), saved.HoldCheckBackOn);
        Assert.True(saved.IsOpen);
        Assert.False(saved.IsWorkable);
        TaskStatusChange change = (await host.Tasks.GetStatusHistoryAsync(task.Id)).Last();
        Assert.Equal(WorkTaskStatus.InProgress, change.FromStatus);
        Assert.Equal(WorkTaskStatus.OnHold, change.ToStatus);
        Assert.Equal("AWAITING FEEDBACK FROM ALICE", change.Note);
        Assert.Equal(new DateTime(2026, 11, 16, 11, 0, 0, DateTimeKind.Utc), change.ChangedAtUtc);
    }

    [Fact]
    public async Task Changing_the_reason_of_a_task_already_on_hold_adds_to_the_history_instead_of_replacing_it()
    {
        // "Comments posted" then "needs testing" is a trail worth keeping; it shows how long each wait lasted.
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Fix the report");
        await host.Tasks.HoldAsync(task.Id, "COMMENTS POSTED", null);
        host.Time.Advance(TimeSpan.FromDays(1));

        await host.Tasks.HoldAsync(task.Id, "NEEDS TESTING", null);

        List<TaskStatusChange> history = await host.Tasks.GetStatusHistoryAsync(task.Id);
        Assert.Equal(new string?[] { null, "COMMENTS POSTED", "NEEDS TESTING" }, history.Select(c => c.Note));
        Assert.Equal(WorkTaskStatus.OnHold, history[2].FromStatus);
        Assert.Equal("NEEDS TESTING", task.HoldReason);
    }

    [Fact]
    public async Task A_hold_needs_a_reason()
    {
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Some task");

        await Assert.ThrowsAsync<DomainRuleException>(() => host.Tasks.HoldAsync(task.Id, "   ", null));
        Assert.Equal(WorkTaskStatus.ToDo, task.Status);
    }

    [Fact]
    public async Task Resuming_a_task_makes_it_workable_again_and_clears_the_hold()
    {
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Some task");
        await host.Tasks.HoldAsync(task.Id, "NEEDS TESTING", Monday.AddDays(1));

        await host.Tasks.ResumeAsync(task.Id);

        Assert.Equal(WorkTaskStatus.ToDo, task.Status);
        Assert.Null(task.HoldReason);
        Assert.Null(task.HoldCheckBackOn);
        Assert.True(task.IsWorkable);
    }

    [Fact]
    public async Task A_task_on_hold_cannot_be_started_or_planned_until_it_is_resumed()
    {
        // On hold means "not being worked on"; letting it be started would make the in-progress count lie.
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Some task");
        await host.Tasks.HoldAsync(task.Id, "NEEDS TESTING", null);

        await Assert.ThrowsAsync<DomainRuleException>(() => host.Tasks.StartAsync(task.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.Tasks.PlanForTodayAsync(task.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.Tasks.PlanManyForTodayAsync(new[] { task.Id }));
        Assert.Equal(WorkTaskStatus.OnHold, task.Status);
    }

    [Fact]
    public async Task A_task_on_hold_is_left_out_of_planned_hours_and_out_of_suggestions()
    {
        // Parked work is not work I can do today, so it must not use up the day or be proposed.
        using TestHost host = new(Monday9am);
        WorkTask parked = await AddAsync(host, "Parked", TaskPriority.Critical, 3, planForToday: true);
        await AddAsync(host, "Active", hours: 2, planForToday: true);
        WorkTask parkedInBacklog = await AddAsync(host, "Parked in backlog", TaskPriority.Critical, 1);
        await host.Tasks.HoldAsync(parked.Id, "NEEDS TESTING", null);
        await host.Tasks.HoldAsync(parkedInBacklog.Id, "COMMENTS POSTED", null);

        TodayView today = await host.Tasks.GetTodayAsync();
        DayProposal proposal = await host.Suggestions.SuggestAsync(NoFocus);

        Assert.Equal(new[] { "Active" }, Titles(today.Planned));
        Assert.Equal(2, today.PlannedHours);
        Assert.Equal(new[] { "Parked" }, Titles(today.OnHold));
        Assert.Empty(proposal.Suggested);
        Assert.Empty(proposal.LeftOut);
        Assert.Equal(2, proposal.AlreadyPlannedHours);
    }

    [Fact]
    public async Task An_on_hold_task_is_brought_up_for_follow_up_once_its_check_back_day_arrives()
    {
        // Parking a task is not the goal; getting it to Done is. The check-back day is what stops it being forgotten.
        using TestHost host = new(Monday9am);
        WorkTask dated = await AddAsync(host, "Chase the reviewer");
        WorkTask undated = await AddAsync(host, "Parked with no date", planForToday: true);
        await host.Tasks.HoldAsync(dated.Id, "AWAITING FEEDBACK", Monday.AddDays(2));
        await host.Tasks.HoldAsync(undated.Id, "NEEDS TESTING", null);
        TodayView monday = await host.Tasks.GetTodayAsync();

        host.Time.SetLocal(Monday9am.AddDays(2));
        TodayView wednesday = await host.Tasks.GetTodayAsync();
        host.Time.SetLocal(Monday9am.AddDays(4));
        TodayView friday = await host.Tasks.GetTodayAsync();

        Assert.Empty(monday.FollowUps);
        Assert.Equal(new[] { "Chase the reviewer" }, Titles(wednesday.FollowUps));
        Assert.Equal(new[] { "Chase the reviewer" }, Titles(friday.FollowUps));
        Assert.Equal(new[] { "Parked with no date" }, Titles(wednesday.OnHold));
    }

    [Fact]
    public async Task The_backlog_lists_tasks_on_hold_and_can_be_filtered_to_them()
    {
        using TestHost host = new(Monday9am);
        await AddAsync(host, "Active");
        WorkTask parked = await AddAsync(host, "Parked");
        await host.Tasks.HoldAsync(parked.Id, "NEEDS TESTING", null);

        BacklogView everything = await host.Tasks.GetBacklogAsync();
        BacklogView onHoldOnly = await host.Tasks.GetBacklogAsync(
            new BacklogFilter(Array.Empty<int>(), Array.Empty<TaskPriority>(), new[] { WorkTaskStatus.OnHold }));

        Assert.Equal(2, everything.ShownCount);
        Assert.Equal(2, everything.Overall.Total);
        Assert.Equal(new[] { "Parked" }, onHoldOnly.Groups.SelectMany(g => g.Tasks).Select(t => t.Title));
        Assert.True(onHoldOnly.IsFiltered);
    }

    [Fact]
    public async Task Remembering_a_hold_reason_adds_it_to_the_suggestions_once()
    {
        using TestHost host = new(Monday9am);
        WorkTask first = await AddAsync(host, "First");
        WorkTask second = await AddAsync(host, "Second");
        WorkTask third = await AddAsync(host, "Third");

        await host.Tasks.HoldAsync(first.Id, "Waiting on the vendor", null, rememberReason: true);
        await host.Tasks.HoldAsync(second.Id, "waiting on the vendor", null, rememberReason: true);
        await host.Tasks.HoldAsync(third.Id, "One-off reason", null);

        List<string> suggestions = (await host.HoldReasons.ListAsync()).Select(r => r.Text).ToList();
        Assert.Single(suggestions, text => text == "Waiting on the vendor");
        Assert.DoesNotContain("One-off reason", suggestions);
        Assert.Contains("NEEDS TESTING", suggestions);
    }

    [Fact]
    public async Task Hold_reason_suggestions_can_be_added_and_removed_but_not_duplicated()
    {
        using TestHost host = new(Monday9am);

        await host.HoldReasons.AddAsync("  Blocked by release freeze ");
        await Assert.ThrowsAsync<DomainRuleException>(() => host.HoldReasons.AddAsync("BLOCKED BY RELEASE FREEZE"));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.HoldReasons.AddAsync("  "));
        HoldReasonSuggestion added = (await host.HoldReasons.ListAsync()).Single(r => r.Text == "Blocked by release freeze");
        await host.HoldReasons.RemoveAsync(added.Id);

        Assert.DoesNotContain(await host.HoldReasons.ListAsync(), r => r.Text == "Blocked by release freeze");
    }

    // ---- Done for today ----

    [Fact]
    public async Task Marking_a_task_done_for_today_keeps_it_open_and_on_show_and_frees_its_planned_hours()
    {
        // The work has stopped but I have not signed it off; it must stay where I can see it.
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Write the summary", hours: 2, planForToday: true);
        await AddAsync(host, "Still to do", hours: 1, planForToday: true);
        host.Time.Advance(TimeSpan.FromHours(5));

        await host.Tasks.MarkDoneForTodayAsync(task.Id);
        TodayView today = await host.Tasks.GetTodayAsync();

        Assert.Equal(WorkTaskStatus.DoneForToday, task.Status);
        Assert.Null(task.CompletedAtUtc);
        Assert.Equal(new DateTime(2026, 11, 16, 14, 0, 0, DateTimeKind.Utc), task.DoneForTodayAtUtc);
        Assert.Equal(new[] { "Write the summary" }, Titles(today.DoneForToday));
        Assert.Empty(today.AwaitingReview);
        Assert.Empty(today.CompletedToday);
        Assert.Equal(new[] { "Still to do" }, Titles(today.Planned));
        Assert.Equal(1, today.PlannedHours);
    }

    [Fact]
    public async Task The_next_day_done_for_today_tasks_are_presented_for_review_and_stay_until_decided()
    {
        // Nothing closes without me: an unreviewed task is still waiting a week later.
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Write the summary", planForToday: true);
        await host.Tasks.MarkDoneForTodayAsync(task.Id);

        host.Time.SetLocal(Monday9am.AddDays(1));
        TodayView tuesday = await host.Tasks.GetTodayAsync();
        host.Time.SetLocal(Monday9am.AddDays(7));
        TodayView nextMonday = await host.Tasks.GetTodayAsync();

        Assert.Equal(new[] { "Write the summary" }, Titles(tuesday.AwaitingReview));
        Assert.Empty(tuesday.DoneForToday);
        Assert.Empty(tuesday.CarriedOver);
        Assert.Equal(new[] { "Write the summary" }, Titles(nextMonday.AwaitingReview));
        Assert.Equal(WorkTaskStatus.DoneForToday, task.Status);
    }

    [Fact]
    public async Task Signing_off_a_reviewed_task_dates_its_completion_to_when_the_work_stopped()
    {
        // The burndown must show the work finishing on Monday afternoon, not on Tuesday morning when I ticked it off.
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Write the summary", planForToday: true);
        host.Time.Advance(TimeSpan.FromHours(6));
        await host.Tasks.MarkDoneForTodayAsync(task.Id);
        host.Time.SetLocal(Monday9am.AddDays(1));

        await host.Tasks.CompleteAsync(task.Id);
        TodayView tuesday = await host.Tasks.GetTodayAsync();

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(new DateTime(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc), task.CompletedAtUtc);
        Assert.Equal(new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc), task.ConfirmedAtUtc);
        Assert.Empty(tuesday.AwaitingReview);
        Assert.Empty(tuesday.CompletedToday);
    }

    [Fact]
    public async Task A_task_completed_outright_is_dated_now_and_has_no_separate_sign_off()
    {
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Quick thing");

        await host.Tasks.CompleteAsync(task.Id);

        Assert.Equal(new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc), task.CompletedAtUtc);
        Assert.Null(task.ConfirmedAtUtc);
    }

    [Fact]
    public async Task Reopening_a_reviewed_task_for_today_puts_it_back_in_todays_work()
    {
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Not finished after all", planForToday: true);
        await host.Tasks.MarkDoneForTodayAsync(task.Id);
        host.Time.SetLocal(Monday9am.AddDays(1));

        await host.Tasks.ReopenForTodayAsync(task.Id);
        TodayView tuesday = await host.Tasks.GetTodayAsync();

        Assert.Equal(WorkTaskStatus.ToDo, task.Status);
        Assert.Null(task.DoneForTodayAtUtc);
        Assert.Equal(Monday.AddDays(1), task.PlannedFor);
        Assert.Equal(new[] { "Not finished after all" }, Titles(tuesday.Planned));
        Assert.Empty(tuesday.AwaitingReview);
    }

    [Fact]
    public async Task Sending_a_reviewed_task_to_the_backlog_makes_it_plain_open_work_with_no_planned_day()
    {
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Pick up later", planForToday: true);
        await host.Tasks.MarkDoneForTodayAsync(task.Id);
        host.Time.SetLocal(Monday9am.AddDays(1));

        await host.Tasks.ReturnToBacklogAsync(task.Id);
        TodayView tuesday = await host.Tasks.GetTodayAsync();
        BacklogView backlog = await host.Tasks.GetBacklogAsync();

        Assert.Equal(WorkTaskStatus.ToDo, task.Status);
        Assert.Null(task.PlannedFor);
        Assert.Empty(tuesday.AwaitingReview);
        Assert.Empty(tuesday.Planned);
        Assert.Empty(tuesday.CarriedOver);
        Assert.Equal(1, backlog.ShownCount);
    }

    [Fact]
    public async Task Each_step_of_the_done_for_today_path_is_in_the_status_history()
    {
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Tracked");
        await host.Tasks.StartAsync(task.Id);
        await host.Tasks.MarkDoneForTodayAsync(task.Id);
        await host.Tasks.ReopenForTodayAsync(task.Id);
        await host.Tasks.MarkDoneForTodayAsync(task.Id);
        await host.Tasks.CompleteAsync(task.Id);

        List<TaskStatusChange> history = await host.Tasks.GetStatusHistoryAsync(task.Id);

        Assert.Equal(
            new[] { WorkTaskStatus.ToDo, WorkTaskStatus.InProgress, WorkTaskStatus.DoneForToday, WorkTaskStatus.ToDo, WorkTaskStatus.DoneForToday, WorkTaskStatus.Completed },
            history.Select(c => c.ToStatus));
    }

    [Fact]
    public async Task A_done_for_today_task_is_not_suggested_and_no_longer_counts_as_work_to_be_done()
    {
        using TestHost host = new(Monday9am);
        WorkTask task = await AddAsync(host, "Finished today", TaskPriority.Critical, 2);
        await AddAsync(host, "Still open", TaskPriority.High, 1);
        await host.Tasks.MarkDoneForTodayAsync(task.Id);

        DayProposal proposal = await host.Suggestions.SuggestAsync(NoFocus);
        BacklogView backlog = await host.Tasks.GetBacklogAsync();

        Assert.DoesNotContain(proposal.Suggested, s => s.Task.Id == task.Id);
        Assert.DoesNotContain(proposal.LeftOut, s => s.Task.Id == task.Id);
        Assert.Equal(1, backlog.Overall.Total);
        Assert.Equal(2, backlog.ShownCount);
    }

    // ---- In progress ----

    [Fact]
    public async Task Today_counts_every_task_in_progress_wherever_it_is_planned()
    {
        // The count is about how many things I have open at once, not about today's list.
        using TestHost host = new(Monday9am);
        WorkTask plannedToday = await AddAsync(host, "Planned today", planForToday: true);
        WorkTask inBacklog = await AddAsync(host, "In the backlog");
        WorkTask parked = await AddAsync(host, "Started then parked");
        await AddAsync(host, "Not started");
        await host.Tasks.StartAsync(plannedToday.Id);
        await host.Tasks.StartAsync(inBacklog.Id);
        await host.Tasks.StartAsync(parked.Id);
        await host.Tasks.HoldAsync(parked.Id, "NEEDS TESTING", null);

        TodayView today = await host.Tasks.GetTodayAsync();

        Assert.Equal(2, today.InProgressCount);
    }

    // ---- Delete ----

    [Fact]
    public async Task Deleting_a_task_removes_it_and_its_history_and_nothing_else()
    {
        // Delete is for tasks created by mistake: no trace should be left to distort later counts.
        using TestHost host = new(Monday9am);
        WorkTask mistake = await AddAsync(host, "Created by mistake", hours: 1, planForToday: true);
        WorkTask keeper = await AddAsync(host, "Real task", hours: 1, planForToday: true);
        await host.Tasks.StartAsync(mistake.Id);
        await host.Tasks.UpdateDetailsAsync(mistake.Id, new TaskDetails("Created by mistake", null, TaskPriority.High, null, 2));

        await host.Tasks.DeleteAsync(mistake.Id);

        using AppDbContext fresh = host.NewContext();
        Assert.Equal(keeper.Id, (await fresh.Tasks.SingleAsync()).Id);
        Assert.All(await fresh.TaskStatusChanges.ToListAsync(), c => Assert.Equal(keeper.Id, c.WorkTaskId));
        Assert.All(await fresh.TaskFieldChanges.ToListAsync(), c => Assert.Equal(keeper.Id, c.WorkTaskId));
        Assert.NotEmpty(await fresh.TaskFieldChanges.ToListAsync());
    }
}
