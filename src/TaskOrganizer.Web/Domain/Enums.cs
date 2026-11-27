namespace TaskOrganizer.Web.Domain;

/// <summary>Lower number means more urgent, so sorting ascending puts Critical first.</summary>
public enum TaskPriority
{
    Critical = 1,
    VeryHigh = 2,
    High = 3,
    Medium = 4,
    Low = 5
}

public enum WorkTaskStatus
{
    ToDo = 0,
    InProgress = 1,
    Completed = 2,

    /// <summary>Closed without being done. Kept apart from Completed so reports never count it as delivered work.</summary>
    Dropped = 3,

    /// <summary>Parked with a reason, waiting on someone or something. Not finished and not being worked on.</summary>
    OnHold = 4,

    /// <summary>
    /// Marked finished for the day but not yet signed off. From the next day it is reviewed
    /// and becomes Completed, goes back into the day's work, or returns to the backlog.
    /// </summary>
    DoneForToday = 5
}

public enum TaskSource
{
    Manual = 0,
    Slack = 1,
    Jira = 2,
    Meeting = 3,
    Interruption = 4,
    Import = 5,
    PullRequest = 6
}
