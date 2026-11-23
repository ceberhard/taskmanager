using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

/// <summary>
/// One row of the mass-edit form: the values as typed, and the values the page showed when
/// it loaded. Only rows where the two differ are saved, so a page left open cannot write its
/// old values over a change made somewhere else in the meantime.
/// </summary>
public class PlanningRowRequest
{
    public int Id { get; set; }
    public TaskPriority Priority { get; set; }
    public DateOnly? DueDate { get; set; }
    public double? EstimateHours { get; set; }
    public TaskPriority OriginalPriority { get; set; }
    public DateOnly? OriginalDueDate { get; set; }
    public double? OriginalEstimateHours { get; set; }

    public bool IsChanged => Priority != OriginalPriority || DueDate != OriginalDueDate || EstimateHours != OriginalEstimateHours;

    public static PlanningRowRequest From(WorkTask task)
    {
        return new PlanningRowRequest
        {
            Id = task.Id,
            Priority = task.Priority,
            DueDate = task.DueDate,
            EstimateHours = task.EstimateHours,
            OriginalPriority = task.Priority,
            OriginalDueDate = task.DueDate,
            OriginalEstimateHours = task.EstimateHours
        };
    }
}

// Mass edit posts seven values per task; the default limit of 1024 form values would cap it near 145 tasks.
[RequestFormLimits(ValueCountLimit = 20000)]
public class BacklogModel : ActionPageModel
{
    private const int RecentlyClosedDays = 14;

    private readonly TaskService _tasks;
    private readonly ProjectService _projects;
    private readonly LocalClock _clock;

    public BacklogModel(TaskService tasks, ProjectService projects, LocalClock clock)
    {
        _tasks = tasks;
        _projects = projects;
        _clock = clock;
    }

    /// <summary>Project ids to show (0 = unfiled). Empty shows every project. Kept in the address so a view can be bookmarked.</summary>
    [BindProperty(SupportsGet = true)]
    public List<int> Project { get; set; } = new();

    /// <summary>Priorities to show. Empty shows every priority.</summary>
    [BindProperty(SupportsGet = true)]
    public List<TaskPriority> Priority { get; set; } = new();

    /// <summary>True shows the mass-edit form instead of the normal rows.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Edit { get; set; }

    public BacklogView Backlog { get; private set; } = null!;
    public IReadOnlyList<WorkTask> RecentlyClosed { get; private set; } = Array.Empty<WorkTask>();
    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();
    public int ClosedWindowDays => RecentlyClosedDays;
    public DateOnly Today => _clock.Today;

    /// <summary>Rows from a rejected mass edit, kept so the form can be shown again as it was typed.</summary>
    private Dictionary<int, PlanningRowRequest> _rejectedRows = new();

    public Task OnGetAsync() => LoadAsync();

    public async Task<IActionResult> OnPostSaveAllAsync([FromForm] List<PlanningRowRequest> rows)
    {
        string? unreadable = InputProblem("Nothing was saved. A priority, date or number of hours could not be read.");
        if (unreadable is not null)
        {
            // The typed values could not be read, so there is nothing reliable to show again.
            ShowError(unreadable);
            return Redirect(ViewUrl(edit: true));
        }

        bool saved = await TryRunAsync(async () =>
        {
            List<TaskPlanningEdit> edits = rows
                .Where(r => r.IsChanged)
                .Select(r => new TaskPlanningEdit(r.Id, r.Priority, r.DueDate, r.EstimateHours))
                .ToList();
            int changed = await _tasks.UpdatePlanningAsync(edits);
            return changed == 1 ? "1 task updated." : $"{changed} tasks updated.";
        });
        if (saved)
        {
            return Redirect(ViewUrl(edit: false));
        }

        // Show the form again exactly as typed, so only the offending row needs fixing.
        _rejectedRows = rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        Edit = true;
        await LoadAsync();
        return Page();
    }

    /// <summary>The values to show for a task in edit mode: what was typed if a save was just rejected, otherwise what is stored.</summary>
    public PlanningRowRequest EditRow(WorkTask task)
    {
        return _rejectedRows.TryGetValue(task.Id, out PlanningRowRequest? typed) ? typed : PlanningRowRequest.From(task);
    }

    private async Task LoadAsync()
    {
        Backlog = await _tasks.GetBacklogAsync(new BacklogFilter(Project, Priority));
        RecentlyClosed = await _tasks.GetRecentlyClosedAsync(RecentlyClosedDays);
        Projects = await _projects.ListAsync();
    }

    public TaskRowModel Row(WorkTask task, bool showProject) => new(task, Projects, Today, Request.CurrentUrl(), showProject);

    /// <summary>This page's address with the current filter, in or out of edit mode.</summary>
    public string ViewUrl(bool edit)
    {
        List<KeyValuePair<string, string>> query = new();
        query.AddRange(Project.Select(id => KeyValuePair.Create(nameof(Project), id.ToString())));
        query.AddRange(Priority.Select(priority => KeyValuePair.Create(nameof(Priority), priority.ToString())));
        if (edit)
        {
            query.Add(KeyValuePair.Create(nameof(Edit), "true"));
        }

        return Url.PageWithQuery("/Backlog", query);
    }
}
