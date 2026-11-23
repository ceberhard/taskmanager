using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

/// <summary>The one order in which open work is listed everywhere: Today, Backlog and suggestions.</summary>
public static class TaskOrdering
{
    /// <summary>Most urgent first, then nearest expected delivery date (undated last), then oldest.</summary>
    public static List<WorkTask> ForWork(IEnumerable<WorkTask> tasks)
    {
        return tasks
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.DueDate ?? DateOnly.MaxValue)
            .ThenBy(t => t.CreatedAtUtc)
            .ToList();
    }
}
