using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

/// <summary>
/// What to build a suggestion from. FocusProjectIds are today's focus projects
/// (BacklogFilter.UnfiledProjectId stands for tasks with no project); empty means no focus.
/// HoursAvailable defaults to the daily plan target; lower it on a day with meetings.
/// </summary>
public record SuggestionRequest(IReadOnlyCollection<int> FocusProjectIds, double? HoursAvailable);

/// <summary>
/// An adjusted proposal being accepted. TaskIds are the tasks ticked; SuggestedTaskIds are the
/// tasks the proposal offered, so the record can show how much of it was kept.
/// </summary>
public record AcceptRequest(IReadOnlyCollection<int> TaskIds, IReadOnlyCollection<int> SuggestedTaskIds, IReadOnlyCollection<int> FocusProjectIds, double? HoursAvailable);

/// <summary>One task in a proposal: the hours it counts as, and why it is there.</summary>
public record SuggestedTask(WorkTask Task, double Hours, bool IsAssumedSize, string Reason, bool IsFocus)
{
    /// <summary>True for work that is in the day whatever the room: carried over, in progress, Critical, overdue or due today.</summary>
    public bool IsMustInclude { get; init; }

    /// <summary>True for a must-include task that the hours available run out before reaching.</summary>
    public bool IsBeyondHours { get; init; }
}

/// <summary>
/// A proposed day. Suggested is the plan offered; LeftOut is every other open task, best
/// candidates first, so any of them can be swapped in. Nothing here has been saved.
/// </summary>
public record DayProposal(
    DateOnly Date,
    double HoursAvailable,
    double AlreadyPlannedHours,
    int AlreadyPlannedCount,
    IReadOnlyList<SuggestedTask> Suggested,
    IReadOnlyList<SuggestedTask> LeftOut)
{
    public double SuggestedHours => Suggested.Sum(s => s.Hours);

    public double TotalHours => AlreadyPlannedHours + SuggestedHours;

    public bool IsOverAvailable => HoursRules.Exceeds(TotalHours, HoursAvailable);

    public IEnumerable<SuggestedTask> MustInclude => Suggested.Where(s => s.IsMustInclude);

    public IEnumerable<SuggestedTask> Optional => Suggested.Where(s => !s.IsMustInclude);

    /// <summary>
    /// Hours by which the must-include work, with what is already planned, goes past the day.
    /// Zero when it fits. Above zero the proposal holds nothing else: the day is already spoken for.
    /// </summary>
    public double MustIncludeOverHours => Math.Max(0, AlreadyPlannedHours + MustInclude.Sum(s => s.Hours) - HoursAvailable);
}

/// <summary>
/// Proposes a workday from the open tasks. SuggestAsync only reads: nothing is planned until
/// the person accepts the proposal through AcceptAsync. The rules, in order:
///
/// 1. Must-include tasks go in whatever the room: Critical, overdue, due today, already in
///    progress, or carried over from an earlier day, in that order. Those the hours run out
///    before reaching are marked rather than dropped.
/// 2. One Low task is added if none is in the day yet and one still fits, so ideas are not
///    starved forever. The pick is random from day to day but tied to the task, so it stays
///    the same every time the proposal is recalculated on one day.
/// 3. Very High tasks are added next, whichever project they belong to, while they fit.
/// 4. The hours left are split between today's focus projects and everything else by the
///    focus share setting. Each side is filled best-first: by priority, with a task due
///    within the due-soon window ranked one priority level higher, then nearest date, then age.
/// 5. Room either side could not use is offered to the best of the rest, focus projects first.
///
/// A task with no size counts as the assumed-size setting.
/// </summary>
public class DaySuggestionService
{
    private readonly AppDbContext _db;
    private readonly LocalClock _clock;
    private readonly SettingsService _settings;
    private readonly TaskService _tasks;

    public DaySuggestionService(AppDbContext db, LocalClock clock, SettingsService settings, TaskService tasks)
    {
        _db = db;
        _clock = clock;
        _settings = settings;
        _tasks = tasks;
    }

    /// <summary>
    /// Accepts an adjusted proposal: plans the ticked tasks for today and records what the day
    /// was about (hours available, focus projects, how much of the proposal was kept). Both
    /// happen in one transaction, so a rejected acceptance leaves no trace.
    /// Returns how many tasks were newly planned.
    /// </summary>
    public async Task<int> AcceptAsync(AcceptRequest request)
    {
        AppSettings settings = await _settings.GetAsync();
        double hoursAvailable = ResolveHoursAvailable(request.HoursAvailable, settings);

        List<int> focusProjectIds = request.FocusProjectIds.Distinct().ToList();
        List<int> realProjectIds = focusProjectIds.Where(id => id != BacklogFilter.UnfiledProjectId).ToList();
        int knownProjects = await _db.Projects.CountAsync(p => realProjectIds.Contains(p.Id));
        if (knownProjects != realProjectIds.Count)
        {
            throw new DomainRuleException("Nothing was planned. A focus project no longer exists. Ask for a new suggestion.");
        }

        HashSet<int> accepted = request.TaskIds.ToHashSet();
        HashSet<int> suggested = request.SuggestedTaskIds.ToHashSet();

        await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync();
        int planned = await _tasks.PlanManyForTodayAsync(accepted);
        _db.DayPlans.Add(new DayPlan
        {
            Date = _clock.Today,
            AcceptedAtUtc = _clock.UtcNow,
            HoursAvailable = hoursAvailable,
            TasksSuggested = suggested.Count,
            TasksAcceptedFromSuggestion = accepted.Count(suggested.Contains),
            TasksSwappedIn = accepted.Count(id => !suggested.Contains(id)),
            FocusProjects = focusProjectIds
                .Select(id => new DayPlanFocusProject { ProjectId = id == BacklogFilter.UnfiledProjectId ? null : id })
                .ToList()
        });
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        return planned;
    }

    /// <summary>The most recent accepted plan for today, or null when none has been accepted yet.</summary>
    public async Task<DayPlan?> GetTodaysPlanAsync()
    {
        DateOnly today = _clock.Today;
        return await _db.DayPlans
            .Include(p => p.FocusProjects)
            .Where(p => p.Date == today)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync();
    }

    private static double ResolveHoursAvailable(double? requested, AppSettings settings)
    {
        double hoursAvailable = requested ?? settings.DailyPlanTargetHours;
        if (hoursAvailable < 0 || hoursAvailable > 24)
        {
            throw new DomainRuleException("Hours available must be between 0 and 24.");
        }

        return hoursAvailable;
    }

    public async Task<DayProposal> SuggestAsync(SuggestionRequest request)
    {
        AppSettings settings = await _settings.GetAsync();
        double hoursAvailable = ResolveHoursAvailable(request.HoursAvailable, settings);
        DateOnly today = _clock.Today;
        List<WorkTask> openTasks = await _db.Tasks
            .Include(t => t.Project)
            .Where(t => t.Status == WorkTaskStatus.ToDo || t.Status == WorkTaskStatus.InProgress)
            .ToListAsync();

        Rules rules = new(today, settings, request.FocusProjectIds);
        List<WorkTask> alreadyPlanned = openTasks.Where(t => t.PlannedFor == today).ToList();
        List<WorkTask> candidates = TaskOrdering.ForWork(openTasks.Where(t => t.PlannedFor != today));
        double alreadyPlannedHours = alreadyPlanned.Sum(rules.HoursFor);

        List<SuggestedTask> suggested = new();
        HashSet<int> chosen = new();

        double toFill = Math.Max(0, hoursAvailable - alreadyPlannedHours);
        double focusShare = rules.HasFocus ? toFill * settings.FocusSharePercent / 100.0 : toFill;
        Room room = new(toFill, focusShare, toFill - focusShare);

        // Rule 1: must-include tasks, most pressing first. They all go in, but each one the
        // hours run out before reaching is marked, so an over-full day shows where the line falls.
        IEnumerable<(WorkTask Task, string Reason)> mustIncludes = candidates
            .Select(task => (Task: task, Reason: rules.MustIncludeReason(task)))
            .Where(entry => entry.Reason is not null)
            .Select(entry => (entry.Task, entry.Reason!))
            .OrderBy(entry => rules.MustIncludeRank(entry.Task));
        foreach ((WorkTask task, string reason) in mustIncludes)
        {
            SuggestedTask must = rules.Suggest(task, reason);
            room.Charge(must, rules.HasFocus);
            suggested.Add(must with { IsMustInclude = true, IsBeyondHours = HoursRules.Exceeds(0, room.For(Side.Any)) });
            chosen.Add(task.Id);
        }

        // Rule 2: the Low pick, when the day has none yet and one still fits. Its hours are set
        // aside before the rest of the day is filled. On a day the must-include work already
        // fills, an idea would only add to the overrun, so none is picked.
        SuggestedTask? lowPick = null;
        bool dayHasLow = alreadyPlanned.Any(t => t.Priority == TaskPriority.Low) || suggested.Any(s => s.Task.Priority == TaskPriority.Low);
        if (!dayHasLow)
        {
            WorkTask? picked = PickLowTask(candidates.Where(t => !chosen.Contains(t.Id)), rules, room.For(Side.Any), today);
            if (picked is not null)
            {
                lowPick = rules.Suggest(picked, "Low pick for today");
                chosen.Add(picked.Id);
                room.Charge(lowPick, rules.HasFocus);
            }
        }

        List<WorkTask> ranked = candidates
            .Where(t => !chosen.Contains(t.Id))
            .OrderBy(rules.Rank)
            .ThenBy(t => t.DueDate ?? DateOnly.MaxValue)
            .ThenBy(t => t.CreatedAtUtc)
            .ToList();

        // Rule 3: Very High work comes before the focus split, whichever project it belongs to.
        Fill(ranked.Where(t => t.Priority == TaskPriority.VeryHigh), Side.Any, room, rules, suggested, chosen, isLeftoverPass: false);

        // Rule 4: fill each side of the split best-first.
        Fill(ranked.Where(t => rules.IsFocus(t) || !rules.HasFocus), Side.Focus, room, rules, suggested, chosen, isLeftoverPass: false);
        Fill(ranked.Where(t => rules.HasFocus && !rules.IsFocus(t)), Side.Other, room, rules, suggested, chosen, isLeftoverPass: false);

        // Rule 5: room one side could not use goes to the best of the rest, focus projects first.
        // Whole tasks rarely split exactly at the share, and the remainder should lean towards focus.
        IEnumerable<WorkTask> focusFirst = ranked.OrderBy(t => rules.HasFocus && rules.IsFocus(t) ? 0 : 1);
        Fill(focusFirst, Side.Any, room, rules, suggested, chosen, isLeftoverPass: true);

        if (lowPick is not null)
        {
            suggested.Add(lowPick);
        }

        List<SuggestedTask> leftOut = ranked
            .Where(t => !chosen.Contains(t.Id))
            .Select(t => rules.Suggest(t, rules.FillReason(t, isLeftoverPass: false)))
            .ToList();

        return new DayProposal(today, hoursAvailable, alreadyPlannedHours, alreadyPlanned.Count, suggested, leftOut);
    }

    /// <summary>
    /// Chooses today's Low task. Each task gets a score from the date and its own id, and the
    /// lowest score wins: random from day to day, yet tied to the task rather than its place in
    /// a list, so finishing or re-prioritising other Low tasks during the day does not move the
    /// pick. A Low task added today cannot take the pick from an older one, for the same reason.
    /// </summary>
    private WorkTask? PickLowTask(IEnumerable<WorkTask> remaining, Rules rules, double room, DateOnly today)
    {
        List<WorkTask> fitting = remaining
            .Where(t => t.Priority == TaskPriority.Low && !HoursRules.Exceeds(rules.HoursFor(t), room))
            .ToList();
        List<WorkTask> addedBeforeToday = fitting.Where(t => _clock.ToLocalDate(t.CreatedAtUtc) < today).ToList();
        List<WorkTask> eligible = addedBeforeToday.Count > 0 ? addedBeforeToday : fitting;
        return eligible.OrderBy(t => DailyScore(today.DayNumber, t.Id)).ThenBy(t => t.Id).FirstOrDefault();
    }

    /// <summary>
    /// A fixed pseudo-random number for one task on one day. Written out rather than using
    /// HashCode, whose results differ between runs of the app.
    /// </summary>
    private static ulong DailyScore(int dayNumber, int taskId)
    {
        unchecked
        {
            ulong value = ((ulong)(uint)dayNumber << 32) | (uint)taskId;
            value ^= value >> 30;
            value *= 0xBF58476D1CE4E5B9UL;
            value ^= value >> 27;
            value *= 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return value;
        }
    }

    /// <summary>Adds every task that still fits on the given side of the split, in the order given.</summary>
    private static void Fill(IEnumerable<WorkTask> ranked, Side side, Room room, Rules rules, List<SuggestedTask> suggested, HashSet<int> chosen, bool isLeftoverPass)
    {
        foreach (WorkTask task in ranked)
        {
            if (chosen.Contains(task.Id))
            {
                continue;
            }

            SuggestedTask candidate = rules.Suggest(task, rules.FillReason(task, isLeftoverPass));
            if (!HoursRules.Exceeds(candidate.Hours, room.For(side)))
            {
                suggested.Add(candidate);
                chosen.Add(task.Id);
                room.Charge(candidate, rules.HasFocus);
            }
        }
    }

    private enum Side
    {
        /// <summary>Limited only by what is left of the whole day.</summary>
        Any,
        Focus,
        Other
    }

    /// <summary>
    /// Hours still free: for the whole day, and for each side of the focus split. A side can
    /// go negative when must-include or Very High work overdraws it; the whole-day figure is
    /// then what stops the other side from overfilling the day.
    /// </summary>
    private sealed class Room
    {
        private double _total;
        private double _focus;
        private double _other;

        public Room(double total, double focus, double other)
        {
            _total = total;
            _focus = focus;
            _other = other;
        }

        public double For(Side side) => side switch
        {
            Side.Focus => Math.Min(_focus, _total),
            Side.Other => Math.Min(_other, _total),
            _ => _total
        };

        /// <summary>With no focus chosen there is one side only, and everything is charged to it.</summary>
        public void Charge(SuggestedTask task, bool hasFocus)
        {
            _total -= task.Hours;
            if (task.IsFocus || !hasFocus)
            {
                _focus -= task.Hours;
            }
            else
            {
                _other -= task.Hours;
            }
        }
    }

    /// <summary>The per-task judgements, gathered in one place so every weight and limit can be read together.</summary>
    private sealed class Rules
    {
        private const string DateFormat = "ddd d MMM";

        private readonly DateOnly _today;
        private readonly AppSettings _settings;
        private readonly IReadOnlyCollection<int> _focusProjectIds;

        public Rules(DateOnly today, AppSettings settings, IReadOnlyCollection<int> focusProjectIds)
        {
            _today = today;
            _settings = settings;
            _focusProjectIds = focusProjectIds;
        }

        public bool HasFocus => _focusProjectIds.Count > 0;

        public bool IsFocus(WorkTask task) => _focusProjectIds.Contains(task.ProjectId ?? BacklogFilter.UnfiledProjectId);

        public double HoursFor(WorkTask task) => task.EstimateHours ?? _settings.AssumedUnsizedHours;

        public SuggestedTask Suggest(WorkTask task, string reason)
        {
            return new SuggestedTask(task, HoursFor(task), task.EstimateHours is null, reason, IsFocus(task));
        }

        /// <summary>
        /// Order among must-include tasks, most pressing first: Critical, overdue, due today,
        /// already in progress, then work only carried over. Ties keep the usual work order.
        /// </summary>
        public int MustIncludeRank(WorkTask task)
        {
            if (task.Priority == TaskPriority.Critical)
            {
                return 0;
            }

            if (task.DueDate is DateOnly due && due <= _today)
            {
                return due < _today ? 1 : 2;
            }

            return task.Status == WorkTaskStatus.InProgress ? 3 : 4;
        }

        /// <summary>Why a task must be in the day regardless of room, or null when it need not be.</summary>
        public string? MustIncludeReason(WorkTask task)
        {
            List<string> reasons = new();
            if (task.PlannedFor is DateOnly plannedFor && plannedFor < _today)
            {
                reasons.Add($"carried over from {plannedFor.ToString(DateFormat)}");
            }

            if (task.Status == WorkTaskStatus.InProgress)
            {
                reasons.Add("already in progress");
            }

            if (task.Priority == TaskPriority.Critical)
            {
                reasons.Add("Critical");
            }

            if (task.DueDate is DateOnly due && due < _today)
            {
                reasons.Add($"overdue since {due.ToString(DateFormat)}");
            }
            else if (task.DueDate == _today)
            {
                reasons.Add("due today");
            }

            return reasons.Count == 0 ? null : string.Join(", ", reasons);
        }

        /// <summary>
        /// Lower ranks are suggested first. A task due soon ranks one priority level higher,
        /// but never above Very High: only a real Critical is treated as must-include.
        /// </summary>
        public int Rank(WorkTask task)
        {
            int rank = (int)task.Priority;
            return IsDueSoon(task) ? Math.Max((int)TaskPriority.VeryHigh, rank - 1) : rank;
        }

        public string FillReason(WorkTask task, bool isLeftoverPass)
        {
            List<string> reasons = new();
            if (HasFocus && IsFocus(task))
            {
                reasons.Add("focus project");
            }

            reasons.Add(task.Priority == TaskPriority.VeryHigh ? "Very High priority" : $"{task.Priority} priority");
            if (IsDueSoon(task))
            {
                reasons.Add($"due {task.DueDate!.Value.ToString(DateFormat)}");
            }

            if (isLeftoverPass)
            {
                reasons.Add("fits the time left");
            }

            return string.Join(", ", reasons);
        }

        private bool IsDueSoon(WorkTask task)
        {
            return task.DueDate is DateOnly due && due > _today && WorkingDaysUntil(due) <= _settings.DueSoonWorkingDays;
        }

        /// <summary>Monday-to-Friday days after today, up to and including the given date.</summary>
        private int WorkingDaysUntil(DateOnly date)
        {
            int workingDays = 0;
            for (DateOnly day = _today.AddDays(1); day <= date; day = day.AddDays(1))
            {
                if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                {
                    workingDays++;
                }
            }

            return workingDays;
        }
    }
}
