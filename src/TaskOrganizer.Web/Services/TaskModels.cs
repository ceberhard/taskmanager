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

    /// <summary>Marked done for today, today. Still on show, with a finished look.</summary>
    public IReadOnlyList<WorkTask> DoneForToday { get; init; } = Array.Empty<WorkTask>();

    /// <summary>Marked done for today on an earlier day and not yet signed off, oldest first.</summary>
    public IReadOnlyList<WorkTask> AwaitingReview { get; init; } = Array.Empty<WorkTask>();

    /// <summary>On hold with a check-back day that has arrived, earliest first.</summary>
    public IReadOnlyList<WorkTask> FollowUps { get; init; } = Array.Empty<WorkTask>();

    /// <summary>On hold, part of a day's plan, and not yet due for follow-up.</summary>
    public IReadOnlyList<WorkTask> OnHold { get; init; } = Array.Empty<WorkTask>();

    /// <summary>How many tasks are in progress at once, across every project and day.</summary>
    public int InProgressCount { get; init; }
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
public record BacklogFilter(IReadOnlyCollection<int> ProjectIds, IReadOnlyCollection<TaskPriority> Priorities, IReadOnlyCollection<WorkTaskStatus>? Statuses = null)
{
    private IReadOnlyCollection<WorkTaskStatus> StatusList => Statuses ?? Array.Empty<WorkTaskStatus>();

    /// <summary>Stands in for "no project" in a project filter, since real ids start at 1.</summary>
    public const int UnfiledProjectId = 0;

    public static readonly BacklogFilter None = new(Array.Empty<int>(), Array.Empty<TaskPriority>());

    public bool IsActive => ProjectIds.Count > 0 || Priorities.Count > 0 || StatusList.Count > 0;

    public bool IncludesProject(int? projectId) => ProjectIds.Count == 0 || ProjectIds.Contains(projectId ?? UnfiledProjectId);

    public bool Matches(WorkTask task)
    {
        return IncludesProject(task.ProjectId)
            && (Priorities.Count == 0 || Priorities.Contains(task.Priority))
            && (StatusList.Count == 0 || StatusList.Contains(task.Status));
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
