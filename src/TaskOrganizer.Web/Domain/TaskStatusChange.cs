namespace TaskOrganizer.Web.Domain;

/// <summary>
/// Append-only record of every status change. Reopening a task clears its completed
/// timestamp, so this history is what keeps the burndown reports honest.
/// </summary>
public class TaskStatusChange
{
    public int Id { get; set; }
    public int WorkTaskId { get; set; }
    public WorkTask? WorkTask { get; set; }

    /// <summary>Null for the entry written when the task is created.</summary>
    public WorkTaskStatus? FromStatus { get; set; }
    public WorkTaskStatus ToStatus { get; set; }
    public DateTime ChangedAtUtc { get; set; }

    /// <summary>The reason given when a task was put on hold or its hold was updated. Null otherwise.</summary>
    public string? Note { get; set; }
}
