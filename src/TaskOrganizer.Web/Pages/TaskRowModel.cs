using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Pages;

/// <summary>Everything the shared task row needs to render one task and its buttons.</summary>
public record TaskRowModel(WorkTask Task, IReadOnlyList<Project> Projects, DateOnly Today, string ReturnUrl, bool ShowProject = true)
{
    /// <summary>
    /// True where the day's work is being done (Today): the main button is then "Done for today",
    /// with plain Done beside it. Elsewhere only plain Done is offered.
    /// </summary>
    public bool OfferDoneForToday { get; init; }

    public bool IsFollowUpDue => Task.Status == WorkTaskStatus.OnHold && Task.HoldCheckBackOn <= Today;

    public bool IsPlannedToday => Task.PlannedFor == Today;

    public bool IsCarriedOver => Task.IsWorkable && Task.PlannedFor < Today;

    public bool IsOverdue => Task.IsOpen && Task.DueDate < Today;
}
