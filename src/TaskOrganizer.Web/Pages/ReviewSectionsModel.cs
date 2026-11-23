using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

/// <summary>
/// The two things to deal with before planning a day: work marked done on an earlier day that
/// still needs signing off, and on-hold tasks whose check-back day has arrived.
/// Shown at the top of both Today and Suggest tasks.
/// </summary>
public record ReviewSectionsModel(TodayView Today, IReadOnlyList<Project> Projects, string ReturnUrl)
{
    public bool HasAnything => Today.AwaitingReview.Count > 0 || Today.FollowUps.Count > 0;

    public TaskRowModel Row(WorkTask task) => new(task, Projects, Today.Date, ReturnUrl);
}
