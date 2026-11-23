namespace TaskOrganizer.Web.Domain;

/// <summary>A reason offered while typing why a task is on hold. The list is managed in Settings.</summary>
public class HoldReasonSuggestion
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
}
