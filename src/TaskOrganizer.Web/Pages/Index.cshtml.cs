using Microsoft.AspNetCore.Mvc.RazorPages;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

public class IndexModel : PageModel
{
    private readonly TaskService _tasks;
    private readonly ProjectService _projects;
    private readonly TimeLogService _timeLog;

    public IndexModel(TaskService tasks, ProjectService projects, TimeLogService timeLog)
    {
        _tasks = tasks;
        _projects = projects;
        _timeLog = timeLog;
    }

    public TodayView Today { get; private set; } = null!;
    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();
    public WeekSummary Week { get; private set; } = null!;
    public ClockStatus Clock { get; private set; } = null!;

    public DayLog TodayLog => Week.Days.Single(d => d.Date == Today.Date);

    public async Task OnGetAsync()
    {
        Today = await _tasks.GetTodayAsync();
        Projects = await _projects.ListAsync();
        Week = await _timeLog.GetWeekAsync(Today.Date);
        Clock = await _timeLog.GetClockStatusAsync();
    }

    /// <summary>Rows on Today offer "Done for today" as the main button, with plain Done beside it.</summary>
    public TaskRowModel Row(WorkTask task) => new(task, Projects, Today.Date, Request.CurrentUrl()) { OfferDoneForToday = true };

    public ReviewSectionsModel Review => new(Today, Projects, Request.CurrentUrl());

    public DayTimeEntriesModel TimeEntries => new(TodayLog, Today.Date, Request.CurrentUrl());
}
