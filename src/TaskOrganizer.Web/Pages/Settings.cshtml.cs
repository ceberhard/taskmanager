using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Integrations;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

public class SettingsModel : ActionPageModel
{
    private readonly SettingsService _settings;
    private readonly ProjectService _projects;
    private readonly PunchdownImporter _importer;
    private readonly TimeLogImporter _timeImporter;
    private readonly HoldReasonService _holdReasons;
    private readonly IntegrationSettingsService _integrations;
    private readonly JiraClient _jira;
    private readonly AzureDevOpsClient _azureDevOps;
    private readonly string _credentialFolder;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public SettingsModel(SettingsService settings, ProjectService projects, PunchdownImporter importer, TimeLogImporter timeImporter, HoldReasonService holdReasons, IntegrationSettingsService integrations, JiraClient jira, AzureDevOpsClient azureDevOps, CredentialStore credentials, IConfiguration configuration, IWebHostEnvironment environment)
    {
        _settings = settings;
        _projects = projects;
        _importer = importer;
        _timeImporter = timeImporter;
        _holdReasons = holdReasons;
        _integrations = integrations;
        _jira = jira;
        _azureDevOps = azureDevOps;
        _credentialFolder = credentials.Folder;
        _configuration = configuration;
        _environment = environment;
    }

    private const string RolloverOfferKey = "RolloverOffer";

    public AppSettings Current { get; private set; } = null!;

    /// <summary>The Rollover figure found by the last time import, waiting for a yes or no. Null when there is nothing to offer.</summary>
    public double? OfferedRolloverHours { get; private set; }
    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();
    public IReadOnlyList<HoldReasonSuggestion> HoldReasons { get; private set; } = Array.Empty<HoldReasonSuggestion>();
    public IReadOnlyList<JiraProjectMapping> JiraMappings { get; private set; } = Array.Empty<JiraProjectMapping>();
    public bool HasJiraToken { get; private set; }
    public bool HasAzureDevOpsToken { get; private set; }
    public string CredentialFolder => _credentialFolder;

    public string WorkbookPath => Path.GetFullPath(
        _configuration["Import:WorkbookPath"] ?? "../../refs/Time_Log.xlsx",
        _environment.ContentRootPath);

    public async Task OnGetAsync()
    {
        Current = await _settings.GetAsync();
        Projects = await _projects.ListAsync();
        HoldReasons = await _holdReasons.ListAsync();
        JiraMappings = await _integrations.ListMappingsAsync();
        HasJiraToken = _integrations.HasJiraToken;
        HasAzureDevOpsToken = _integrations.HasAzureDevOpsToken;
        if (TempData[RolloverOfferKey] is string offered
            && double.TryParse(offered, NumberStyles.Float, CultureInfo.InvariantCulture, out double hours))
        {
            OfferedRolloverHours = hours;
        }
    }

    public Task<IActionResult> OnPostSaveAsync(double weeklyGoalHours, double openingRolloverHours, DateOnly? rolloverStartDate, double dailyPlanTargetHours)
    {
        return RunAsync(async () =>
        {
            await _settings.UpdateAsync(weeklyGoalHours, openingRolloverHours, rolloverStartDate, dailyPlanTargetHours);
            return "Settings saved.";
        });
    }

    public Task<IActionResult> OnPostSaveSuggestionAsync(int focusSharePercent, double assumedUnsizedHours, int dueSoonWorkingDays)
    {
        return RunAsync(async () =>
        {
            await _settings.UpdateSuggestionSettingsAsync(focusSharePercent, assumedUnsizedHours, dueSoonWorkingDays);
            return "Suggestion settings saved.";
        });
    }

    public Task<IActionResult> OnPostSaveJiraAsync(string? jiraSiteUrl, string? jiraEmail, string? jiraApiToken)
    {
        return RunAsync(async () =>
        {
            await _integrations.SaveJiraAsync(jiraSiteUrl, jiraEmail, jiraApiToken);
            return "Jira settings saved.";
        });
    }

    public Task<IActionResult> OnPostTestJiraAsync()
    {
        return RunAsync(async () => $"Jira works: signed in as {await _jira.TestAsync(await _integrations.GetJiraConnectionAsync())}.");
    }

    public Task<IActionResult> OnPostRemoveJiraTokenAsync()
    {
        return RunAsync(() =>
        {
            _integrations.RemoveToken(CredentialStore.JiraApiToken);
            return Task.FromResult("Jira API token removed from this machine.");
        });
    }

    public Task<IActionResult> OnPostAddJiraMappingAsync(string? prefix, int projectId)
    {
        return RunAsync(async () =>
        {
            await _integrations.AddMappingAsync(prefix, projectId);
            return "Mapping added.";
        });
    }

    public Task<IActionResult> OnPostRemoveJiraMappingAsync(int id)
    {
        return RunAsync(async () =>
        {
            await _integrations.RemoveMappingAsync(id);
            return "Mapping removed.";
        });
    }

    public Task<IActionResult> OnPostSaveAzureDevOpsAsync(string? azureDevOpsOrganizationUrl, string? azureDevOpsProject, string? azureDevOpsToken)
    {
        return RunAsync(async () =>
        {
            await _integrations.SaveAzureDevOpsAsync(azureDevOpsOrganizationUrl, azureDevOpsProject, azureDevOpsToken);
            return "Azure DevOps settings saved.";
        });
    }

    public Task<IActionResult> OnPostTestAzureDevOpsAsync()
    {
        return RunAsync(async () => $"Azure DevOps works: signed in as {await _azureDevOps.TestAsync(await _integrations.GetAzureDevOpsConnectionAsync())}.");
    }

    public Task<IActionResult> OnPostRemoveAzureDevOpsTokenAsync()
    {
        return RunAsync(() =>
        {
            _integrations.RemoveToken(CredentialStore.AzureDevOpsToken);
            return Task.FromResult("Azure DevOps token removed from this machine.");
        });
    }

    public Task<IActionResult> OnPostSavePullRequestDefaultsAsync(int? pullRequestProjectId, double pullRequestDefaultHours, TaskPriority pullRequestDefaultPriority)
    {
        return RunAsync(async () =>
        {
            await _integrations.SavePullRequestDefaultsAsync(pullRequestProjectId, pullRequestDefaultHours, pullRequestDefaultPriority);
            return "Pull request defaults saved.";
        });
    }

    public Task<IActionResult> OnPostAddHoldReasonAsync(string? text)
    {
        return RunAsync(async () =>
        {
            await _holdReasons.AddAsync(text);
            return "Hold reason added.";
        });
    }

    public Task<IActionResult> OnPostRemoveHoldReasonAsync(int id)
    {
        return RunAsync(async () =>
        {
            await _holdReasons.RemoveAsync(id);
            return "Hold reason removed.";
        });
    }

    public Task<IActionResult> OnPostAddProjectAsync(string? name)
    {
        return RunAsync(async () =>
        {
            Project project = await _projects.AddAsync(name);
            return $"Project '{project.Name}' added.";
        });
    }

    public Task<IActionResult> OnPostRenameProjectAsync(int id, string? name)
    {
        return RunAsync(async () =>
        {
            await _projects.RenameAsync(id, name);
            return "Project renamed.";
        });
    }

    public Task<IActionResult> OnPostImportAsync()
    {
        return RunAsync(async () =>
        {
            ImportResult result = await _importer.ImportAsync(WorkbookPath);
            string summary = $"Import finished: {result.ProjectsCreated} projects and {result.TasksCreated} tasks added, {result.TasksSkipped} already present.";
            return result.Warnings.Count == 0 ? summary : summary + " Warnings: " + string.Join(" ", result.Warnings);
        });
    }

    public Task<IActionResult> OnPostImportTimeAsync()
    {
        return RunAsync(async () =>
        {
            TimeImportResult result = await _timeImporter.ImportAsync(WorkbookPath);
            if (result.RolloverHours is double rollover)
            {
                // Offered, never applied here: the balance only changes when the person says so.
                TempData[RolloverOfferKey] = rollover.ToString("R", CultureInfo.InvariantCulture);
            }

            string summary = $"Time import finished: {result.EntriesCreated} entries added, {result.EntriesSkipped} already present.";
            return result.Warnings.Count == 0 ? summary : summary + " Skipped: " + string.Join(" ", result.Warnings);
        });
    }

    public Task<IActionResult> OnPostApplyRolloverAsync(double hours)
    {
        return RunAsync(async () =>
        {
            await _settings.SetOpeningRolloverAsync(hours);
            return $"Opening rollover balance set to {Display.SignedHours(hours)}.";
        });
    }

    private async Task<IActionResult> RunAsync(Func<Task<string>> action)
    {
        await TryRunAsync(async () => await action());
        return RedirectToPage();
    }
}
