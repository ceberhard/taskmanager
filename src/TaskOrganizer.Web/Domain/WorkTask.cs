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

    /// <summary>Where the task came from outside the app (a Jira ticket, pull request or Slack message), as a web link.</summary>
    public string? ExternalUrl { get; set; }

    /// <summary>
    /// Identifies the outside item a task came from (a spreadsheet row, "jira:ROC-123",
    /// "ado-pr:26512", "slack:C123/1700000000.123"), so the same item never becomes two tasks.
    /// </summary>
    public string? ImportKey { get; set; }

    /// <summary>Why the task is on hold, in the person's own words. Null unless the status is OnHold.</summary>
    public string? HoldReason { get; set; }

    /// <summary>Optional day on which an on-hold task should be brought up for follow-up.</summary>
    public DateOnly? HoldCheckBackOn { get; set; }

    /// <summary>When the task was marked done for today. Becomes the completion time once it is confirmed.</summary>
    public DateTime? DoneForTodayAtUtc { get; set; }

    /// <summary>When a done-for-today task was signed off as Completed. Null for tasks completed outright.</summary>
    public DateTime? ConfirmedAtUtc { get; set; }

    // The three questions every list and rule asks about a task's state are answered here, once.

    /// <summary>Finished with for good: Completed or Dropped.</summary>
    public bool IsClosed => Status is WorkTaskStatus.Completed or WorkTaskStatus.Dropped;

    /// <summary>Not closed. Includes tasks on hold and tasks done for today awaiting sign-off.</summary>
    public bool IsOpen => !IsClosed;

    /// <summary>Can be worked on now, so it can be planned, started and suggested: ToDo or InProgress.</summary>
    public bool IsWorkable => Status is WorkTaskStatus.ToDo or WorkTaskStatus.InProgress;
}
