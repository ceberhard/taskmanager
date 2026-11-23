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

    /// <summary>Share of a suggested day, in percent, that goes to the projects chosen as today's focus.</summary>
    public int FocusSharePercent { get; set; } = 70;

    /// <summary>Hours a task with no size is counted as when a day is suggested.</summary>
    public double AssumedUnsizedHours { get; set; } = 1;

    /// <summary>A task due within this many working days is pulled forward in a suggestion.</summary>
    public int DueSoonWorkingDays { get; set; } = 3;
}
