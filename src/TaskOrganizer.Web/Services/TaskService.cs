using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

public class TaskService
{
    public const double EstimateStepHours = 0.25;

    private readonly AppDbContext _db;
    private readonly LocalClock _clock;
    private readonly SettingsService _settings;

    public TaskService(AppDbContext db, LocalClock clock, SettingsService settings)
    {
        _db = db;
        _clock = clock;
        _settings = settings;
    }

    public async Task<WorkTask> AddAsync(TaskDetails details, TaskSource source = TaskSource.Manual, bool planForToday = false)
    {
        WorkTask task = new()
        {
            Source = source,
            Status = WorkTaskStatus.ToDo,
            CreatedAtUtc = _clock.UtcNow,
            PlannedFor = planForToday ? _clock.Today : null
        };
        await ApplyDetailsAsync(task, details, recordChanges: false);

        _db.Tasks.Add(task);
        RecordChange(task, TaskField.PlannedFor, null, FormatDate(task.PlannedFor), task.CreatedAtUtc);
        _db.TaskStatusChanges.Add(new TaskStatusChange { WorkTask = task, FromStatus = null, ToStatus = task.Status, ChangedAtUtc = task.CreatedAtUtc });
        await _db.SaveChangesAsync();
        return task;
    }

    /// <summary>
    /// Records an interruption with no input at all: it is created now, planned for today,
    /// and given a placeholder title that can be edited once the interruption is dealt with.
    /// </summary>
    public async Task<WorkTask> AddInterruptionAsync(string? title = null)
    {
        string effectiveTitle = string.IsNullOrWhiteSpace(title)
            ? $"Interruption at {_clock.LocalNow:HH:mm}"
            : title;
        TaskDetails details = new(effectiveTitle, null, TaskPriority.Medium, null, null);
        return await AddAsync(details, TaskSource.Interruption, planForToday: true);
    }

    public async Task UpdateDetailsAsync(int id, TaskDetails details)
    {
        WorkTask task = await FindAsync(id);
        await ApplyDetailsAsync(task, details, recordChanges: true);
        await _db.SaveChangesAsync();
    }

    public Task StartAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.InProgress);

    public Task PauseAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.ToDo);

    public Task CompleteAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.Completed);

    public Task DropAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.Dropped);

    public Task ReopenAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.ToDo);

    public async Task PlanForTodayAsync(int id)
    {
        WorkTask task = await FindAsync(id);
        if (!task.IsOpen)
        {
            throw new DomainRuleException("Only open tasks can be planned for today. Reopen it first.");
        }

        SetPlannedFor(task, _clock.Today);
        await _db.SaveChangesAsync();
    }

    public async Task ReturnToBacklogAsync(int id)
    {
        WorkTask task = await FindAsync(id);
        SetPlannedFor(task, null);
        await _db.SaveChangesAsync();
    }

    public async Task<TodayView> GetTodayAsync()
    {
        DateOnly today = _clock.Today;
        List<WorkTask> openPlanned = await _db.Tasks
            .Include(t => t.Project)
            .Where(t => t.PlannedFor != null && (t.Status == WorkTaskStatus.ToDo || t.Status == WorkTaskStatus.InProgress))
            .ToListAsync();

        List<WorkTask> planned = SortForWork(openPlanned.Where(t => t.PlannedFor == today));
        List<WorkTask> carriedOver = SortForWork(openPlanned.Where(t => t.PlannedFor < today));

        // Completed timestamps are UTC; look a day either side and then compare local dates.
        DateTime windowStartUtc = _clock.UtcNow.AddDays(-2);
        List<WorkTask> recentlyCompleted = await _db.Tasks
            .Include(t => t.Project)
            .Where(t => t.Status == WorkTaskStatus.Completed && t.CompletedAtUtc >= windowStartUtc)
            .ToListAsync();
        List<WorkTask> completedToday = recentlyCompleted
            .Where(t => _clock.ToLocalDate(t.CompletedAtUtc!.Value) == today)
            .OrderBy(t => t.CompletedAtUtc)
            .ToList();

        AppSettings settings = await _settings.GetAsync();
        return new TodayView(
            today,
            planned,
            carriedOver,
            completedToday,
            planned.Sum(t => t.EstimateHours ?? 0),
            planned.Count(t => t.EstimateHours is null),
            settings.DailyPlanTargetHours);
    }

    public async Task<BacklogView> GetBacklogAsync()
    {
        List<WorkTask> openTasks = await _db.Tasks
            .Include(t => t.Project)
            .Where(t => t.Status == WorkTaskStatus.ToDo || t.Status == WorkTaskStatus.InProgress)
            .ToListAsync();
        List<Project> projects = await _db.Projects.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync();

        List<ProjectBacklog> groups = new();
        List<WorkTask> unfiled = SortForWork(openTasks.Where(t => t.ProjectId is null));
        if (unfiled.Count > 0)
        {
            groups.Add(new ProjectBacklog(null, unfiled, PriorityCounts.From(unfiled)));
        }

        foreach (Project project in projects)
        {
            List<WorkTask> projectTasks = SortForWork(openTasks.Where(t => t.ProjectId == project.Id));
            groups.Add(new ProjectBacklog(project, projectTasks, PriorityCounts.From(projectTasks)));
        }

        return new BacklogView(groups, PriorityCounts.From(openTasks));
    }

    public async Task<List<WorkTask>> GetRecentlyClosedAsync(int days)
    {
        DateTime sinceUtc = _clock.UtcNow.AddDays(-days);
        List<WorkTask> closed = await _db.Tasks
            .Include(t => t.Project)
            .Where(t => (t.Status == WorkTaskStatus.Completed && t.CompletedAtUtc >= sinceUtc)
                || (t.Status == WorkTaskStatus.Dropped && t.DroppedAtUtc >= sinceUtc))
            .ToListAsync();
        return closed.OrderByDescending(t => t.CompletedAtUtc ?? t.DroppedAtUtc).ToList();
    }

    private static List<WorkTask> SortForWork(IEnumerable<WorkTask> tasks)
    {
        return tasks
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.DueDate ?? DateOnly.MaxValue)
            .ThenBy(t => t.CreatedAtUtc)
            .ToList();
    }

    private async Task ChangeStatusAsync(int id, WorkTaskStatus newStatus)
    {
        WorkTask task = await FindAsync(id);
        WorkTaskStatus oldStatus = task.Status;
        if (oldStatus == newStatus)
        {
            return;
        }

        bool closing = newStatus is WorkTaskStatus.Completed or WorkTaskStatus.Dropped;
        if (closing && !task.IsOpen)
        {
            // A direct Completed <-> Dropped switch would rewrite what happened; go through reopen.
            throw new DomainRuleException("This task is already closed. Reopen it first.");
        }

        DateTime now = _clock.UtcNow;
        task.Status = newStatus;
        task.CompletedAtUtc = newStatus == WorkTaskStatus.Completed ? now : null;
        task.DroppedAtUtc = newStatus == WorkTaskStatus.Dropped ? now : null;

        _db.TaskStatusChanges.Add(new TaskStatusChange { WorkTaskId = task.Id, FromStatus = oldStatus, ToStatus = newStatus, ChangedAtUtc = now });
        await _db.SaveChangesAsync();
    }

    private async Task ApplyDetailsAsync(WorkTask task, TaskDetails details, bool recordChanges)
    {
        string title = (details.Title ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            throw new DomainRuleException("A task needs a title.");
        }

        if (title.Length > 1000)
        {
            throw new DomainRuleException("Task title must be 1000 characters or fewer.");
        }

        if (!Enum.IsDefined(details.Priority))
        {
            throw new DomainRuleException("Unknown priority.");
        }

        ValidateEstimate(details.EstimateHours);

        if (details.ProjectId is int projectId && !await _db.Projects.AnyAsync(p => p.Id == projectId))
        {
            throw new DomainRuleException("Project not found.");
        }

        if (recordChanges)
        {
            DateTime now = _clock.UtcNow;
            RecordChange(task, TaskField.EstimateHours, FormatHours(task.EstimateHours), FormatHours(details.EstimateHours), now);
            RecordChange(task, TaskField.Priority, task.Priority.ToString(), details.Priority.ToString(), now);
        }

        task.Title = title;
        task.ProjectId = details.ProjectId;
        task.Priority = details.Priority;
        task.DueDate = details.DueDate;
        task.EstimateHours = details.EstimateHours;
    }

    private void SetPlannedFor(WorkTask task, DateOnly? plannedFor)
    {
        RecordChange(task, TaskField.PlannedFor, FormatDate(task.PlannedFor), FormatDate(plannedFor), _clock.UtcNow);
        task.PlannedFor = plannedFor;
    }

    /// <summary>Adds a history row to the pending save, but only when the value really changed.</summary>
    private void RecordChange(WorkTask task, TaskField field, string? oldValue, string? newValue, DateTime changedAtUtc)
    {
        if (oldValue == newValue)
        {
            return;
        }

        _db.TaskFieldChanges.Add(new TaskFieldChange
        {
            WorkTask = task,
            Field = field,
            OldValue = oldValue,
            NewValue = newValue,
            ChangedAtUtc = changedAtUtc
        });
    }

    private static string? FormatHours(double? hours) => hours?.ToString("0.##", CultureInfo.InvariantCulture);

    private static string? FormatDate(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void ValidateEstimate(double? estimateHours)
    {
        if (estimateHours is not double hours)
        {
            return;
        }

        double steps = hours / EstimateStepHours;
        bool isWholeSteps = Math.Abs(steps - Math.Round(steps)) < 1e-9;
        if (hours < EstimateStepHours || !isWholeSteps)
        {
            throw new DomainRuleException("Size is in hours, in 15-minute steps, with a minimum of 0.25.");
        }
    }

    private async Task<WorkTask> FindAsync(int id)
    {
        return await _db.Tasks.FindAsync(id) ?? throw new DomainRuleException("Task not found.");
    }
}
