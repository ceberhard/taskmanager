using Microsoft.AspNetCore.Mvc;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.ViewComponents;

public record CaptureBarModel(IReadOnlyList<Project> Projects, ClockStatus Clock, string ReturnUrl, bool FocusQuickAdd);

/// <summary>The strip on every page: quick-add, the interruption button and the clock.</summary>
public class CaptureBarViewComponent : ViewComponent
{
    private readonly ProjectService _projects;
    private readonly TimeLogService _timeLog;

    public CaptureBarViewComponent(ProjectService projects, TimeLogService timeLog)
    {
        _projects = projects;
        _timeLog = timeLog;
    }

    public async Task<IViewComponentResult> InvokeAsync(bool focusQuickAdd)
    {
        List<Project> projects = await _projects.ListAsync();
        ClockStatus clock = await _timeLog.GetClockStatusAsync();
        string returnUrl = HttpContext.Request.Path + HttpContext.Request.QueryString;
        return View(new CaptureBarModel(projects, clock, returnUrl, focusQuickAdd));
    }
}
