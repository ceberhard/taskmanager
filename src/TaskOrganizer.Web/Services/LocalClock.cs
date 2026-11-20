namespace TaskOrganizer.Web.Services;

/// <summary>
/// The single source of "now" for the business rules. Wraps TimeProvider so tests
/// can fix the date and time instead of waiting for midnight.
/// </summary>
public class LocalClock
{
    private readonly TimeProvider _timeProvider;

    public LocalClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public DateTime LocalNow => _timeProvider.GetLocalNow().DateTime;

    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    /// <summary>Current local time of day, cut to the minute; the time log does not track seconds.</summary>
    public TimeOnly TimeOfDayToMinute
    {
        get
        {
            DateTime now = LocalNow;
            return new TimeOnly(now.Hour, now.Minute);
        }
    }

    public DateOnly ToLocalDate(DateTime utc)
    {
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _timeProvider.LocalTimeZone);
        return DateOnly.FromDateTime(local);
    }
}
