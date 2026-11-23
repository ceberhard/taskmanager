using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Tests;

public class TaskServiceTests
{
    private static readonly DateTime Monday9am = new(2026, 11, 16, 9, 0, 0);

    private static TaskDetails Details(string title, TaskPriority priority = TaskPriority.Medium, double? hours = null, int? projectId = null)
    {
        return new TaskDetails(title, projectId, priority, null, hours);
    }

    [Fact]
    public async Task Adding_a_task_stamps_creation_time_and_source_from_the_server()
    {
        // The burndown counts work added per day from these two fields, so they must exist on every task.
        using TestHost host = new(Monday9am);

        WorkTask created = await host.Tasks.AddAsync(Details("Write the release note"));

        using AppDbContext fresh = host.NewContext();
        WorkTask saved = await fresh.Tasks.SingleAsync(t => t.Id == created.Id);
        Assert.Equal(new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc), saved.CreatedAtUtc);
        Assert.Equal(TaskSource.Manual, saved.Source);
        Assert.Equal(WorkTaskStatus.ToDo, saved.Status);
        Assert.Null(saved.PlannedFor);
    }

    [Fact]
    public async Task A_task_needs_only_a_title_and_defaults_to_medium_priority()
    {
        // Entry cost is the reason the spreadsheet was abandoned: nothing but the title may be required.
        using TestHost host = new(Monday9am);

        WorkTask created = await host.Tasks.AddAsync(new TaskDetails("Call the vendor", null, TaskPriority.Medium, null, null));

        Assert.Null(created.ProjectId);
        Assert.Null(created.EstimateHours);
        Assert.Equal(TaskPriority.Medium, new WorkTask().Priority);
    }

    [Fact]
    public async Task A_blank_title_is_rejected()
    {
        using TestHost host = new(Monday9am);

        await Assert.ThrowsAsync<DomainRuleException>(() => host.Tasks.AddAsync(Details("   ")));
    }

    [Theory]
    [InlineData(0.25, true)]
    [InlineData(1.5, true)]
    [InlineData(9.0, true)]
    [InlineData(0.1, false)]
    [InlineData(0.4, false)]
    [InlineData(0.0, false)]
    [InlineData(-1.0, false)]
    public async Task Size_is_hours_in_quarter_hour_steps_with_a_quarter_hour_minimum(double hours, bool accepted)
    {
        using TestHost host = new(Monday9am);

        if (accepted)
        {
            WorkTask created = await host.Tasks.AddAsync(Details("Sized task", hours: hours));
            Assert.Equal(hours, created.EstimateHours);
        }
        else
        {
            await Assert.ThrowsAsync<DomainRuleException>(() => host.Tasks.AddAsync(Details("Sized task", hours: hours)));
        }
    }

    [Fact]
    public async Task Completing_sets_the_completed_time_and_dropping_sets_only_the_dropped_time()
    {
        // Dropped work must never look delivered, or the burndown would overstate what got done.
        using TestHost host = new(Monday9am);
        WorkTask done = await host.Tasks.AddAsync(Details("Finish this"));
        WorkTask abandoned = await host.Tasks.AddAsync(Details("Abandon this"));

        host.Time.Advance(TimeSpan.FromHours(2));
        await host.Tasks.CompleteAsync(done.Id);
        await host.Tasks.DropAsync(abandoned.Id);

        DateTime expected = new(2026, 11, 16, 11, 0, 0, DateTimeKind.Utc);
        Assert.Equal(WorkTaskStatus.Completed, done.Status);
        Assert.Equal(expected, done.CompletedAtUtc);
        Assert.Null(done.DroppedAtUtc);
        Assert.Equal(WorkTaskStatus.Dropped, abandoned.Status);
        Assert.Equal(expected, abandoned.DroppedAtUtc);
        Assert.Null(abandoned.CompletedAtUtc);
    }

    [Fact]
    public async Task A_closed_task_cannot_be_switched_between_completed_and_dropped_without_reopening()
    {
        using TestHost host = new(Monday9am);
        WorkTask task = await host.Tasks.AddAsync(Details("Finish this"));
        await host.Tasks.CompleteAsync(task.Id);

        await Assert.ThrowsAsync<DomainRuleException>(() => host.Tasks.DropAsync(task.Id));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
    }

    [Fact]
    public async Task Reopening_clears_the_completed_time_but_the_history_still_shows_the_completion()
    {
        // Reports are built from history; a reopen must not erase the fact that the task was once closed.
        using TestHost host = new(Monday9am);
        WorkTask task = await host.Tasks.AddAsync(Details("Finish this"));
        host.Time.Advance(TimeSpan.FromHours(1));
        await host.Tasks.CompleteAsync(task.Id);
        host.Time.Advance(TimeSpan.FromHours(1));

        await host.Tasks.ReopenAsync(task.Id);

        Assert.Equal(WorkTaskStatus.ToDo, task.Status);
        Assert.Null(task.CompletedAtUtc);
        using AppDbContext fresh = host.NewContext();
        List<TaskStatusChange> history = await fresh.TaskStatusChanges.Where(c => c.WorkTaskId == task.Id).OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(3, history.Count);
        Assert.Null(history[0].FromStatus);
        Assert.Equal(WorkTaskStatus.Completed, history[1].ToStatus);
        Assert.Equal(new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc), history[1].ChangedAtUtc);
        Assert.Equal(WorkTaskStatus.Completed, history[2].FromStatus);
        Assert.Equal(WorkTaskStatus.ToDo, history[2].ToStatus);
    }

    [Fact]
    public async Task Editing_a_task_or_moving_it_to_another_project_keeps_its_creation_time_and_source()
    {
        using TestHost host = new(Monday9am);
        Project first = await host.Projects.AddAsync("Alpha");
        Project second = await host.Projects.AddAsync("Beta");
        WorkTask task = await host.Tasks.AddAsync(Details("Original", projectId: first.Id), TaskSource.Meeting);
        DateTime createdAt = task.CreatedAtUtc;

        host.Time.Advance(TimeSpan.FromDays(3));
        await host.Tasks.UpdateDetailsAsync(task.Id, new TaskDetails("Renamed", second.Id, TaskPriority.High, new DateOnly(2026, 12, 1), 2.5));

        using AppDbContext fresh = host.NewContext();
        WorkTask saved = await fresh.Tasks.SingleAsync(t => t.Id == task.Id);
        Assert.Equal("Renamed", saved.Title);
        Assert.Equal(second.Id, saved.ProjectId);
        Assert.Equal(createdAt, saved.CreatedAtUtc);
        Assert.Equal(TaskSource.Meeting, saved.Source);
    }

    [Fact]
    public async Task A_re_estimate_is_kept_as_history_so_scope_growth_stays_visible()
    {
        // The burndown counts hours. If 2 h quietly became 8 h, the build-up report must be able to see the 6 h that appeared.
        using TestHost host = new(Monday9am);
        WorkTask task = await host.Tasks.AddAsync(Details("Grows", hours: 2));

        host.Time.Advance(TimeSpan.FromDays(1));
        await host.Tasks.UpdateDetailsAsync(task.Id, new TaskDetails("Grows", null, TaskPriority.Medium, null, 8));

        using AppDbContext fresh = host.NewContext();
        TaskFieldChange change = await fresh.TaskFieldChanges.SingleAsync(c => c.WorkTaskId == task.Id);
        Assert.Equal(TaskField.EstimateHours, change.Field);
        Assert.Equal("2", change.OldValue);
        Assert.Equal("8", change.NewValue);
        Assert.Equal(new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc), change.ChangedAtUtc);
    }

    [Fact]
    public async Task Sizing_an_unsized_task_and_raising_its_priority_are_each_recorded()
    {
        // Priority drift (everything becoming High) is one of the things the priority view is meant to expose over time.
        using TestHost host = new(Monday9am);
        WorkTask task = await host.Tasks.AddAsync(Details("Unsized"));

        await host.Tasks.UpdateDetailsAsync(task.Id, new TaskDetails("Unsized", null, TaskPriority.VeryHigh, null, 0.5));

        using AppDbContext fresh = host.NewContext();
        List<TaskFieldChange> changes = await fresh.TaskFieldChanges.Where(c => c.WorkTaskId == task.Id).OrderBy(c => c.Field).ToListAsync();
        Assert.Equal(2, changes.Count);
        TaskFieldChange size = changes.Single(c => c.Field == TaskField.EstimateHours);
        Assert.Null(size.OldValue);
        Assert.Equal("0.5", size.NewValue);
        TaskFieldChange priority = changes.Single(c => c.Field == TaskField.Priority);
        Assert.Equal("Medium", priority.OldValue);
        Assert.Equal("VeryHigh", priority.NewValue);
    }

    [Fact]
    public async Task Saving_a_task_without_changing_size_or_priority_adds_no_history()
    {
        // History rows are counted by reports; a rename or a repeated save must not look like a re-estimate.
        using TestHost host = new(Monday9am);
        WorkTask task = await host.Tasks.AddAsync(Details("Stable", TaskPriority.High, hours: 1.5));

        await host.Tasks.UpdateDetailsAsync(task.Id, new TaskDetails("Stable, renamed", null, TaskPriority.High, new DateOnly(2026, 12, 1), 1.5));
        await host.Tasks.ReturnToBacklogAsync(task.Id);

        using AppDbContext fresh = host.NewContext();
        Assert.Equal(0, await fresh.TaskFieldChanges.CountAsync());
    }

    [Fact]
    public async Task A_task_planned_on_three_different_days_shows_three_plan_records()
    {
        // A task that keeps being re-planned is the clearest sign of a day that was over-committed.
        using TestHost host = new(Monday9am);
        WorkTask task = await host.Tasks.AddAsync(Details("Keeps slipping"), planForToday: true);
        await host.Tasks.PlanForTodayAsync(task.Id);
        host.Time.SetLocal(new DateTime(2026, 11, 17, 8, 0, 0));
        await host.Tasks.PlanForTodayAsync(task.Id);
        host.Time.SetLocal(new DateTime(2026, 11, 18, 8, 0, 0));
        await host.Tasks.PlanForTodayAsync(task.Id);
        await host.Tasks.ReturnToBacklogAsync(task.Id);

        using AppDbContext fresh = host.NewContext();
        List<TaskFieldChange> plans = await fresh.TaskFieldChanges
            .Where(c => c.WorkTaskId == task.Id && c.Field == TaskField.PlannedFor)
            .OrderBy(c => c.Id)
            .ToListAsync();
        Assert.Equal(new string?[] { "2026-11-16", "2026-11-17", "2026-11-18", null }, plans.Select(c => c.NewValue));
        Assert.Equal(new string?[] { null, "2026-11-16", "2026-11-17", "2026-11-18" }, plans.Select(c => c.OldValue));
    }

    [Fact]
    public async Task A_task_pulled_into_today_shows_in_the_today_view_and_leaves_when_sent_back()
    {
        using TestHost host = new(Monday9am);
        WorkTask task = await host.Tasks.AddAsync(Details("Plan me"));

        await host.Tasks.PlanForTodayAsync(task.Id);
        TodayView planned = await host.Tasks.GetTodayAsync();
        await host.Tasks.ReturnToBacklogAsync(task.Id);
        TodayView afterReturn = await host.Tasks.GetTodayAsync();

        Assert.Equal(task.Id, Assert.Single(planned.Planned).Id);
        Assert.Empty(afterReturn.Planned);
        Assert.Empty(afterReturn.CarriedOver);
    }

    [Fact]
    public async Task A_task_planned_yesterday_and_not_finished_is_carried_over_not_silently_kept_or_lost()
    {
        // Unfinished work has to be re-decided each morning; hiding it would hide how far behind the day ran.
        using TestHost host = new(Monday9am);
        WorkTask unfinished = await host.Tasks.AddAsync(Details("Not finished"), planForToday: true);
        WorkTask finished = await host.Tasks.AddAsync(Details("Finished"), planForToday: true);
        await host.Tasks.CompleteAsync(finished.Id);

        host.Time.SetLocal(new DateTime(2026, 11, 17, 8, 0, 0));
        TodayView tuesday = await host.Tasks.GetTodayAsync();

        Assert.Empty(tuesday.Planned);
        Assert.Equal(unfinished.Id, Assert.Single(tuesday.CarriedOver).Id);
        Assert.Empty(tuesday.CompletedToday);
    }

    [Fact]
    public async Task Today_is_the_local_calendar_day_not_the_utc_day()
    {
        // At 10pm in New York it is already tomorrow in UTC; the day must not roll over early.
        TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        using TestHost host = new(new DateTime(2026, 11, 16, 22, 0, 0), eastern);

        WorkTask task = await host.Tasks.AddAsync(Details("Late task"), planForToday: true);
        await host.Tasks.CompleteAsync(task.Id);
        TodayView today = await host.Tasks.GetTodayAsync();

        Assert.Equal(new DateOnly(2026, 11, 16), task.PlannedFor);
        Assert.Equal(new DateOnly(2026, 11, 16), today.Date);
        Assert.Single(today.CompletedToday);
    }

    [Fact]
    public async Task A_closed_task_cannot_be_planned_for_today()
    {
        using TestHost host = new(Monday9am);
        WorkTask task = await host.Tasks.AddAsync(Details("Done already"));
        await host.Tasks.CompleteAsync(task.Id);

        await Assert.ThrowsAsync<DomainRuleException>(() => host.Tasks.PlanForTodayAsync(task.Id));
    }

    [Fact]
    public async Task Planned_hours_count_an_unsized_task_at_the_assumed_size_and_say_how_much_is_assumed()
    {
        // Counting unsized work as nothing made the day look emptier than a suggested day did;
        // both pages must give the same answer to "how full is today".
        using TestHost host = new(Monday9am);
        await host.Settings.UpdateAsync(40, 0, null, 3);
        await host.Tasks.AddAsync(Details("Two hours", hours: 2), planForToday: true);
        await host.Tasks.AddAsync(Details("Unsized"), planForToday: true);
        TodayView atTarget = await host.Tasks.GetTodayAsync();

        await host.Tasks.AddAsync(Details("Another unsized"), planForToday: true);
        TodayView overTarget = await host.Tasks.GetTodayAsync();

        Assert.Equal(3, atTarget.PlannedHours);
        Assert.Equal(1, atTarget.AssumedHours);
        Assert.Equal(1, atTarget.UnsizedCount);
        Assert.False(atTarget.IsOverTarget);
        Assert.Equal(4, overTarget.PlannedHours);
        Assert.True(overTarget.IsOverTarget);
    }

    [Fact]
    public async Task Today_and_a_suggested_day_agree_on_the_hours_already_planned()
    {
        using TestHost host = new(Monday9am);
        await host.Settings.UpdateSuggestionSettingsAsync(70, 1.5, 3);
        await host.Tasks.AddAsync(Details("Sized", hours: 2), planForToday: true);
        await host.Tasks.AddAsync(Details("Unsized"), planForToday: true);

        TodayView today = await host.Tasks.GetTodayAsync();
        DayProposal proposal = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), null));

        Assert.Equal(3.5, today.PlannedHours);
        Assert.Equal(today.PlannedHours, proposal.AlreadyPlannedHours);
    }

    [Fact]
    public async Task Today_lists_the_most_urgent_work_first()
    {
        using TestHost host = new(Monday9am);
        await host.Tasks.AddAsync(Details("Low", TaskPriority.Low), planForToday: true);
        await host.Tasks.AddAsync(Details("Critical", TaskPriority.Critical), planForToday: true);
        await host.Tasks.AddAsync(Details("High", TaskPriority.High), planForToday: true);

        TodayView today = await host.Tasks.GetTodayAsync();

        Assert.Equal(new[] { "Critical", "High", "Low" }, today.Planned.Select(t => t.Title));
    }

    [Fact]
    public async Task An_interruption_needs_no_input_and_lands_in_today_marked_as_an_interruption()
    {
        // One click is the whole budget; the source is what the interruption-frequency report will count.
        using TestHost host = new(new DateTime(2026, 11, 16, 14, 32, 0));

        WorkTask interruption = await host.Tasks.AddInterruptionAsync();

        Assert.Equal(TaskSource.Interruption, interruption.Source);
        Assert.Equal(new DateOnly(2026, 11, 16), interruption.PlannedFor);
        Assert.Equal(new DateTime(2026, 11, 16, 14, 32, 0, DateTimeKind.Utc), interruption.CreatedAtUtc);
        Assert.Equal("Interruption at 14:32", interruption.Title);
        TodayView today = await host.Tasks.GetTodayAsync();
        Assert.Contains(today.Planned, t => t.Id == interruption.Id);
    }

    [Fact]
    public async Task Priority_counts_cover_open_tasks_only_overall_and_per_project()
    {
        // The priority surface exists to show when too much is marked High; closed work must not inflate it.
        using TestHost host = new(Monday9am);
        Project alpha = await host.Projects.AddAsync("Alpha");
        Project beta = await host.Projects.AddAsync("Beta");
        await host.Tasks.AddAsync(Details("A1", TaskPriority.High, projectId: alpha.Id));
        await host.Tasks.AddAsync(Details("A2", TaskPriority.High, projectId: alpha.Id));
        await host.Tasks.AddAsync(Details("B1", TaskPriority.Low, projectId: beta.Id));
        WorkTask closed = await host.Tasks.AddAsync(Details("B2", TaskPriority.High, projectId: beta.Id));
        await host.Tasks.CompleteAsync(closed.Id);

        BacklogView backlog = await host.Tasks.GetBacklogAsync();

        Assert.Equal(2, backlog.Overall.For(TaskPriority.High));
        Assert.Equal(1, backlog.Overall.For(TaskPriority.Low));
        Assert.Equal(3, backlog.Overall.Total);
        ProjectBacklog betaGroup = backlog.Groups.Single(g => g.Project?.Id == beta.Id);
        Assert.Equal(0, betaGroup.Counts.For(TaskPriority.High));
        Assert.Equal(1, betaGroup.Counts.For(TaskPriority.Low));
    }

    [Fact]
    public async Task Project_names_are_unique_regardless_of_case()
    {
        using TestHost host = new(Monday9am);
        await host.Projects.AddAsync("Alpha");

        await Assert.ThrowsAsync<DomainRuleException>(() => host.Projects.AddAsync("ALPHA"));
    }
}
