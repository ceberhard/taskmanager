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
    Dropped = 3
}

public enum TaskSource
{
    Manual = 0,
    Slack = 1,
    Jira = 2,
    Meeting = 3,
    Interruption = 4,
    Import = 5
}
