namespace TaskOrganizer.Web.Services;

/// <summary>The rules for any figure measured in hours: task sizes, the assumed size, daily totals.</summary>
public static class HoursRules
{
    /// <summary>Sizes are rough: nothing is tracked finer than fifteen minutes.</summary>
    public const double StepHours = 0.25;

    /// <summary>Slack allowed when comparing sums of hours, to absorb floating-point error.</summary>
    public const double Tolerance = 1e-9;

    public const string StepProblem = "in hours, in 15-minute steps, with a minimum of 0.25";

    /// <summary>True when the value is at least one step and a whole number of steps.</summary>
    public static bool IsValidSize(double hours)
    {
        double steps = hours / StepHours;
        bool isWholeSteps = Math.Abs(steps - Math.Round(steps)) < Tolerance;
        return hours >= StepHours && isWholeSteps;
    }

    /// <summary>True when the hours go past the limit by more than rounding error.</summary>
    public static bool Exceeds(double hours, double limit) => hours > limit + Tolerance;
}
