using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

public class TaskService
{
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

    /// <summary>
    /// Mass edit: sets priority, expected delivery date and size on many tasks in one save.
    /// Every row is checked first; if any is invalid nothing is saved and the message names the task.
    /// Returns how many tasks actually changed.
    /// </summary>
    public async Task<int> UpdatePlanningAsync(IReadOnlyList<TaskPlanningEdit> edits)
    {
        List<int> ids = edits.Select(e => e.Id).ToList();
        Dictionary<int, WorkTask> tasks = await _db.Tasks.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id);

        List<string> problems = new();
        foreach (TaskPlanningEdit edit in edits)
        {
            if (!tasks.TryGetValue(edit.Id, out WorkTask? task))
            {
                problems.Add($"Task {edit.Id} no longer exists.");
                continue;
            }

            string? problem = PlanningProblem(edit.Priority, edit.EstimateHours);
            if (problem is not null)
            {
                problems.Add($"\"{Shorten(task.Title)}\": {problem}");
            }
        }

        if (problems.Count > 0)
        {
            throw new DomainRuleException("Nothing was saved. " + string.Join(" ", problems));
        }

        int changed = 0;
        DateTime now = _clock.UtcNow;
        foreach (TaskPlanningEdit edit in edits)
        {
            WorkTask task = tasks[edit.Id];
            bool differs = task.Priority != edit.Priority || task.DueDate != edit.DueDate || task.EstimateHours != edit.EstimateHours;
            if (differs)
            {
                ApplyPlanning(task, edit.Priority, edit.DueDate, edit.EstimateHours, now);
                changed++;
            }
        }

        await _db.SaveChangesAsync();
        return changed;
    }

    public Task StartAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.InProgress);

    public Task PauseAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.ToDo);

    /// <summary>
    /// Closes a task as finished. For a task that was done for today this is the sign-off:
    /// the completion time is when it was marked, because that is when the work stopped.
    /// </summary>
    public Task CompleteAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.Completed);

    public Task DropAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.Dropped);

    /// <summary>Brings a closed task back as To do.</summary>
    public Task ReopenAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.ToDo);

    /// <summary>Takes a task off hold; it becomes To do again and its hold reason is cleared.</summary>
    public Task ResumeAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.ToDo);

    /// <summary>
    /// Marks a task finished for the day without closing it. It stays visible, and from the
    /// next day it is offered for review: Done, re-open for today, or back to the backlog.
    /// </summary>
    public Task MarkDoneForTodayAsync(int id) => ChangeStatusAsync(id, WorkTaskStatus.DoneForToday);

    /// <summary>
    /// Puts a task on hold with a reason in the person's own words, or changes the reason or
    /// check-back date of a task already on hold. Every call is recorded in the status history
    /// with the reason. Optionally remembers the reason as a suggestion for next time.
    /// </summary>
    public async Task HoldAsync(int id, string? reason, DateOnly? checkBackOn, bool rememberReason = false)
    {
        string cleanReason = (reason ?? string.Empty).Trim();
        if (cleanReason.Length == 0)
        {
            throw new DomainRuleException("Say why the task is on hold.");
        }

        if (cleanReason.Length > AppDbContext.HoldReasonMaxLength)
        {
            throw new DomainRuleException($"The hold reason must be {AppDbContext.HoldReasonMaxLength} characters or fewer.");
        }

        WorkTask task = await FindAsync(id);
        ApplyStatus(task, WorkTaskStatus.OnHold, cleanReason);
        task.HoldReason = cleanReason;
        task.HoldCheckBackOn = checkBackOn;

        if (rememberReason && !await _db.HoldReasonSuggestions.AnyAsync(r => r.Text == cleanReason))
        {
            _db.HoldReasonSuggestions.Add(new HoldReasonSuggestion { Text = cleanReason });
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>Review choice: the task was not finished after all, so it goes back into today's work.</summary>
    public async Task ReopenForTodayAsync(int id)
    {
        WorkTask task = await FindAsync(id);
        if (!task.IsWorkable)
        {
            ApplyStatus(task, WorkTaskStatus.ToDo, null);
        }

        SetPlannedFor(task, _clock.Today);
        await _db.SaveChangesAsync();
    }

    /// <summary>Review choice: more to do, but not today. The task returns to the backlog unplanned.</summary>
    public async Task ReturnToBacklogAsync(int id)
    {
        WorkTask task = await FindAsync(id);
        if (task.Status == WorkTaskStatus.DoneForToday)
        {
            ApplyStatus(task, WorkTaskStatus.ToDo, null);
        }

        SetPlannedFor(task, null);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Removes a task and all of its history for good. Meant for tasks created by mistake;
    /// real work that was abandoned should be dropped instead, so it still shows in reports.
    /// </summary>
    public async Task DeleteAsync(int id)
    {
        WorkTask task = await FindAsync(id);
        _db.TaskStatusChanges.RemoveRange(await _db.TaskStatusChanges.Where(c => c.WorkTaskId == id).ToListAsync());
        _db.TaskFieldChanges.RemoveRange(await _db.TaskFieldChanges.Where(c => c.WorkTaskId == id).ToListAsync());
        _db.Tasks.Remove(task);
        await _db.SaveChangesAsync();
    }

    public async Task<List<TaskStatusChange>> GetStatusHistoryAsync(int id)
    {
        return await _db.TaskStatusChanges.Where(c => c.WorkTaskId == id).OrderBy(c => c.Id).ToListAsync();
    }

    public async Task<List<TaskFieldChange>> GetFieldHistoryAsync(int id)
    {
        return await _db.TaskFieldChanges.Where(c => c.WorkTaskId == id).OrderBy(c => c.Id).ToListAsync();
    }

    public async Task<WorkTask?> FindWithProjectAsync(int id)
    {
        return await _db.Tasks.Include(t => t.Project).FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task PlanForTodayAsync(int id)
    {
        WorkTask task = await FindAsync(id);
        if (!task.IsWorkable)
        {
            throw new DomainRuleException(task.IsClosed
                ? "Only open tasks can be planned for today. Reopen it first."
                : "This task is on hold or done for today. Resume or re-open it first.");
        }

        SetPlannedFor(task, _clock.Today);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Plans several tasks for today in one save, as when a suggested day is accepted.
    /// If any task no longer exists or has been closed since it was offered, nothing is
    /// planned and the message names it. Returns how many tasks were newly planned.
    /// </summary>
    public async Task<int> PlanManyForTodayAsync(IReadOnlyCollection<int> ids)
    {
        List<int> distinctIds = ids.Distinct().ToList();
        List<WorkTask> tasks = await _db.Tasks.Where(t => distinctIds.Contains(t.Id)).ToListAsync();

        List<string> problems = new();
        if (tasks.Count != distinctIds.Count)
        {
            problems.Add("One or more tasks no longer exist.");
        }

        problems.AddRange(tasks.Where(t => t.IsClosed).Select(t => $"\"{Shorten(t.Title)}\" has been closed."));
        problems.AddRange(tasks.Where(t => t.IsOpen && !t.IsWorkable).Select(t => $"\"{Shorten(t.Title)}\" is on hold or done for today."));
        if (problems.Count > 0)
        {
            throw new DomainRuleException("Nothing was planned. " + string.Join(" ", problems) + " Ask for a new suggestion.");
        }

        DateOnly today = _clock.Today;
        int planned = 0;
        foreach (WorkTask task in tasks.Where(t => t.PlannedFor != today))
        {
            SetPlannedFor(task, today);
            planned++;
        }

        await _db.SaveChangesAsync();
        return planned;
    }

    public async Task<TodayView> GetTodayAsync()
    {
        DateOnly today = _clock.Today;
        List<WorkTask> open = await _db.Tasks
            .Include(t => t.Project)
            .Where(t => t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Dropped)
            .ToListAsync();

        List<WorkTask> planned = TaskOrdering.ForWork(open.Where(t => t.IsWorkable && t.PlannedFor == today));
        List<WorkTask> carriedOver = TaskOrdering.ForWork(open.Where(t => t.IsWorkable && t.PlannedFor < today));

        // Done for today splits on the day it was marked: today's stay on show, earlier ones await review.
        List<WorkTask> doneForToday = open.Where(t => t.Status == WorkTaskStatus.DoneForToday).OrderBy(t => t.DoneForTodayAtUtc).ToList();
        List<WorkTask> markedToday = doneForToday.Where(t => MarkedDoneOn(t) == today).ToList();
        List<WorkTask> awaitingReview = doneForToday.Where(t => MarkedDoneOn(t) < today).ToList();

        // On hold: those whose check-back day has come are brought up for follow-up; the rest
        // stay listed only if they were part of a day's plan.
        List<WorkTask> onHold = TaskOrdering.ForWork(open.Where(t => t.Status == WorkTaskStatus.OnHold));
        List<WorkTask> followUps = onHold.Where(t => t.HoldCheckBackOn <= today).OrderBy(t => t.HoldCheckBackOn).ToList();
        List<WorkTask> parked = onHold.Where(t => !(t.HoldCheckBackOn <= today) && t.PlannedFor is not null).ToList();

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

        // Unsized tasks count at the assumed size, the same as in a suggested day, so the two
        // pages never give different answers to "how full is today".
        int unsizedCount = planned.Count(t => t.EstimateHours is null);
        double assumedHours = unsizedCount * settings.AssumedUnsizedHours;
        double plannedHours = planned.Sum(t => t.EstimateHours ?? 0) + assumedHours;

        // If a suggestion was accepted today, the hours entered for it are today's capacity.
        DayPlan? latestPlan = await _db.DayPlans
            .Where(p => p.Date == today)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync();

        return new TodayView(
            today,
            planned,
            carriedOver,
            completedToday,
            plannedHours,
            unsizedCount,
            assumedHours,
            latestPlan?.HoursAvailable ?? settings.DailyPlanTargetHours,
            latestPlan is not null)
        {
            DoneForToday = markedToday,
            AwaitingReview = awaitingReview,
            FollowUps = followUps,
            OnHold = parked,
            InProgressCount = open.Count(t => t.Status == WorkTaskStatus.InProgress)
        };
    }

    /// <summary>The local day a task was marked done for today.</summary>
    private DateOnly? MarkedDoneOn(WorkTask task)
    {
        return task.DoneForTodayAtUtc is DateTime markedAt ? _clock.ToLocalDate(markedAt) : null;
    }

    public async Task<BacklogView> GetBacklogAsync(BacklogFilter? filter = null)
    {
        BacklogFilter effectiveFilter = filter ?? BacklogFilter.None;
        List<WorkTask> notClosed = await _db.Tasks
            .Include(t => t.Project)
            .Where(t => t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Dropped)
            .ToListAsync();

        // The priority picture counts work still to be done: workable and on hold. A task that
        // is done for today is listed, but no longer adds to the pile.
        List<WorkTask> openTasks = notClosed.Where(t => t.Status != WorkTaskStatus.DoneForToday).ToList();
        List<Project> projects = await _db.Projects.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync();
        List<WorkTask> shownTasks = notClosed.Where(effectiveFilter.Matches).ToList();

        List<ProjectBacklog> groups = new();
        List<WorkTask> unfiled = TaskOrdering.ForWork(shownTasks.Where(t => t.ProjectId is null));
        if (unfiled.Count > 0)
        {
            groups.Add(new ProjectBacklog(null, unfiled, PriorityCounts.From(unfiled)));
        }

        foreach (Project project in projects)
        {
            List<WorkTask> projectTasks = TaskOrdering.ForWork(shownTasks.Where(t => t.ProjectId == project.Id));

            // Unfiltered, an empty project is still listed so it can be seen and filled.
            // With a filter on, a project with nothing to show would only be noise.
            if (projectTasks.Count > 0 || !effectiveFilter.IsActive)
            {
                groups.Add(new ProjectBacklog(project, projectTasks, PriorityCounts.From(projectTasks)));
            }
        }

        // The priority surface ignores the filter: it exists to show the whole picture.
        List<ProjectPriorityCounts> surface = openTasks
            .GroupBy(t => t.ProjectId)
            .Select(g => new ProjectPriorityCounts(projects.FirstOrDefault(p => p.Id == g.Key), PriorityCounts.From(g)))
            .OrderBy(row => row.Project is null ? int.MinValue : row.Project.SortOrder)
            .ToList();

        return new BacklogView(groups, PriorityCounts.From(openTasks), surface, effectiveFilter.IsActive);
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

    private async Task ChangeStatusAsync(int id, WorkTaskStatus newStatus)
    {
        WorkTask task = await FindAsync(id);
        if (task.Status == newStatus)
        {
            return;
        }

        ApplyStatus(task, newStatus, null);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// The moves a task may make. Anything else is refused, so a task cannot, for example, go
    /// straight from Completed to Dropped (which would rewrite what happened) or be worked on
    /// while it is on hold.
    /// </summary>
    private static readonly IReadOnlyDictionary<WorkTaskStatus, WorkTaskStatus[]> AllowedMoves = new Dictionary<WorkTaskStatus, WorkTaskStatus[]>
    {
        [WorkTaskStatus.ToDo] = new[] { WorkTaskStatus.InProgress, WorkTaskStatus.OnHold, WorkTaskStatus.DoneForToday, WorkTaskStatus.Completed, WorkTaskStatus.Dropped },
        [WorkTaskStatus.InProgress] = new[] { WorkTaskStatus.ToDo, WorkTaskStatus.OnHold, WorkTaskStatus.DoneForToday, WorkTaskStatus.Completed, WorkTaskStatus.Dropped },
        [WorkTaskStatus.OnHold] = new[] { WorkTaskStatus.ToDo, WorkTaskStatus.OnHold, WorkTaskStatus.Completed, WorkTaskStatus.Dropped },
        [WorkTaskStatus.DoneForToday] = new[] { WorkTaskStatus.ToDo, WorkTaskStatus.Completed, WorkTaskStatus.Dropped },
        [WorkTaskStatus.Completed] = new[] { WorkTaskStatus.ToDo },
        [WorkTaskStatus.Dropped] = new[] { WorkTaskStatus.ToDo }
    };

    /// <summary>
    /// Moves a task to a new status, sets the timestamps that go with it, and adds the history
    /// row to the pending save. The caller saves.
    /// </summary>
    private void ApplyStatus(WorkTask task, WorkTaskStatus newStatus, string? note)
    {
        WorkTaskStatus oldStatus = task.Status;
        if (!AllowedMoves[oldStatus].Contains(newStatus))
        {
            throw new DomainRuleException(MoveRefusedMessage(task, newStatus));
        }

        DateTime now = _clock.UtcNow;
        switch (newStatus)
        {
            case WorkTaskStatus.Completed:
                // Signing off a done-for-today task: the work stopped when it was marked, not now.
                bool isSignOff = oldStatus == WorkTaskStatus.DoneForToday && task.DoneForTodayAtUtc is not null;
                task.CompletedAtUtc = isSignOff ? task.DoneForTodayAtUtc : now;
                task.ConfirmedAtUtc = isSignOff ? now : null;
                break;
            case WorkTaskStatus.Dropped:
                task.DroppedAtUtc = now;
                break;
            case WorkTaskStatus.DoneForToday:
                task.DoneForTodayAtUtc = now;
                break;
            case WorkTaskStatus.ToDo:
                // Back to open work, from wherever it was: nothing about a finish still applies.
                task.CompletedAtUtc = null;
                task.DroppedAtUtc = null;
                task.ConfirmedAtUtc = null;
                task.DoneForTodayAtUtc = null;
                break;
        }

        if (newStatus != WorkTaskStatus.OnHold)
        {
            task.HoldReason = null;
            task.HoldCheckBackOn = null;
        }

        task.Status = newStatus;
        _db.TaskStatusChanges.Add(new TaskStatusChange { WorkTask = task, FromStatus = oldStatus, ToStatus = newStatus, ChangedAtUtc = now, Note = note });
    }

    private static string MoveRefusedMessage(WorkTask task, WorkTaskStatus newStatus)
    {
        if (task.IsClosed)
        {
            return "This task is already closed. Reopen it first.";
        }

        return task.Status switch
        {
            WorkTaskStatus.OnHold => "This task is on hold. Resume it first.",
            WorkTaskStatus.DoneForToday => "This task is done for today. Re-open it first.",
            _ => $"A task that is {task.Status} cannot become {newStatus}."
        };
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

        string? planningProblem = PlanningProblem(details.Priority, details.EstimateHours);
        if (planningProblem is not null)
        {
            throw new DomainRuleException(planningProblem);
        }

        if (details.ProjectId is int projectId && !await _db.Projects.AnyAsync(p => p.Id == projectId))
        {
            throw new DomainRuleException("Project not found.");
        }

        task.Title = title;
        task.ProjectId = details.ProjectId;
        ApplyPlanning(task, details.Priority, details.DueDate, details.EstimateHours, recordChanges ? _clock.UtcNow : null);
    }

    /// <summary>
    /// Sets the three planning fields. Shared by single edit and mass edit so both leave the
    /// same history. A null changedAtUtc means the task is being created and has no history yet.
    /// </summary>
    private void ApplyPlanning(WorkTask task, TaskPriority priority, DateOnly? dueDate, double? estimateHours, DateTime? changedAtUtc)
    {
        if (changedAtUtc is DateTime at)
        {
            RecordChange(task, TaskField.EstimateHours, FormatHours(task.EstimateHours), FormatHours(estimateHours), at);
            RecordChange(task, TaskField.Priority, task.Priority.ToString(), priority.ToString(), at);
        }

        task.Priority = priority;
        task.DueDate = dueDate;
        task.EstimateHours = estimateHours;
    }

    private static string Shorten(string title) => title.Length <= 60 ? title : title[..57] + "...";

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

    /// <summary>Returns what is wrong with a priority and size, or null when both are acceptable.</summary>
    private static string? PlanningProblem(TaskPriority priority, double? estimateHours)
    {
        if (!Enum.IsDefined(priority))
        {
            return "Unknown priority.";
        }

        if (estimateHours is not double hours)
        {
            return null;
        }

        return HoursRules.IsValidSize(hours) ? null : $"Size is {HoursRules.StepProblem}.";
    }

    private async Task<WorkTask> FindAsync(int id)
    {
        return await _db.Tasks.FindAsync(id) ?? throw new DomainRuleException("Task not found.");
    }
}
