namespace TaskOrganizer.Web.Domain;

public class WorkTask
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Null means the task has not been filed under a project yet.</summary>
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public DateOnly? DueDate { get; set; }
    public WorkTaskStatus Status { get; set; } = WorkTaskStatus.ToDo;
    public TaskSource Source { get; set; } = TaskSource.Manual;

    /// <summary>Rough size in hours, in 15-minute steps. Null means not sized.</summary>
    public double? EstimateHours { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? DroppedAtUtc { get; set; }

    /// <summary>The day this task is planned for. Null means it sits in the backlog only.</summary>
    public DateOnly? PlannedFor { get; set; }

    /// <summary>Identifies the spreadsheet row a task came from, so re-running the import adds nothing.</summary>
    public string? ImportKey { get; set; }

    public bool IsOpen => Status is WorkTaskStatus.ToDo or WorkTaskStatus.InProgress;
}
