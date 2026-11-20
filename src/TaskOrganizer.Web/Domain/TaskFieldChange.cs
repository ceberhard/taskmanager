namespace TaskOrganizer.Web.Domain;

public enum TaskField
{
    EstimateHours = 0,
    Priority = 1,
    PlannedFor = 2
}

/// <summary>
/// Append-only record of a change to a task's size, priority or planned day.
/// The task row only holds the current value; reports need this to see re-estimates
/// (scope growth) and how often a task was re-planned. Values are stored as text:
/// hours as an invariant number, priority by name, planned day as yyyy-MM-dd.
/// A task's values at creation are not recorded here, except a planned day, because
/// planning is an event in itself; the original size is the OldValue of the first change.
/// </summary>
public class TaskFieldChange
{
    public int Id { get; set; }
    public int WorkTaskId { get; set; }
    public WorkTask? WorkTask { get; set; }
    public TaskField Field { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime ChangedAtUtc { get; set; }
}
