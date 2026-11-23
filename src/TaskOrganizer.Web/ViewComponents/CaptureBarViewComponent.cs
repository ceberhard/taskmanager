using Microsoft.AspNetCore.Mvc;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Pages;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.ViewComponents;

public record CaptureBarModel(IReadOnlyList<Project> Projects, ClockStatus Clock, string ReturnUrl, bool FocusQuickAdd, QuickAddDraft? Draft, IReadOnlyList<HoldReasonSuggestion> HoldReasons);

/// <summary>The strip on every page: quick-add, the interruption button and the clock.</summary>
public class CaptureBarViewComponent : ViewComponent
{
    private readonly ProjectService _projects;
    private readonly TimeLogService _timeLog;
    private readonly HoldReasonService _holdReasons;

    public CaptureBarViewComponent(ProjectService projects, TimeLogService timeLog, HoldReasonService holdReasons)
    {
        _projects = projects;
        _timeLog = timeLog;
        _holdReasons = holdReasons;
    }

    public async Task<IViewComponentResult> InvokeAsync(bool focusQuickAdd)
    {
        List<Project> projects = await _projects.ListAsync();
        ClockStatus clock = await _timeLog.GetClockStatusAsync();
        QuickAddDraft? draft = QuickAddDraft.Load(TempData);

        // A rejected quick-add puts the cursor back in the box, whichever page it was sent from.
        // The saved hold reasons are rendered once here, for every "put on hold" box on the page to share.
        List<HoldReasonSuggestion> holdReasons = await _holdReasons.ListAsync();
        return View(new CaptureBarModel(projects, clock, HttpContext.Request.CurrentUrl(), focusQuickAdd || draft is not null, draft, holdReasons));
    }
}
