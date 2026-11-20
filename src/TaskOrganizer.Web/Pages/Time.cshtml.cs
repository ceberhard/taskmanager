using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

public class TimeModel : PageModel
{
    private readonly TimeLogService _timeLog;
    private readonly LocalClock _clock;

    public TimeModel(TimeLogService timeLog, LocalClock clock)
    {
        _timeLog = timeLog;
        _clock = clock;
    }

    /// <summary>Any date inside the week to show. Defaults to the current week.</summary>
    [BindProperty(SupportsGet = true)]
    public DateOnly? Week { get; set; }

    public WeekSummary Summary { get; private set; } = null!;
    public DateOnly Today => _clock.Today;

    public async Task OnGetAsync()
    {
        Summary = await _timeLog.GetWeekAsync(Week ?? _clock.Today);
    }

    public Task<IActionResult> OnPostAddAsync(DateOnly date, TimeOnly start, TimeOnly? end)
    {
        return RunAsync(() => _timeLog.AddEntryAsync(date, start, end));
    }

    public Task<IActionResult> OnPostUpdateAsync(int id, TimeOnly start, TimeOnly? end)
    {
        return RunAsync(() => _timeLog.UpdateEntryAsync(id, start, end));
    }

    public Task<IActionResult> OnPostDeleteAsync(int id)
    {
        return RunAsync(() => _timeLog.DeleteEntryAsync(id));
    }

    private async Task<IActionResult> RunAsync(Func<Task> action)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Nothing was saved. Enter times as HH:MM.";
        }
        else
        {
            try
            {
                await action();
            }
            catch (DomainRuleException ex)
            {
                TempData["Error"] = ex.Message;
            }
        }

        return RedirectToPage(new { week = Week?.ToString("yyyy-MM-dd") });
    }
}
