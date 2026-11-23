using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

/// <summary>One line of a task's history, whichever table it came from.</summary>
public record HistoryEvent(DateTime LocalTime, string What, string? Note);

/// <summary>Everything recorded about one task: status changes with their reasons, and changes to size, priority and planned day.</summary>
public class HistoryModel : PageModel
{
    private readonly TaskService _tasks;
    private readonly LocalClock _clock;

    public HistoryModel(TaskService tasks, LocalClock clock)
    {
        _tasks = tasks;
        _clock = clock;
    }

    public WorkTask Item { get; private set; } = null!;
    public IReadOnlyList<HistoryEvent> Events { get; private set; } = Array.Empty<HistoryEvent>();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        WorkTask? task = await _tasks.FindWithProjectAsync(id);
        if (task is null)
        {
            return NotFound();
        }

        Item = task;
        List<TaskStatusChange> statusChanges = await _tasks.GetStatusHistoryAsync(id);
        List<TaskFieldChange> fieldChanges = await _tasks.GetFieldHistoryAsync(id);

        Events = statusChanges
            .Select(change => (change.ChangedAtUtc, Event: new HistoryEvent(_clock.ToLocal(change.ChangedAtUtc), DescribeStatus(change), change.Note)))
            .Concat(fieldChanges.Select(change => (change.ChangedAtUtc, Event: new HistoryEvent(_clock.ToLocal(change.ChangedAtUtc), DescribeField(change), null))))
            .OrderBy(entry => entry.ChangedAtUtc)
            .Select(entry => entry.Event)
            .ToList();
        return Page();
    }

    private static string DescribeStatus(TaskStatusChange change)
    {
        if (change.FromStatus is not WorkTaskStatus from)
        {
            return $"Created as {Display.Status(change.ToStatus)}";
        }

        return from == change.ToStatus
            ? $"{Display.Status(change.ToStatus)}: reason changed"
            : $"{Display.Status(from)} → {Display.Status(change.ToStatus)}";
    }

    private static string DescribeField(TaskFieldChange change)
    {
        string field = change.Field switch
        {
            TaskField.EstimateHours => "Size (hours)",
            TaskField.PlannedFor => "Planned for",
            _ => change.Field.ToString()
        };
        return $"{field}: {change.OldValue ?? "none"} → {change.NewValue ?? "none"}";
    }
}
