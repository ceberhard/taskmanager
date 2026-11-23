using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Tests;

public class DaySuggestionServiceTests
{
    private static readonly DateTime Monday9am = new(2026, 11, 16, 9, 0, 0);
    private static readonly DateOnly Monday = new(2026, 11, 16);
    private static readonly SuggestionRequest NoFocus = new(Array.Empty<int>(), null);

    private static Task<WorkTask> AddAsync(TestHost host, string title, TaskPriority priority = TaskPriority.Medium, double? hours = null, int? projectId = null, DateOnly? due = null, bool planForToday = false)
    {
        return host.Tasks.AddAsync(new TaskDetails(title, projectId, priority, due, hours), planForToday: planForToday);
    }

    private static IEnumerable<string> Titles(IEnumerable<SuggestedTask> tasks) => tasks.Select(s => s.Task.Title);

    [Fact]
    public async Task The_day_is_filled_up_to_the_hours_available_and_not_past_them()
    {
        using TestHost host = new(Monday9am);
        for (int number = 1; number <= 10; number++)
        {
            await AddAsync(host, $"Task {number}", hours: 2);
        }

        DayProposal proposal = await host.Suggestions.SuggestAsync(NoFocus);

        Assert.Equal(9, proposal.HoursAvailable);
        Assert.Equal(8, proposal.SuggestedHours);
        Assert.False(proposal.IsOverAvailable);
        Assert.Equal(6, proposal.LeftOut.Count);
    }

    [Fact]
    public async Task Work_already_planned_for_today_uses_up_room_and_is_never_suggested_again()
    {
        // The proposal tops the day up; it must not double-book hours I have already committed.
        using TestHost host = new(Monday9am);
        await AddAsync(host, "Already planned", hours: 5, planForToday: true);
        for (int number = 1; number <= 5; number++)
        {
            await AddAsync(host, $"Candidate {number}", hours: 2);
        }

        DayProposal proposal = await host.Suggestions.SuggestAsync(NoFocus);

        Assert.Equal(5, proposal.AlreadyPlannedHours);
        Assert.Equal(1, proposal.AlreadyPlannedCount);
        Assert.Equal(4, proposal.SuggestedHours);
        Assert.DoesNotContain("Already planned", Titles(proposal.Suggested));
        Assert.DoesNotContain("Already planned", Titles(proposal.LeftOut));
    }

    [Fact]
    public async Task Lowering_the_hours_available_shrinks_the_proposal()
    {
        // This is where meeting time comes off the day until the calendar can be read.
        using TestHost host = new(Monday9am);
        for (int number = 1; number <= 10; number++)
        {
            await AddAsync(host, $"Task {number}", hours: 1);
        }

        DayProposal proposal = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 4));

        Assert.Equal(4, proposal.HoursAvailable);
        Assert.Equal(4, proposal.SuggestedHours);
    }

    [Fact]
    public async Task Higher_priorities_are_suggested_first()
    {
        using TestHost host = new(Monday9am);
        await AddAsync(host, "Medium", TaskPriority.Medium, 3);
        await AddAsync(host, "High", TaskPriority.High, 3);
        await AddAsync(host, "Very high", TaskPriority.VeryHigh, 3);
        await AddAsync(host, "Another medium", TaskPriority.Medium, 3);

        DayProposal proposal = await host.Suggestions.SuggestAsync(NoFocus);

        Assert.Equal(new[] { "Very high", "High", "Medium" }, Titles(proposal.Suggested));
        Assert.Equal(new[] { "Another medium" }, Titles(proposal.LeftOut));
    }

    [Fact]
    public async Task One_low_task_is_included_even_when_higher_priorities_could_fill_the_day()
    {
        // Low is where ideas live; without a reserved place they would never surface.
        using TestHost host = new(Monday9am);
        for (int number = 1; number <= 12; number++)
        {
            await AddAsync(host, $"High {number}", TaskPriority.High, 1);
        }

        await AddAsync(host, "Idea one", TaskPriority.Low, 1);
        await AddAsync(host, "Idea two", TaskPriority.Low, 1);

        DayProposal proposal = await host.Suggestions.SuggestAsync(NoFocus);

        SuggestedTask low = Assert.Single(proposal.Suggested, s => s.Task.Priority == TaskPriority.Low);
        Assert.Equal("Low pick for today", low.Reason);
        Assert.Equal(9, proposal.SuggestedHours);
    }

    [Fact]
    public async Task No_extra_low_task_is_reserved_when_one_is_already_planned_for_today()
    {
        using TestHost host = new(Monday9am);
        await AddAsync(host, "Idea already planned", TaskPriority.Low, 1, planForToday: true);
        await AddAsync(host, "Another idea", TaskPriority.Low, 1);
        for (int number = 1; number <= 12; number++)
        {
            await AddAsync(host, $"High {number}", TaskPriority.High, 1);
        }

        DayProposal proposal = await host.Suggestions.SuggestAsync(NoFocus);

        Assert.DoesNotContain(proposal.Suggested, s => s.Task.Priority == TaskPriority.Low);
    }

    [Fact]
    public async Task The_low_pick_changes_from_day_to_day_but_not_within_a_day()
    {
        // Random, so the oldest idea is not the only one ever shown; stable within a day, so
        // the proposal does not reshuffle under me while I adjust it.
        using TestHost host = new(Monday9am);
        for (int number = 1; number <= 6; number++)
        {
            await AddAsync(host, $"Idea {number}", TaskPriority.Low, 1);
        }

        HashSet<string> picksAcrossDays = new();
        for (int day = 0; day < 14; day++)
        {
            host.Time.SetLocal(Monday9am.AddDays(day));
            DayProposal first = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 1));
            DayProposal second = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 1));
            string pick = Assert.Single(Titles(first.Suggested));
            Assert.Equal(pick, Assert.Single(Titles(second.Suggested)));
            picksAcrossDays.Add(pick);
        }

        Assert.True(picksAcrossDays.Count >= 3, $"Only {picksAcrossDays.Count} different ideas were picked in two weeks.");
    }

    [Fact]
    public async Task Focus_projects_get_their_share_of_the_day_and_other_projects_are_still_sprinkled_in()
    {
        using TestHost host = new(Monday9am);
        Project focus = await host.Projects.AddAsync("Focus");
        Project other = await host.Projects.AddAsync("Other");
        for (int number = 1; number <= 12; number++)
        {
            await AddAsync(host, $"Focus {number}", hours: 1, projectId: focus.Id);
            await AddAsync(host, $"Other {number}", hours: 1, projectId: other.Id);
        }

        DayProposal proposal = await host.Suggestions.SuggestAsync(new SuggestionRequest(new[] { focus.Id }, 10));

        Assert.Equal(7, proposal.Suggested.Where(s => s.IsFocus).Sum(s => s.Hours));
        Assert.Equal(3, proposal.Suggested.Where(s => !s.IsFocus).Sum(s => s.Hours));
        Assert.All(proposal.Suggested.Where(s => s.IsFocus), s => Assert.Contains("focus project", s.Reason));
    }

    [Fact]
    public async Task When_whole_tasks_do_not_split_at_the_share_the_remainder_leans_towards_the_focus_projects()
    {
        // Four hours at 70% is 2.8 and 1.2: two focus tasks and one other fit, leaving one hour.
        // Giving that hour to another project would make the day 50/50, the opposite of a focus day.
        using TestHost host = new(Monday9am);
        Project focus = await host.Projects.AddAsync("Focus");
        Project other = await host.Projects.AddAsync("Other");
        for (int number = 1; number <= 5; number++)
        {
            await AddAsync(host, $"Other {number}", hours: 1, projectId: other.Id);
            await AddAsync(host, $"Focus {number}", hours: 1, projectId: focus.Id);
        }

        DayProposal proposal = await host.Suggestions.SuggestAsync(new SuggestionRequest(new[] { focus.Id }, 4));

        Assert.Equal(3, proposal.Suggested.Count(s => s.IsFocus));
        Assert.Equal(1, proposal.Suggested.Count(s => !s.IsFocus));
    }

    [Fact]
    public async Task Very_high_work_outside_the_focus_projects_is_not_crowded_out_by_lesser_focus_work()
    {
        // Focus decides where ordinary work comes from; it must not hide something Very High in another project.
        using TestHost host = new(Monday9am);
        Project focus = await host.Projects.AddAsync("Focus");
        Project other = await host.Projects.AddAsync("Other");
        await AddAsync(host, "Very high elsewhere", TaskPriority.VeryHigh, 3, other.Id);
        await AddAsync(host, "Idea", TaskPriority.Low, 0.5, other.Id);
        for (int number = 1; number <= 8; number++)
        {
            await AddAsync(host, $"Focus medium {number}", TaskPriority.Medium, 1, focus.Id);
        }

        DayProposal proposal = await host.Suggestions.SuggestAsync(new SuggestionRequest(new[] { focus.Id }, 6));

        // 3 h Very High, then two 1 h focus tasks, then the 0.5 h Low pick; the last half hour fits nothing.
        Assert.Equal("Very high elsewhere", proposal.Suggested[0].Task.Title);
        Assert.Equal(2, proposal.Suggested.Count(s => s.IsFocus));
        Assert.Equal(5.5, proposal.SuggestedHours);
        Assert.False(proposal.IsOverAvailable);
    }

    [Fact]
    public async Task The_focus_share_follows_the_setting()
    {
        using TestHost host = new(Monday9am);
        await host.Settings.UpdateSuggestionSettingsAsync(50, 1, 3);
        Project focus = await host.Projects.AddAsync("Focus");
        Project other = await host.Projects.AddAsync("Other");
        for (int number = 1; number <= 12; number++)
        {
            await AddAsync(host, $"Focus {number}", hours: 1, projectId: focus.Id);
            await AddAsync(host, $"Other {number}", hours: 1, projectId: other.Id);
        }

        DayProposal proposal = await host.Suggestions.SuggestAsync(new SuggestionRequest(new[] { focus.Id }, 10));

        Assert.Equal(5, proposal.Suggested.Where(s => s.IsFocus).Sum(s => s.Hours));
    }

    [Fact]
    public async Task Room_the_focus_projects_cannot_use_goes_to_other_work_instead_of_being_left_empty()
    {
        using TestHost host = new(Monday9am);
        Project focus = await host.Projects.AddAsync("Focus");
        Project other = await host.Projects.AddAsync("Other");
        await AddAsync(host, "The only focus task", hours: 2, projectId: focus.Id);
        for (int number = 1; number <= 12; number++)
        {
            await AddAsync(host, $"Other {number}", hours: 1, projectId: other.Id);
        }

        DayProposal proposal = await host.Suggestions.SuggestAsync(new SuggestionRequest(new[] { focus.Id }, 10));

        Assert.Equal(10, proposal.SuggestedHours);
        Assert.Contains("The only focus task", Titles(proposal.Suggested));
    }

    [Fact]
    public async Task Critical_and_overdue_tasks_are_suggested_even_when_they_take_the_day_past_the_hours_available()
    {
        // Hiding a Critical task because the day looks full would defeat the point of the priority.
        using TestHost host = new(Monday9am);
        await AddAsync(host, "Critical thing", TaskPriority.Critical, 8);
        await AddAsync(host, "Overdue thing", TaskPriority.Medium, 4, due: Monday.AddDays(-3));
        await AddAsync(host, "Due today thing", TaskPriority.Medium, 1, due: Monday);
        await AddAsync(host, "Ordinary thing", TaskPriority.High, 1);

        DayProposal proposal = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 6));

        Assert.Equal(new[] { "Critical thing", "Overdue thing", "Due today thing" }, Titles(proposal.Suggested));
        Assert.True(proposal.IsOverAvailable);
        Assert.Contains("overdue since Fri 13 Nov", proposal.Suggested[1].Reason);
        Assert.Equal("due today", proposal.Suggested[2].Reason);
    }

    [Fact]
    public async Task Carried_over_and_in_progress_tasks_are_suggested_with_the_reason_shown()
    {
        using TestHost host = new(Monday9am);
        WorkTask carried = await AddAsync(host, "Left from Monday", hours: 1, planForToday: true);
        WorkTask started = await AddAsync(host, "Half done", hours: 1);
        await host.Tasks.StartAsync(started.Id);
        await AddAsync(host, "Fresh task", TaskPriority.VeryHigh, 1);
        host.Time.SetLocal(Monday9am.AddDays(1));

        DayProposal proposal = await host.Suggestions.SuggestAsync(NoFocus);

        SuggestedTask carriedOver = proposal.Suggested.Single(s => s.Task.Id == carried.Id);
        Assert.Equal("carried over from Mon 16 Nov", carriedOver.Reason);
        Assert.Equal("already in progress", proposal.Suggested.Single(s => s.Task.Id == started.Id).Reason);
        Assert.True(proposal.Suggested.ToList().IndexOf(carriedOver) < proposal.Suggested.ToList().FindIndex(s => s.Task.Title == "Fresh task"));
    }

    [Fact]
    public async Task A_task_due_within_the_window_is_pulled_ahead_of_an_undated_task_one_priority_higher()
    {
        using TestHost host = new(Monday9am);
        await AddAsync(host, "High, no date", TaskPriority.High, 1);
        await AddAsync(host, "Medium, due Wednesday", TaskPriority.Medium, 1, due: Monday.AddDays(2));

        DayProposal proposal = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 1));

        SuggestedTask suggested = Assert.Single(proposal.Suggested);
        Assert.Equal("Medium, due Wednesday", suggested.Task.Title);
        Assert.Contains("due Wed 18 Nov", suggested.Reason);
    }

    [Fact]
    public async Task A_task_due_beyond_the_window_gets_no_boost_and_weekends_do_not_count_towards_it()
    {
        using TestHost host = new(new DateTime(2026, 11, 20, 9, 0, 0));
        DateOnly friday = new(2026, 11, 20);
        await AddAsync(host, "High, no date", TaskPriority.High, 1);
        await AddAsync(host, "Medium, due in two weeks", TaskPriority.Medium, 1, due: friday.AddDays(14));
        DayProposal farOff = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 1));

        // Friday to the following Wednesday is five calendar days but three working days.
        await AddAsync(host, "Medium, due next Wednesday", TaskPriority.Medium, 1, due: friday.AddDays(5));
        DayProposal acrossWeekend = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 1));

        Assert.Equal("High, no date", Assert.Single(farOff.Suggested).Task.Title);
        Assert.Equal("Medium, due next Wednesday", Assert.Single(acrossWeekend.Suggested).Task.Title);
    }

    [Fact]
    public async Task An_unsized_task_counts_as_the_assumed_size_and_is_marked_as_assumed()
    {
        // Most imported tasks have no size; counting them as nothing would overfill every day.
        using TestHost host = new(Monday9am);
        await AddAsync(host, "Unsized");
        await AddAsync(host, "Sized", hours: 2);
        DayProposal withDefault = await host.Suggestions.SuggestAsync(NoFocus);

        await host.Settings.UpdateSuggestionSettingsAsync(70, 2.5, 3);
        DayProposal withSetting = await host.Suggestions.SuggestAsync(NoFocus);

        SuggestedTask unsized = withDefault.Suggested.Single(s => s.Task.Title == "Unsized");
        Assert.True(unsized.IsAssumedSize);
        Assert.Equal(1, unsized.Hours);
        Assert.False(withDefault.Suggested.Single(s => s.Task.Title == "Sized").IsAssumedSize);
        Assert.Equal(2.5, withSetting.Suggested.Single(s => s.Task.Title == "Unsized").Hours);
    }

    [Fact]
    public async Task Asking_for_a_suggestion_changes_nothing()
    {
        // A proposal is something to look at; only accepting it may plan work or write history.
        using TestHost host = new(Monday9am);
        await AddAsync(host, "First", TaskPriority.Critical, 1);
        await AddAsync(host, "Second", hours: 1);

        await host.Suggestions.SuggestAsync(NoFocus);

        using AppDbContext fresh = host.NewContext();
        Assert.All(await fresh.Tasks.ToListAsync(), task => Assert.Null(task.PlannedFor));
        Assert.Equal(0, await fresh.TaskFieldChanges.CountAsync());
    }

    [Fact]
    public async Task Hours_available_outside_a_day_are_rejected()
    {
        using TestHost host = new(Monday9am);

        await Assert.ThrowsAsync<DomainRuleException>(() => host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 25)));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), -1)));
    }

    [Fact]
    public async Task Accepting_a_proposal_plans_every_task_in_one_save_with_history_for_each()
    {
        using TestHost host = new(Monday9am);
        WorkTask first = await AddAsync(host, "First");
        WorkTask second = await AddAsync(host, "Second");
        WorkTask alreadyPlanned = await AddAsync(host, "Already planned", planForToday: true);

        int planned = await host.Tasks.PlanManyForTodayAsync(new[] { first.Id, second.Id, alreadyPlanned.Id });

        Assert.Equal(2, planned);
        using AppDbContext fresh = host.NewContext();
        Assert.All(await fresh.Tasks.ToListAsync(), task => Assert.Equal(Monday, task.PlannedFor));
        Assert.Equal(3, await fresh.TaskFieldChanges.CountAsync(c => c.Field == TaskField.PlannedFor));
    }

    [Fact]
    public async Task Accepting_a_proposal_that_contains_a_task_closed_since_plans_nothing_and_names_it()
    {
        // The proposal may have sat on screen while a task was finished elsewhere; planning the rest
        // silently would leave me unsure what the day now holds.
        using TestHost host = new(Monday9am);
        WorkTask open = await AddAsync(host, "Still open");
        WorkTask closed = await AddAsync(host, "Finished meanwhile");
        await host.Tasks.CompleteAsync(closed.Id);

        DomainRuleException error = await Assert.ThrowsAsync<DomainRuleException>(() => host.Tasks.PlanManyForTodayAsync(new[] { open.Id, closed.Id }));

        Assert.Contains("Finished meanwhile", error.Message);
        using AppDbContext fresh = host.NewContext();
        Assert.Null((await fresh.Tasks.SingleAsync(t => t.Id == open.Id)).PlannedFor);
    }

    [Theory]
    [InlineData(101, 1, 3)]
    [InlineData(-1, 1, 3)]
    [InlineData(70, 0.1, 3)]
    [InlineData(70, 1, 31)]
    public async Task Suggestion_settings_outside_their_ranges_are_rejected(int focusShare, double assumedHours, int dueSoonDays)
    {
        using TestHost host = new(Monday9am);

        await Assert.ThrowsAsync<DomainRuleException>(() => host.Settings.UpdateSuggestionSettingsAsync(focusShare, assumedHours, dueSoonDays));
    }

    [Fact]
    public async Task Accepting_a_proposal_plans_the_tasks_and_records_what_the_day_was_about()
    {
        // Hours available per day is the capacity a later burndown needs, and it cannot be worked out afterwards.
        using TestHost host = new(Monday9am);
        Project focus = await host.Projects.AddAsync("Focus");
        WorkTask kept = await AddAsync(host, "Kept", projectId: focus.Id);
        WorkTask removed = await AddAsync(host, "Removed", projectId: focus.Id);
        WorkTask swappedIn = await AddAsync(host, "Swapped in");

        int planned = await host.Suggestions.AcceptAsync(new AcceptRequest(
            new[] { kept.Id, swappedIn.Id },
            new[] { kept.Id, removed.Id },
            new[] { focus.Id, BacklogFilter.UnfiledProjectId },
            6.5));

        Assert.Equal(2, planned);
        using AppDbContext fresh = host.NewContext();
        DayPlan plan = await fresh.DayPlans.Include(p => p.FocusProjects).SingleAsync();
        Assert.Equal(Monday, plan.Date);
        Assert.Equal(new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc), plan.AcceptedAtUtc);
        Assert.Equal(6.5, plan.HoursAvailable);
        Assert.Equal(2, plan.TasksSuggested);
        Assert.Equal(1, plan.TasksAcceptedFromSuggestion);
        Assert.Equal(1, plan.TasksSwappedIn);
        Assert.Equal(new int?[] { null, focus.Id }, plan.FocusProjects.Select(f => f.ProjectId).OrderBy(id => id));
        Assert.Null((await fresh.Tasks.SingleAsync(t => t.Id == removed.Id)).PlannedFor);
    }

    [Fact]
    public async Task A_rejected_acceptance_leaves_no_day_plan_behind()
    {
        // A recorded plan for a day on which nothing was planned would be a false capacity figure.
        using TestHost host = new(Monday9am);
        WorkTask open = await AddAsync(host, "Still open");
        WorkTask closed = await AddAsync(host, "Finished meanwhile");
        await host.Tasks.CompleteAsync(closed.Id);

        await Assert.ThrowsAsync<DomainRuleException>(() => host.Suggestions.AcceptAsync(
            new AcceptRequest(new[] { open.Id, closed.Id }, new[] { open.Id, closed.Id }, Array.Empty<int>(), 6)));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.Suggestions.AcceptAsync(
            new AcceptRequest(new[] { open.Id }, new[] { open.Id }, new[] { 999 }, 6)));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.Suggestions.AcceptAsync(
            new AcceptRequest(new[] { open.Id }, new[] { open.Id }, Array.Empty<int>(), 30)));

        using AppDbContext fresh = host.NewContext();
        Assert.Equal(0, await fresh.DayPlans.CountAsync());
        Assert.Null((await fresh.Tasks.SingleAsync(t => t.Id == open.Id)).PlannedFor);
    }

    [Fact]
    public async Task Today_measures_against_the_hours_entered_for_the_latest_accepted_plan()
    {
        // On a meeting-heavy day I lower the hours; Today must not go back to judging the day against the usual target.
        using TestHost host = new(Monday9am);
        WorkTask first = await AddAsync(host, "First", hours: 3);
        WorkTask second = await AddAsync(host, "Second", hours: 3);
        TodayView before = await host.Tasks.GetTodayAsync();

        await host.Suggestions.AcceptAsync(new AcceptRequest(new[] { first.Id }, new[] { first.Id }, Array.Empty<int>(), 5));
        TodayView afterFirst = await host.Tasks.GetTodayAsync();
        await host.Suggestions.AcceptAsync(new AcceptRequest(new[] { second.Id }, new[] { second.Id }, Array.Empty<int>(), 4));
        TodayView afterTopUp = await host.Tasks.GetTodayAsync();
        host.Time.SetLocal(Monday9am.AddDays(1));
        TodayView nextDay = await host.Tasks.GetTodayAsync();

        Assert.Equal(9, before.TargetHours);
        Assert.False(before.IsTargetFromDayPlan);
        Assert.Equal(5, afterFirst.TargetHours);
        Assert.True(afterFirst.IsTargetFromDayPlan);
        Assert.Equal(4, afterTopUp.TargetHours);
        Assert.True(afterTopUp.IsOverTarget);
        Assert.Equal(9, nextDay.TargetHours);
        Assert.False(nextDay.IsTargetFromDayPlan);
    }

    [Fact]
    public async Task The_low_pick_stays_put_when_other_low_tasks_are_finished_or_added_during_the_day()
    {
        // I adjust a proposal over several minutes while also clearing and capturing tasks;
        // the idea I was shown must not be swapped for another one underneath me.
        using TestHost host = new(Monday9am.AddDays(-3));
        List<WorkTask> ideas = new();
        for (int number = 1; number <= 8; number++)
        {
            ideas.Add(await AddAsync(host, $"Idea {number}", TaskPriority.Low, 1));
        }

        host.Time.SetLocal(Monday9am);
        SuggestionRequest roomForOne = new(Array.Empty<int>(), 1);
        string pick = Assert.Single(Titles((await host.Suggestions.SuggestAsync(roomForOne)).Suggested));

        foreach (WorkTask other in ideas.Where(idea => idea.Title != pick).Take(3))
        {
            await host.Tasks.CompleteAsync(other.Id);
        }

        for (int number = 1; number <= 20; number++)
        {
            await AddAsync(host, $"New idea {number}", TaskPriority.Low, 1);
        }

        Assert.Equal(pick, Assert.Single(Titles((await host.Suggestions.SuggestAsync(roomForOne)).Suggested)));
    }

    [Fact]
    public async Task No_low_task_is_picked_when_must_do_work_already_fills_the_day()
    {
        // An idea on top of a day that is already over only makes the overrun worse.
        using TestHost host = new(Monday9am);
        await AddAsync(host, "Critical thing", TaskPriority.Critical, 6);
        await AddAsync(host, "Idea", TaskPriority.Low, 0.5);
        await AddAsync(host, "Ordinary thing", TaskPriority.High, 1);

        DayProposal full = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 6));
        DayProposal withRoom = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 6.5));

        Assert.Equal(new[] { "Critical thing" }, Titles(full.Suggested));
        Assert.Contains("Idea", Titles(withRoom.Suggested));
    }

    [Fact]
    public async Task On_an_over_full_day_must_do_work_is_all_shown_most_pressing_first_with_the_overflow_marked()
    {
        // Everything pressing stays visible, because hiding it would hide how far behind I am;
        // the marks show where today's hours run out so I can decide what slips.
        using TestHost host = new(Monday9am);
        WorkTask carried = await AddAsync(host, "Carried over", hours: 2, planForToday: true);
        host.Time.SetLocal(Monday9am.AddDays(1));
        DateOnly tuesday = Monday.AddDays(1);
        WorkTask started = await AddAsync(host, "In progress", hours: 2);
        await host.Tasks.StartAsync(started.Id);
        await AddAsync(host, "Due today", hours: 2, due: tuesday);
        await AddAsync(host, "Overdue", hours: 2, due: tuesday.AddDays(-5));
        await AddAsync(host, "Critical", TaskPriority.Critical, 2);
        await AddAsync(host, "Ordinary high", TaskPriority.High, 0.5);

        DayProposal proposal = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 5));

        Assert.Equal(new[] { "Critical", "Overdue", "Due today", "In progress", "Carried over" }, Titles(proposal.MustInclude));
        Assert.Equal(new[] { false, false, true, true, true }, proposal.MustInclude.Select(s => s.IsBeyondHours));
        Assert.Empty(proposal.Optional);
        Assert.Equal(5, proposal.MustIncludeOverHours);
        Assert.True(proposal.IsOverAvailable);
        Assert.Equal(carried.Id, proposal.MustInclude.Last().Task.Id);
    }

    [Fact]
    public async Task Must_do_work_that_fits_is_not_marked_and_leaves_room_for_the_rest()
    {
        using TestHost host = new(Monday9am);
        await AddAsync(host, "Critical", TaskPriority.Critical, 2);
        await AddAsync(host, "Ordinary high", TaskPriority.High, 1);

        DayProposal proposal = await host.Suggestions.SuggestAsync(new SuggestionRequest(Array.Empty<int>(), 5));

        Assert.All(proposal.MustInclude, s => Assert.False(s.IsBeyondHours));
        Assert.Equal(0, proposal.MustIncludeOverHours);
        Assert.Equal(new[] { "Ordinary high" }, Titles(proposal.Optional));
    }
}
