using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Tests;

/// <summary>Mass edit and the backlog filter.</summary>
public class BacklogPlanningTests
{
    private static readonly DateTime Monday9am = new(2026, 11, 16, 9, 0, 0);

    private static TaskDetails Details(string title, TaskPriority priority = TaskPriority.Medium, double? hours = null, int? projectId = null)
    {
        return new TaskDetails(title, projectId, priority, null, hours);
    }

    [Fact]
    public async Task Mass_edit_saves_priority_date_and_hours_on_several_tasks_at_once()
    {
        using TestHost host = new(Monday9am);
        WorkTask first = await host.Tasks.AddAsync(Details("First"));
        WorkTask second = await host.Tasks.AddAsync(Details("Second"));
        WorkTask untouched = await host.Tasks.AddAsync(Details("Untouched", TaskPriority.Low, 1));

        int changed = await host.Tasks.UpdatePlanningAsync(new List<TaskPlanningEdit>
        {
            new(first.Id, TaskPriority.High, new DateOnly(2026, 11, 20), 2),
            new(second.Id, TaskPriority.Critical, null, 0.5),
            new(untouched.Id, TaskPriority.Low, null, 1)
        });

        Assert.Equal(2, changed);
        using AppDbContext fresh = host.NewContext();
        WorkTask savedFirst = await fresh.Tasks.SingleAsync(t => t.Id == first.Id);
        Assert.Equal(TaskPriority.High, savedFirst.Priority);
        Assert.Equal(new DateOnly(2026, 11, 20), savedFirst.DueDate);
        Assert.Equal(2, savedFirst.EstimateHours);
        WorkTask savedSecond = await fresh.Tasks.SingleAsync(t => t.Id == second.Id);
        Assert.Equal(TaskPriority.Critical, savedSecond.Priority);
        Assert.Equal(0.5, savedSecond.EstimateHours);
    }

    [Fact]
    public async Task Mass_edit_leaves_the_same_history_as_editing_each_task_singly()
    {
        // A re-estimation session is exactly when scope growth happens; if bulk saves skipped the history the burndown would miss it.
        using TestHost host = new(Monday9am);
        WorkTask resized = await host.Tasks.AddAsync(Details("Resized", hours: 2));
        WorkTask reprioritised = await host.Tasks.AddAsync(Details("Reprioritised"));
        WorkTask onlyDated = await host.Tasks.AddAsync(Details("Only dated", hours: 1));

        await host.Tasks.UpdatePlanningAsync(new List<TaskPlanningEdit>
        {
            new(resized.Id, TaskPriority.Medium, null, 8),
            new(reprioritised.Id, TaskPriority.VeryHigh, null, null),
            new(onlyDated.Id, TaskPriority.Medium, new DateOnly(2026, 12, 1), 1)
        });

        using AppDbContext fresh = host.NewContext();
        List<TaskFieldChange> history = await fresh.TaskFieldChanges.OrderBy(c => c.WorkTaskId).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal((resized.Id, TaskField.EstimateHours, "2", "8"), (history[0].WorkTaskId, history[0].Field, history[0].OldValue, history[0].NewValue));
        Assert.Equal((reprioritised.Id, TaskField.Priority, "Medium", "VeryHigh"), (history[1].WorkTaskId, history[1].Field, history[1].OldValue, history[1].NewValue));
    }

    [Fact]
    public async Task One_invalid_row_stops_the_whole_mass_edit_and_the_message_names_the_task()
    {
        // A half-applied save would leave no way to tell which rows took; all or nothing keeps the screen and the data in step.
        using TestHost host = new(Monday9am);
        WorkTask good = await host.Tasks.AddAsync(Details("Good row"));
        WorkTask bad = await host.Tasks.AddAsync(Details("Bad row"));

        DomainRuleException error = await Assert.ThrowsAsync<DomainRuleException>(() => host.Tasks.UpdatePlanningAsync(new List<TaskPlanningEdit>
        {
            new(good.Id, TaskPriority.High, null, 2),
            new(bad.Id, TaskPriority.High, null, 0.1)
        }));

        Assert.Contains("Nothing was saved", error.Message);
        Assert.Contains("Bad row", error.Message);
        using AppDbContext fresh = host.NewContext();
        Assert.Equal(TaskPriority.Medium, (await fresh.Tasks.SingleAsync(t => t.Id == good.Id)).Priority);
        Assert.Equal(0, await fresh.TaskFieldChanges.CountAsync());
    }

    [Fact]
    public async Task The_filter_narrows_the_list_to_the_chosen_projects_and_priorities()
    {
        using TestHost host = new(Monday9am);
        Project alpha = await host.Projects.AddAsync("Alpha");
        Project beta = await host.Projects.AddAsync("Beta");
        await host.Tasks.AddAsync(Details("Alpha high", TaskPriority.High, projectId: alpha.Id));
        await host.Tasks.AddAsync(Details("Alpha low", TaskPriority.Low, projectId: alpha.Id));
        await host.Tasks.AddAsync(Details("Beta high", TaskPriority.High, projectId: beta.Id));
        await host.Tasks.AddAsync(Details("Unfiled high", TaskPriority.High));

        BacklogView alphaHigh = await host.Tasks.GetBacklogAsync(new BacklogFilter(new[] { alpha.Id }, new[] { TaskPriority.High }));
        BacklogView unfiledOnly = await host.Tasks.GetBacklogAsync(new BacklogFilter(new[] { BacklogFilter.UnfiledProjectId }, Array.Empty<TaskPriority>()));
        BacklogView allHigh = await host.Tasks.GetBacklogAsync(new BacklogFilter(Array.Empty<int>(), new[] { TaskPriority.High }));

        Assert.Equal(new[] { "Alpha high" }, alphaHigh.Groups.SelectMany(g => g.Tasks).Select(t => t.Title));
        Assert.Equal(new[] { "Unfiled high" }, unfiledOnly.Groups.SelectMany(g => g.Tasks).Select(t => t.Title));
        Assert.Equal(3, allHigh.ShownCount);
        Assert.True(alphaHigh.IsFiltered);
    }

    [Fact]
    public async Task The_priority_picture_stays_whole_while_the_list_is_filtered()
    {
        // The counts exist to show whether too much is marked High overall; a filter must not make that look better than it is.
        using TestHost host = new(Monday9am);
        Project alpha = await host.Projects.AddAsync("Alpha");
        Project beta = await host.Projects.AddAsync("Beta");
        await host.Tasks.AddAsync(Details("Alpha high", TaskPriority.High, projectId: alpha.Id));
        await host.Tasks.AddAsync(Details("Beta high", TaskPriority.High, projectId: beta.Id));
        await host.Tasks.AddAsync(Details("Beta low", TaskPriority.Low, projectId: beta.Id));

        BacklogView filtered = await host.Tasks.GetBacklogAsync(new BacklogFilter(new[] { alpha.Id }, Array.Empty<TaskPriority>()));

        Assert.Equal(1, filtered.ShownCount);
        Assert.Equal(3, filtered.Overall.Total);
        Assert.Equal(2, filtered.Overall.For(TaskPriority.High));
        ProjectPriorityCounts betaRow = filtered.Surface.Single(row => row.Project?.Id == beta.Id);
        Assert.Equal(2, betaRow.Counts.Total);
    }

    [Fact]
    public async Task Unfiltered_an_empty_project_is_still_listed_but_a_filter_hides_projects_with_nothing_to_show()
    {
        using TestHost host = new(Monday9am);
        Project alpha = await host.Projects.AddAsync("Alpha");
        await host.Projects.AddAsync("Empty");
        await host.Tasks.AddAsync(Details("Alpha task", TaskPriority.High, projectId: alpha.Id));

        BacklogView unfiltered = await host.Tasks.GetBacklogAsync();
        BacklogView filtered = await host.Tasks.GetBacklogAsync(new BacklogFilter(Array.Empty<int>(), new[] { TaskPriority.High }));

        Assert.Equal(new[] { "Alpha", "Empty" }, unfiltered.Groups.Select(g => g.Name));
        Assert.Equal(new[] { "Alpha" }, filtered.Groups.Select(g => g.Name));
    }
}
