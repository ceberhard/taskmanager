using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

/// <summary>The fields a person can set on a task. Timestamps, status and source are never taken from input.</summary>
public record TaskDetails(string? Title, int? ProjectId, TaskPriority Priority, DateOnly? DueDate, double? EstimateHours);

public record TodayView(
    DateOnly Date,
    IReadOnlyList<WorkTask> Planned,
    IReadOnlyList<WorkTask> CarriedOver,
    IReadOnlyList<WorkTask> CompletedToday,
    double PlannedHours,
    int UnsizedCount,
    double TargetHours)
{
    public bool IsOverTarget => PlannedHours > TargetHours;
}

public record ProjectBacklog(Project? Project, IReadOnlyList<WorkTask> Tasks, PriorityCounts Counts)
{
    public string Name => Project?.Name ?? "Unfiled";
}

public record BacklogView(IReadOnlyList<ProjectBacklog> Groups, PriorityCounts Overall);

/// <summary>How many open tasks sit at each priority: the "priority surface".</summary>
public record PriorityCounts(IReadOnlyDictionary<TaskPriority, int> ByPriority)
{
    public int Total => ByPriority.Values.Sum();

    public int For(TaskPriority priority) => ByPriority.GetValueOrDefault(priority);

    public static PriorityCounts From(IEnumerable<WorkTask> tasks)
    {
        Dictionary<TaskPriority, int> counts = tasks
            .GroupBy(t => t.Priority)
            .ToDictionary(g => g.Key, g => g.Count());
        return new PriorityCounts(counts);
    }
}
