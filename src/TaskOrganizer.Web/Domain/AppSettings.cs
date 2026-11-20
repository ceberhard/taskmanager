namespace TaskOrganizer.Web.Domain;

/// <summary>Single-row table (Id is always 1).</summary>
public class AppSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public double WeeklyGoalHours { get; set; } = 40;

    /// <summary>Hours balance carried in from before this app was used. Positive means ahead of goal.</summary>
    public double OpeningRolloverHours { get; set; }

    /// <summary>Weeks before this date are ignored when carrying the balance forward. Null counts every logged week.</summary>
    public DateOnly? RolloverStartDate { get; set; }

    public double DailyPlanTargetHours { get; set; } = 9;
}
