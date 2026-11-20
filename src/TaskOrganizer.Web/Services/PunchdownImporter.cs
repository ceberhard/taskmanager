using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

public record ImportResult(int ProjectsCreated, int TasksCreated, int TasksSkipped, IReadOnlyList<string> Warnings);

/// <summary>
/// One-time import of the "Daily Punchdown" sheet. A heading in column A starts a project;
/// rows under it hold a task title in column B and a status in column C.
/// The workbook is opened read-only and never written to.
/// </summary>
public class PunchdownImporter
{
    public const string SheetName = "Daily Punchdown";
    private const string HeaderTaskCell = "Tasks";

    private readonly AppDbContext _db;
    private readonly LocalClock _clock;

    public PunchdownImporter(AppDbContext db, LocalClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<ImportResult> ImportAsync(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new DomainRuleException($"Workbook not found at {workbookPath}.");
        }

        // FileShare.ReadWrite lets the import run while the workbook is open in Excel.
        using FileStream stream = new(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await ImportAsync(stream);
    }

    public async Task<ImportResult> ImportAsync(Stream workbookStream)
    {
        List<ParsedRow> rows = ReadRows(workbookStream, out List<string> warnings);

        DateTime now = _clock.UtcNow;
        List<Project> projects = await _db.Projects.ToListAsync();
        HashSet<string> existingKeys = (await _db.Tasks.Where(t => t.ImportKey != null).Select(t => t.ImportKey!).ToListAsync()).ToHashSet();
        int nextSortOrder = projects.Count == 0 ? 1 : projects.Max(p => p.SortOrder) + 1;
        int projectsCreated = 0;
        int tasksCreated = 0;
        int tasksSkipped = 0;

        foreach (ParsedRow row in rows)
        {
            Project? project = projects.FirstOrDefault(p => string.Equals(p.Name, row.ProjectName, StringComparison.OrdinalIgnoreCase));
            if (project is null)
            {
                project = new Project { Name = row.ProjectName, SortOrder = nextSortOrder++, CreatedAtUtc = now };
                _db.Projects.Add(project);
                projects.Add(project);
                projectsCreated++;
            }

            if (row.Title is null)
            {
                continue;
            }

            string importKey = $"punchdown|{row.ProjectName}|{row.Title}";
            if (!existingKeys.Add(importKey))
            {
                tasksSkipped++;
                continue;
            }

            WorkTask task = new()
            {
                Title = row.Title,
                Project = project,
                Status = row.Status,
                Source = TaskSource.Import,
                CreatedAtUtc = now,
                ImportKey = importKey
            };
            _db.Tasks.Add(task);
            _db.TaskStatusChanges.Add(new TaskStatusChange { WorkTask = task, FromStatus = null, ToStatus = row.Status, ChangedAtUtc = now });
            tasksCreated++;
        }

        await _db.SaveChangesAsync();
        return new ImportResult(projectsCreated, tasksCreated, tasksSkipped, warnings);
    }

    /// <summary>A heading row has a null Title; it exists so projects with no tasks are still created.</summary>
    private sealed record ParsedRow(string ProjectName, string? Title, WorkTaskStatus Status);

    private static List<ParsedRow> ReadRows(Stream workbookStream, out List<string> warnings)
    {
        warnings = new List<string>();
        using XLWorkbook workbook = new(workbookStream);
        if (!workbook.TryGetWorksheet(SheetName, out IXLWorksheet? sheet) || sheet is null)
        {
            throw new DomainRuleException($"The workbook has no sheet named '{SheetName}'.");
        }

        int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        int headerRow = 0;
        for (int rowNumber = 1; rowNumber <= lastRow; rowNumber++)
        {
            if (string.Equals(CellText(sheet, rowNumber, 1), HeaderTaskCell, StringComparison.OrdinalIgnoreCase))
            {
                headerRow = rowNumber;
                break;
            }
        }

        if (headerRow == 0)
        {
            throw new DomainRuleException($"Could not find the '{HeaderTaskCell}' header row in sheet '{SheetName}'.");
        }

        List<ParsedRow> rows = new();
        string? currentProject = null;
        for (int rowNumber = headerRow + 1; rowNumber <= lastRow; rowNumber++)
        {
            string heading = CellText(sheet, rowNumber, 1);
            string title = CellText(sheet, rowNumber, 2);
            string statusText = CellText(sheet, rowNumber, 3);

            if (heading.Length > 0)
            {
                currentProject = heading.Length > 100 ? heading[..100] : heading;
                rows.Add(new ParsedRow(currentProject, null, WorkTaskStatus.ToDo));
            }

            if (title.Length == 0)
            {
                continue;
            }

            if (currentProject is null)
            {
                warnings.Add($"Row {rowNumber}: task appears before any project heading and was skipped.");
                continue;
            }

            WorkTaskStatus status = ParseStatus(statusText, rowNumber, warnings);
            rows.Add(new ParsedRow(currentProject, title.Length > 1000 ? title[..1000] : title, status));
        }

        return rows;
    }

    private static WorkTaskStatus ParseStatus(string statusText, int rowNumber, List<string> warnings)
    {
        switch (statusText.ToUpperInvariant())
        {
            case "TO DO":
                return WorkTaskStatus.ToDo;
            case "IN PROGRESS":
                return WorkTaskStatus.InProgress;
            default:
                warnings.Add($"Row {rowNumber}: status '{statusText}' is not recognised; imported as To Do.");
                return WorkTaskStatus.ToDo;
        }
    }

    private static string CellText(IXLWorksheet sheet, int row, int column)
    {
        return sheet.Cell(row, column).GetString().Trim();
    }
}
