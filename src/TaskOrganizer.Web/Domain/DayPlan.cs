namespace TaskOrganizer.Web.Domain;

/// <summary>
/// Append-only record of one accepted suggestion: what the day was about and how much room it
/// had. A day can have several (a top-up later in the day); the latest one sets the hours
/// Today measures against. Kept because capacity per day cannot be worked out afterwards.
/// </summary>
public class DayPlan
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public DateTime AcceptedAtUtc { get; set; }

    /// <summary>Hours available that day, as entered when the suggestion was asked for.</summary>
    public double HoursAvailable { get; set; }

    /// <summary>How many tasks the proposal offered.</summary>
    public int TasksSuggested { get; set; }

    /// <summary>How many of the offered tasks were kept.</summary>
    public int TasksAcceptedFromSuggestion { get; set; }

    /// <summary>How many accepted tasks were not in the proposal but swapped in by hand.</summary>
    public int TasksSwappedIn { get; set; }

    public List<DayPlanFocusProject> FocusProjects { get; set; } = new();
}

/// <summary>One focus project of a day plan. A null project means the unfiled tasks were a focus.</summary>
public class DayPlanFocusProject
{
    public int Id { get; set; }
    public int DayPlanId { get; set; }
    public DayPlan? DayPlan { get; set; }
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
}
