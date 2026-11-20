using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

public record DayLog(DateOnly Date, IReadOnlyList<TimeEntry> Entries, double TotalHours);

/// <summary>
/// One Monday-to-Sunday week, with the same figures as the old spreadsheet's Overall tab.
/// RolloverInHours is the balance carried in: positive means ahead of goal.
/// </summary>
public record WeekSummary(DateOnly WeekStart, IReadOnlyList<DayLog> Days, double WeeklyTotalHours, double WeeklyGoalHours, double RolloverInHours)
{
    public double WeeklyRemainingHours => WeeklyGoalHours - WeeklyTotalHours;

    public double OverallTotalHours => RolloverInHours + WeeklyTotalHours;

    public double OverallRemainingHours => WeeklyGoalHours - OverallTotalHours;
}

public record ClockStatus(TimeEntry? RunningEntry, TimeEntry? StaleOpenEntry, double TodayHours);

public class TimeLogService
{
    /// <summary>SQLITE_CONSTRAINT: a uniqueness or other constraint rejected the write.</summary>
    private const int SqliteConstraintErrorCode = 19;

    private readonly AppDbContext _db;
    private readonly LocalClock _clock;
    private readonly SettingsService _settings;

    public TimeLogService(AppDbContext db, LocalClock clock, SettingsService settings)
    {
        _db = db;
        _clock = clock;
        _settings = settings;
    }

    public static DateOnly WeekStartOf(DateOnly date)
    {
        int daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    public async Task<TimeEntry> ClockInAsync()
    {
        TimeEntry entry = new() { Date = _clock.Today };
        await ApplyTimesAsync(entry, _clock.TimeOfDayToMinute, null);
        _db.TimeEntries.Add(entry);
        await SaveEntryAsync(entry);
        return entry;
    }

    /// <summary>
    /// Stops the running entry. Returns null when it was started in this same minute:
    /// that is an accidental click, and an entry of no length is discarded rather than kept.
    /// </summary>
    public async Task<TimeEntry?> ClockOutAsync()
    {
        TimeEntry open = await _db.TimeEntries.FirstOrDefaultAsync(e => e.EndTime == null)
            ?? throw new DomainRuleException("There is no running entry to stop.");
        if (open.Date != _clock.Today)
        {
            // Guessing an end time for a forgotten entry would put invented hours in the log.
            throw new DomainRuleException($"The open entry is from {open.Date:yyyy-MM-dd}. Set its end time on the Time page.");
        }

        TimeOnly now = _clock.TimeOfDayToMinute;
        if (now <= open.StartTime)
        {
            _db.TimeEntries.Remove(open);
            await _db.SaveChangesAsync();
            return null;
        }

        await ApplyTimesAsync(open, open.StartTime, now);
        await SaveEntryAsync(open);
        return open;
    }

    public async Task<TimeEntry> AddEntryAsync(DateOnly date, TimeOnly start, TimeOnly? end)
    {
        TimeEntry entry = new() { Date = date };
        await ApplyTimesAsync(entry, start, end);
        _db.TimeEntries.Add(entry);
        await SaveEntryAsync(entry);
        return entry;
    }

    public async Task UpdateEntryAsync(int id, TimeOnly start, TimeOnly? end)
    {
        TimeEntry entry = await _db.TimeEntries.FindAsync(id) ?? throw new DomainRuleException("Time entry not found.");
        await ApplyTimesAsync(entry, start, end);
        await SaveEntryAsync(entry);
    }

    public async Task DeleteEntryAsync(int id)
    {
        TimeEntry entry = await _db.TimeEntries.FindAsync(id) ?? throw new DomainRuleException("Time entry not found.");
        _db.TimeEntries.Remove(entry);
        await _db.SaveChangesAsync();
    }

    public async Task<ClockStatus> GetClockStatusAsync()
    {
        DateOnly today = _clock.Today;
        List<TimeEntry> relevant = await _db.TimeEntries.Where(e => e.Date == today || e.EndTime == null).ToListAsync();
        TimeEntry? running = relevant.FirstOrDefault(e => e.EndTime is null && e.Date == today);
        TimeEntry? stale = relevant.FirstOrDefault(e => e.EndTime is null && e.Date != today);
        double todayHours = relevant.Where(e => e.Date == today).Sum(DurationHours);
        return new ClockStatus(running, stale, todayHours);
    }

    public async Task<WeekSummary> GetWeekAsync(DateOnly anyDateInWeek)
    {
        DateOnly weekStart = WeekStartOf(anyDateInWeek);
        DateOnly weekEnd = weekStart.AddDays(6);
        AppSettings settings = await _settings.GetAsync();

        List<TimeEntry> entries = await _db.TimeEntries
            .Where(e => e.Date >= weekStart && e.Date <= weekEnd)
            .ToListAsync();

        List<DayLog> days = new();
        for (int offset = 0; offset < 7; offset++)
        {
            DateOnly date = weekStart.AddDays(offset);
            List<TimeEntry> dayEntries = entries.Where(e => e.Date == date).OrderBy(e => e.StartTime).ToList();
            days.Add(new DayLog(date, dayEntries, dayEntries.Sum(DurationHours)));
        }

        double rolloverIn = await GetRolloverIntoAsync(weekStart, settings);
        return new WeekSummary(weekStart, days, days.Sum(d => d.TotalHours), settings.WeeklyGoalHours, rolloverIn);
    }

    /// <summary>
    /// Hours for one entry. An entry still running today counts up to now; an open entry
    /// from another day counts as zero until its end time is filled in.
    /// </summary>
    private double DurationHours(TimeEntry entry)
    {
        if (entry.EndTime is TimeOnly end)
        {
            return (end - entry.StartTime).TotalHours;
        }

        if (entry.Date == _clock.Today && _clock.TimeOfDayToMinute > entry.StartTime)
        {
            return (_clock.TimeOfDayToMinute - entry.StartTime).TotalHours;
        }

        return 0;
    }

    /// <summary>
    /// Balance carried into a week: the opening balance plus (hours worked - weekly goal) for each
    /// earlier week that has at least one entry. Weeks with nothing logged (leave, or before the
    /// app was in use) are skipped rather than counted as a full week missed.
    /// </summary>
    private async Task<double> GetRolloverIntoAsync(DateOnly weekStart, AppSettings settings)
    {
        DateOnly? firstCountedWeek = settings.RolloverStartDate is DateOnly startDate ? WeekStartOf(startDate) : null;
        if (firstCountedWeek is DateOnly first && weekStart < first)
        {
            return 0;
        }

        List<TimeEntry> earlier = await _db.TimeEntries
            .Where(e => e.Date < weekStart && e.EndTime != null)
            .ToListAsync();

        double balance = settings.OpeningRolloverHours;
        IEnumerable<IGrouping<DateOnly, TimeEntry>> weeks = earlier
            .Where(e => firstCountedWeek is null || e.Date >= firstCountedWeek)
            .GroupBy(e => WeekStartOf(e.Date));
        foreach (IGrouping<DateOnly, TimeEntry> week in weeks)
        {
            balance += week.Sum(DurationHours) - settings.WeeklyGoalHours;
        }

        return balance;
    }

    /// <summary>
    /// Validates and sets an entry's times. An entry with no end time is treated as running
    /// to the end of its day when checking for overlap, so hours can never be counted twice.
    /// </summary>
    private async Task ApplyTimesAsync(TimeEntry entry, TimeOnly start, TimeOnly? end)
    {
        TimeOnly cleanStart = new(start.Hour, start.Minute);
        TimeOnly? cleanEnd = end is TimeOnly value ? new TimeOnly(value.Hour, value.Minute) : null;
        if (cleanEnd is TimeOnly endTime && endTime <= cleanStart)
        {
            throw new DomainRuleException("End time must be after start time.");
        }

        List<TimeEntry> sameDay = await _db.TimeEntries
            .Where(e => e.Date == entry.Date && e.Id != entry.Id)
            .ToListAsync();
        foreach (TimeEntry other in sameDay)
        {
            if (cleanEnd is null && other.EndTime is null)
            {
                // Two open entries: left to the database rule in SaveEntryAsync, which gives the clearer message.
                continue;
            }

            TimeOnly effectiveEnd = cleanEnd ?? TimeOnly.MaxValue;
            TimeOnly otherEnd = other.EndTime ?? TimeOnly.MaxValue;
            if (cleanStart < otherEnd && other.StartTime < effectiveEnd)
            {
                string otherRange = other.EndTime is TimeOnly closed
                    ? $"{other.StartTime:HH\\:mm} to {closed:HH\\:mm}"
                    : $"the entry running since {other.StartTime:HH\\:mm}";
                throw new DomainRuleException($"That overlaps {otherRange} on {entry.Date:yyyy-MM-dd}.");
            }
        }

        entry.StartTime = cleanStart;
        entry.EndTime = cleanEnd;
    }

    /// <summary>
    /// Saves an entry. "Only one open entry" is enforced by a unique index rather than a
    /// check-then-write, so a double-click on Clock in cannot leave two entries running.
    /// </summary>
    private async Task SaveEntryAsync(TimeEntry entry)
    {
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode })
        {
            // Drop the rejected change so the context can still be used for the lookup below.
            _db.ChangeTracker.Clear();
            TimeEntry? open = await _db.TimeEntries.FirstOrDefaultAsync(e => e.EndTime == null && e.Id != entry.Id);
            string detail = open is null
                ? "Another entry is already open."
                : $"An entry started {open.Date:yyyy-MM-dd} at {open.StartTime:HH\\:mm} is still open.";
            throw new DomainRuleException($"{detail} Close it first; only one entry can be running.");
        }
    }
}
