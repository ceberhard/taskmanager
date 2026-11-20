using Microsoft.AspNetCore.Mvc.RazorPages;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

public class BacklogModel : PageModel
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

    public BacklogView Backlog { get; private set; } = null!;
    public IReadOnlyList<WorkTask> RecentlyClosed { get; private set; } = Array.Empty<WorkTask>();
    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();
    public int ClosedWindowDays => RecentlyClosedDays;

    public async Task OnGetAsync()
    {
        Backlog = await _tasks.GetBacklogAsync();
        RecentlyClosed = await _tasks.GetRecentlyClosedAsync(RecentlyClosedDays);
        Projects = await _projects.ListAsync();
    }

    public TaskRowModel Row(WorkTask task, bool showProject) => new(task, Projects, _clock.Today, Request.Path + Request.QueryString, showProject);
}
