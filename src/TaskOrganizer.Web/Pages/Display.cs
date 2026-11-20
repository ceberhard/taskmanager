using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Pages;

/// <summary>Formatting shared by every page.</summary>
public static partial class Display
{
    public static readonly IReadOnlyList<TaskPriority> Priorities = Enum.GetValues<TaskPriority>().OrderBy(p => (int)p).ToList();

    public static string Priority(TaskPriority priority) => priority == TaskPriority.VeryHigh ? "Very High" : priority.ToString();

    public static string PriorityCss(TaskPriority priority) => "prio-" + priority.ToString().ToLowerInvariant();

    public static string Status(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.ToDo => "To do",
        WorkTaskStatus.InProgress => "In progress",
        _ => status.ToString()
    };

    public static string Hours(double hours) => hours.ToString("0.##", CultureInfo.InvariantCulture) + " h";

    public static string SignedHours(double hours) => hours.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " h";

    /// <summary>Value for a number input, which always expects a dot as the decimal separator.</summary>
    public static string NumberInput(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string TimeInput(TimeOnly? time) => time?.ToString("HH\\:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string DateInput(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Encodes a task title and turns any http(s) links in it into anchors.</summary>
    public static IHtmlContent Linkify(string text)
    {
        HtmlContentBuilder builder = new();
        int position = 0;
        foreach (Match match in UrlPattern().Matches(text))
        {
            builder.Append(text[position..match.Index]);
            string url = HtmlEncoder.Default.Encode(match.Value);
            builder.AppendHtml($"<a href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer\">{url}</a>");
            position = match.Index + match.Length;
        }

        builder.Append(text[position..]);
        return builder;
    }

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
