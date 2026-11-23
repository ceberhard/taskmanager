using Microsoft.AspNetCore.Mvc;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

/// <summary>Tasks left out of a proposal, for one project, offered for swapping in.</summary>
public record SwapGroup(string Name, bool IsFocus, IReadOnlyList<SuggestedTask> Tasks);

/// <summary>
/// Planning a day in three steps: choose today's focus projects and the hours available,
/// look at the proposal, adjust it. Nothing is planned until the proposal is accepted.
/// </summary>
public class SuggestModel : ActionPageModel
{
    private readonly DaySuggestionService _suggestions;
    private readonly ProjectService _projects;
    private readonly SettingsService _settings;
    private readonly TaskService _tasks;

    public SuggestModel(DaySuggestionService suggestions, ProjectService projects, SettingsService settings, TaskService tasks)
    {
        _suggestions = suggestions;
        _projects = projects;
        _settings = settings;
        _tasks = tasks;
    }

    /// <summary>What needs dealing with before a new day is proposed: sign-offs and follow-ups.</summary>
    public ReviewSectionsModel Review { get; private set; } = null!;

    /// <summary>Today's focus projects (0 = unfiled). Chosen each day; kept in the address while planning.</summary>
    [BindProperty(SupportsGet = true)]
    public List<int> Focus { get; set; } = new();

    /// <summary>Hours available today. Empty means the daily plan target.</summary>
    [BindProperty(SupportsGet = true)]
    public double? Hours { get; set; }

    /// <summary>True once step 1 has been submitted and a proposal should be shown.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Proposed { get; set; }

    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();
    public AppSettings Settings { get; private set; } = null!;
    public DayProposal? Proposal { get; private set; }
    public IReadOnlyList<SwapGroup> SwapGroups { get; private set; } = Array.Empty<SwapGroup>();

    public double HoursShown => Hours ?? Settings.DailyPlanTargetHours;

    public async Task OnGetAsync()
    {
        Projects = await _projects.ListAsync();
        Settings = await _settings.GetAsync();
        Review = new ReviewSectionsModel(await _tasks.GetTodayAsync(), Projects, Request.CurrentUrl());
        if (!Proposed)
        {
            await PrefillFromTodaysPlanAsync();
            return;
        }

        await TryRunAsync(
            async () =>
            {
                Proposal = await _suggestions.SuggestAsync(new SuggestionRequest(Focus, Hours));
                SwapGroups = GroupForSwapping(Proposal.LeftOut);
                return null;
            },
            "The hours available could not be read.");
    }

    public async Task<IActionResult> OnPostCommitAsync([FromForm] List<int> taskIds, [FromForm] List<int> suggestedIds)
    {
        bool accepted = await TryRunAsync(
            async () =>
            {
                int planned = await _suggestions.AcceptAsync(new AcceptRequest(taskIds, suggestedIds, Focus, Hours));
                return planned == 1 ? "1 task planned for today." : $"{planned} tasks planned for today.";
            },
            "Nothing was planned. The selection could not be read.");

        return accepted ? RedirectToPage("/Index") : Redirect(ProposalUrl());
    }

    /// <summary>
    /// When a suggestion was already accepted today, step 1 starts from that day's focus projects
    /// and hours, so asking again later in the day does not mean choosing them again.
    /// </summary>
    private async Task PrefillFromTodaysPlanAsync()
    {
        if (Focus.Count > 0 || Hours is not null)
        {
            return;
        }

        DayPlan? todaysPlan = await _suggestions.GetTodaysPlanAsync();
        if (todaysPlan is not null)
        {
            Focus = todaysPlan.FocusProjects.Select(f => f.ProjectId ?? BacklogFilter.UnfiledProjectId).ToList();
            Hours = todaysPlan.HoursAvailable;
        }
    }

    /// <summary>Left-out tasks by project: focus projects first, then the order projects are listed in everywhere else.</summary>
    private List<SwapGroup> GroupForSwapping(IReadOnlyList<SuggestedTask> leftOut)
    {
        return leftOut
            .GroupBy(item => item.Task.ProjectId)
            .Select(group => new
            {
                Group = new SwapGroup(group.First().Task.Project?.Name ?? "Unfiled", group.First().IsFocus, group.ToList()),
                SortOrder = group.First().Task.Project?.SortOrder ?? int.MinValue
            })
            .OrderBy(entry => entry.Group.IsFocus ? 0 : 1)
            .ThenBy(entry => entry.SortOrder)
            .Select(entry => entry.Group)
            .ToList();
    }

    /// <summary>The address of the proposal for the current focus and hours.</summary>
    private string ProposalUrl()
    {
        List<KeyValuePair<string, string>> query = new();
        query.AddRange(Focus.Select(id => KeyValuePair.Create(nameof(Focus), id.ToString())));
        if (Hours is double hours)
        {
            query.Add(KeyValuePair.Create(nameof(Hours), Display.NumberInput(hours)));
        }

        query.Add(KeyValuePair.Create(nameof(Proposed), "true"));
        return Url.PageWithQuery("/Suggest", query);
    }
}
