using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

/// <summary>One day's time entries with their correct/delete/add forms, shared by the Time and Today pages.</summary>
public record DayTimeEntriesModel(DayLog Day, DateOnly Today, string ReturnUrl)
{
    public bool IsToday => Day.Date == Today;
}
