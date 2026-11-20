using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Pages;

/// <summary>Everything the shared task row needs to render one task and its buttons.</summary>
public record TaskRowModel(WorkTask Task, IReadOnlyList<Project> Projects, DateOnly Today, string ReturnUrl, bool ShowProject = true)
{
    public bool IsPlannedToday => Task.PlannedFor == Today;

    public bool IsCarriedOver => Task.IsOpen && Task.PlannedFor < Today;

    public bool IsOverdue => Task.IsOpen && Task.DueDate < Today;
}
