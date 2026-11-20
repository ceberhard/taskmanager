using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Tests;

public class PunchdownImporterTests
{
    private static readonly DateTime ImportDay = new(2026, 11, 19, 7, 30, 0);

    /// <summary>Builds a workbook laid out like the real sheet, with invented content.</summary>
    private static MemoryStream BuildWorkbook(Action<IXLWorksheet>? customise = null)
    {
        using XLWorkbook workbook = new();
        workbook.AddWorksheet("Overall");
        IXLWorksheet sheet = workbook.AddWorksheet(PunchdownImporter.SheetName);
        sheet.Cell("A1").Value = "Punch Down List:";
        sheet.Cell("A3").Value = "Tasks";
        sheet.Cell("C3").Value = "Status";
        sheet.Cell("A4").Value = "GARDEN";
        sheet.Cell("B5").Value = "Plant the tomatoes";
        sheet.Cell("C5").Value = "IN PROGRESS";
        sheet.Cell("B6").Value = "Fix the gate";
        sheet.Cell("C6").Value = "TO DO";
        sheet.Cell("A8").Value = "EMPTY HEADING";
        sheet.Cell("A10").Value = "KITCHEN";
        sheet.Cell("B11").Value = "Descale the kettle";
        sheet.Cell("C11").Value = "TO DO";
        customise?.Invoke(sheet);

        MemoryStream stream = new();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task Headings_become_projects_and_rows_become_tasks_with_their_status()
    {
        using TestHost host = new(ImportDay);
        using MemoryStream workbook = BuildWorkbook();

        ImportResult result = await host.Importer.ImportAsync(workbook);

        Assert.Equal(3, result.ProjectsCreated);
        Assert.Equal(3, result.TasksCreated);
        Assert.Empty(result.Warnings);
        List<Project> projects = await host.Db.Projects.OrderBy(p => p.SortOrder).ToListAsync();
        Assert.Equal(new[] { "GARDEN", "EMPTY HEADING", "KITCHEN" }, projects.Select(p => p.Name));
        WorkTask tomatoes = await host.Db.Tasks.Include(t => t.Project).SingleAsync(t => t.Title == "Plant the tomatoes");
        Assert.Equal("GARDEN", tomatoes.Project!.Name);
        Assert.Equal(WorkTaskStatus.InProgress, tomatoes.Status);
        WorkTask gate = await host.Db.Tasks.SingleAsync(t => t.Title == "Fix the gate");
        Assert.Equal(WorkTaskStatus.ToDo, gate.Status);
    }

    [Fact]
    public async Task Imported_tasks_are_stamped_with_the_import_time_and_an_import_source()
    {
        // The sheet has no creation dates; a shared import stamp gives the burndown a clean day-one baseline.
        using TestHost host = new(ImportDay);
        using MemoryStream workbook = BuildWorkbook();

        await host.Importer.ImportAsync(workbook);

        List<WorkTask> tasks = await host.Db.Tasks.ToListAsync();
        Assert.All(tasks, t => Assert.Equal(TaskSource.Import, t.Source));
        Assert.All(tasks, t => Assert.Equal(new DateTime(2026, 11, 19, 7, 30, 0, DateTimeKind.Utc), t.CreatedAtUtc));
        Assert.Equal(tasks.Count, await host.Db.TaskStatusChanges.CountAsync());
    }

    [Fact]
    public async Task Running_the_import_twice_adds_nothing_even_after_a_task_was_renamed_or_finished()
    {
        // The import will be re-run by accident; it must not bring back work that was already handled.
        using TestHost host = new(ImportDay);
        using MemoryStream first = BuildWorkbook();
        await host.Importer.ImportAsync(first);
        WorkTask gate = await host.Db.Tasks.SingleAsync(t => t.Title == "Fix the gate");
        await host.Tasks.UpdateDetailsAsync(gate.Id, new TaskDetails("Fix the side gate", gate.ProjectId, TaskPriority.High, null, null));
        await host.Tasks.CompleteAsync(gate.Id);

        using MemoryStream second = BuildWorkbook();
        ImportResult result = await host.Importer.ImportAsync(second);

        Assert.Equal(0, result.ProjectsCreated);
        Assert.Equal(0, result.TasksCreated);
        Assert.Equal(3, result.TasksSkipped);
        Assert.Equal(3, await host.Db.Tasks.CountAsync());
        Assert.Equal(3, await host.Db.Projects.CountAsync());
    }

    [Fact]
    public async Task A_second_run_picks_up_rows_added_to_the_sheet_since_the_first()
    {
        using TestHost host = new(ImportDay);
        using MemoryStream first = BuildWorkbook();
        await host.Importer.ImportAsync(first);

        using MemoryStream second = BuildWorkbook(sheet =>
        {
            sheet.Cell("B12").Value = "Sharpen the knives";
            sheet.Cell("C12").Value = "TO DO";
        });
        ImportResult result = await host.Importer.ImportAsync(second);

        Assert.Equal(1, result.TasksCreated);
        WorkTask added = await host.Db.Tasks.Include(t => t.Project).SingleAsync(t => t.Title == "Sharpen the knives");
        Assert.Equal("KITCHEN", added.Project!.Name);
    }

    [Fact]
    public async Task An_unrecognised_status_is_reported_rather_than_silently_accepted()
    {
        using TestHost host = new(ImportDay);
        using MemoryStream workbook = BuildWorkbook(sheet => sheet.Cell("C6").Value = "BLOCKED");

        ImportResult result = await host.Importer.ImportAsync(workbook);

        string warning = Assert.Single(result.Warnings);
        Assert.Contains("BLOCKED", warning);
        Assert.Equal(3, result.TasksCreated);
    }

    [Fact]
    public async Task A_workbook_without_the_punchdown_sheet_fails_with_a_clear_message()
    {
        using TestHost host = new(ImportDay);
        using XLWorkbook other = new();
        other.AddWorksheet("Something Else");
        using MemoryStream stream = new();
        other.SaveAs(stream);
        stream.Position = 0;

        DomainRuleException error = await Assert.ThrowsAsync<DomainRuleException>(() => host.Importer.ImportAsync(stream));

        Assert.Contains(PunchdownImporter.SheetName, error.Message);
        Assert.Equal(0, await host.Db.Tasks.CountAsync());
    }
}
