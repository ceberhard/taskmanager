using Microsoft.AspNetCore.Mvc.RazorPages;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

public class IndexModel : PageModel
{
    private readonly TaskService _tasks;
    private readonly ProjectService _projects;

    public IndexModel(TaskService tasks, ProjectService projects)
    {
        _tasks = tasks;
        _projects = projects;
    }

    public TodayView Today { get; private set; } = null!;
    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();

    public async Task OnGetAsync()
    {
        Today = await _tasks.GetTodayAsync();
        Projects = await _projects.ListAsync();
    }

    public TaskRowModel Row(WorkTask task) => new(task, Projects, Today.Date, Request.Path + Request.QueryString);
}
