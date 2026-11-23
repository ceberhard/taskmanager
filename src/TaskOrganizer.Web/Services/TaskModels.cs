using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

/// <summary>The fields a person can set on a task. Timestamps, status and source are never taken from input.</summary>
public record TaskDetails(string? Title, int? ProjectId, TaskPriority Priority, DateOnly? DueDate, double? EstimateHours);

/// <summary>
/// What the Today page shows. PlannedHours includes AssumedHours, the part contributed by the
/// UnsizedCount tasks with no size. TargetHours is the hours entered for today's accepted
/// suggestion when there is one (IsTargetFromDayPlan), otherwise the daily plan target.
/// </summary>
public record TodayView(
    DateOnly Date,
    IReadOnlyList<WorkTask> Planned,
    IReadOnlyList<WorkTask> CarriedOver,
    IReadOnlyList<WorkTask> CompletedToday,
    double PlannedHours,
    int UnsizedCount,
    double AssumedHours,
    double TargetHours,
    bool IsTargetFromDayPlan)
{
    public bool IsOverTarget => HoursRules.Exceeds(PlannedHours, TargetHours);
}

public record ProjectBacklog(Project? Project, IReadOnlyList<WorkTask> Tasks, PriorityCounts Counts)
{
    public string Name => Project?.Name ?? "Unfiled";
}

/// <summary>
/// Groups holds the tasks that pass the filter; Overall always counts every open task,
/// so the priority picture stays whole while the list is narrowed.
/// </summary>
public record BacklogView(IReadOnlyList<ProjectBacklog> Groups, PriorityCounts Overall, IReadOnlyList<ProjectPriorityCounts> Surface, bool IsFiltered)
{
    public int ShownCount => Groups.Sum(g => g.Tasks.Count);
}

/// <summary>
/// Which slice of the backlog to show. An empty list means "no restriction" for that dimension.
/// </summary>
public record BacklogFilter(IReadOnlyCollection<int> ProjectIds, IReadOnlyCollection<TaskPriority> Priorities)
{
    /// <summary>Stands in for "no project" in a project filter, since real ids start at 1.</summary>
    public const int UnfiledProjectId = 0;

    public static readonly BacklogFilter None = new(Array.Empty<int>(), Array.Empty<TaskPriority>());

    public bool IsActive => ProjectIds.Count > 0 || Priorities.Count > 0;

    public bool IncludesProject(int? projectId) => ProjectIds.Count == 0 || ProjectIds.Contains(projectId ?? UnfiledProjectId);

    public bool Matches(WorkTask task)
    {
        return IncludesProject(task.ProjectId) && (Priorities.Count == 0 || Priorities.Contains(task.Priority));
    }
}

/// <summary>New values for the three planning fields of one task, as submitted by mass edit.</summary>
public record TaskPlanningEdit(int Id, TaskPriority Priority, DateOnly? DueDate, double? EstimateHours);

/// <summary>One project's row of the priority surface. A null project is the unfiled tasks.</summary>
public record ProjectPriorityCounts(Project? Project, PriorityCounts Counts)
{
    public string Name => Project?.Name ?? "Unfiled";
}

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
