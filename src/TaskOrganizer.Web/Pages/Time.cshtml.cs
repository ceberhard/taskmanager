using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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

    public DayTimeEntriesModel Entries(DayLog day) => new(day, Today, Request.CurrentUrl());
}
