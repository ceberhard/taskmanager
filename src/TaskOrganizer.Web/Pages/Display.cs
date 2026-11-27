using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

/// <summary>Formatting shared by every page.</summary>
public static partial class Display
{
    public static readonly IReadOnlyList<TaskPriority> Priorities = Enum.GetValues<TaskPriority>().OrderBy(p => (int)p).ToList();

    public static string Priority(TaskPriority priority) => priority == TaskPriority.VeryHigh ? "Very High" : priority.ToString();

    public static string PriorityCss(TaskPriority priority) => "prio-" + priority.ToString().ToLowerInvariant();

    public static string Status(WorkTaskStatus status) => TextFormat.Status(status);

    /// <summary>The statuses a task can be listed under while it is not closed, in the order they are offered as filters.</summary>
    public static readonly IReadOnlyList<WorkTaskStatus> OpenStatuses = new[]
    {
        WorkTaskStatus.ToDo, WorkTaskStatus.InProgress, WorkTaskStatus.OnHold, WorkTaskStatus.DoneForToday
    };

    public static readonly IReadOnlyList<TimeEntryKind> TimeEntryKinds = Enum.GetValues<TimeEntryKind>();

    public static string Kind(TimeEntryKind kind) => kind == TimeEntryKind.Pto ? "PTO" : kind.ToString();

    public static string KindCss(TimeEntryKind kind) => "kind-" + kind.ToString().ToLowerInvariant();

    /// <summary>What to call the link back to where a task came from.</summary>
    public static string ExternalLinkLabel(TaskSource source) => source switch
    {
        TaskSource.Jira => "open in Jira",
        TaskSource.PullRequest => "open pull request",
        TaskSource.Slack => "open in Slack",
        _ => "open link"
    };

    public static string Source(TaskSource source) => source == TaskSource.PullRequest ? "pull request" : source.ToString().ToLowerInvariant();

    /// <summary>CSS class that gives a task row the look of its state.</summary>
    public static string StateCss(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.InProgress => "in-progress",
        WorkTaskStatus.OnHold => "on-hold",
        WorkTaskStatus.DoneForToday => "done-for-today",
        _ => string.Empty
    };

    /// <summary>
    /// How many tasks are in progress at once, as a word and a colour: 1 to 3 is fine,
    /// 4 to 5 a warning, 6 to 7 too many, 8 or more danger.
    /// </summary>
    public static (string Label, string Css) InProgressLevel(int count) => count switch
    {
        0 => ("None in progress", "text-bg-secondary"),
        <= 3 => ("OK", "text-bg-success"),
        <= 5 => ("Warning", "text-bg-warning"),
        <= 7 => ("Too many", "level-too-many"),
        _ => ("Danger", "text-bg-danger")
    };

    public static string Hours(double hours) => TextFormat.Hours(hours);

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
