namespace TaskOrganizer.Web.Domain;

public class TimeEntry
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly StartTime { get; set; }

    /// <summary>Null while the entry is still running.</summary>
    public TimeOnly? EndTime { get; set; }

    /// <summary>Work unless flagged as PTO, Holiday or Sick time.</summary>
    public TimeEntryKind Kind { get; set; } = TimeEntryKind.Work;
}
