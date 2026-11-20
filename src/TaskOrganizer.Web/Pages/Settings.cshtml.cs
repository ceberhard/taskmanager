using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

public class SettingsModel : PageModel
{
    private readonly SettingsService _settings;
    private readonly ProjectService _projects;
    private readonly PunchdownImporter _importer;
    private readonly TimeLogImporter _timeImporter;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public SettingsModel(SettingsService settings, ProjectService projects, PunchdownImporter importer, TimeLogImporter timeImporter, IConfiguration configuration, IWebHostEnvironment environment)
    {
        _settings = settings;
        _projects = projects;
        _importer = importer;
        _timeImporter = timeImporter;
        _configuration = configuration;
        _environment = environment;
    }

    private const string RolloverOfferKey = "RolloverOffer";

    public AppSettings Current { get; private set; } = null!;

    /// <summary>The Rollover figure found by the last time import, waiting for a yes or no. Null when there is nothing to offer.</summary>
    public double? OfferedRolloverHours { get; private set; }
    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();

    public string WorkbookPath => Path.GetFullPath(
        _configuration["Import:WorkbookPath"] ?? "../../refs/Time_Log.xlsx",
        _environment.ContentRootPath);

    public async Task OnGetAsync()
    {
        Current = await _settings.GetAsync();
        Projects = await _projects.ListAsync();
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
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Nothing was saved. Check that the numbers and dates are filled in correctly.";
        }
        else
        {
            try
            {
                TempData["Flash"] = await action();
            }
            catch (DomainRuleException ex)
            {
                TempData["Error"] = ex.Message;
            }
            catch (IOException ex)
            {
                TempData["Error"] = $"Could not read the workbook: {ex.Message}";
            }
        }

        return RedirectToPage();
    }
}
