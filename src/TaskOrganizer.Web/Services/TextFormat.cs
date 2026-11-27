using System.Globalization;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

/// <summary>Words and numbers as people read them, shared by pages, services and the local API.</summary>
public static class TextFormat
{
    public static string Hours(double hours) => hours.ToString("0.##", CultureInfo.InvariantCulture) + " h";

    public static string Status(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.ToDo => "To do",
        WorkTaskStatus.InProgress => "In progress",
        WorkTaskStatus.OnHold => "On hold",
        WorkTaskStatus.DoneForToday => "Done for today",
        _ => status.ToString()
    };
}
