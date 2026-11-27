namespace TaskOrganizer.Web.Services;

public class UiOptions
{
    public const string SectionName = "Ui";

    /// <summary>How often, while a time entry is running, the time figures on the page are refreshed.</summary>
    public int TimeRefreshSeconds { get; set; } = 60;
}
