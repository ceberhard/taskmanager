using System.Net;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;
using static TaskOrganizer.Tests.AppFactory;

namespace TaskOrganizer.Tests;

public class PageTests
{
    private static Task<int> CountTasksAsync(AppFactory factory)
    {
        return factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.CountAsync());
    }

    private static Task<WorkTask> AddTaskAsync(AppFactory factory, string title, TaskPriority priority = TaskPriority.Medium, int? projectId = null)
    {
        return factory.WithServicesAsync(services =>
            services.GetRequiredService<TaskService>().AddAsync(new TaskDetails(title, projectId, priority, null, null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Quick_add_with_no_real_title_saves_nothing_and_says_so_beside_the_box(string title)
    {
        // A task with no title is noise in the backlog and cannot be acted on, so it must never be created,
        // and the rest of what was typed must survive so it does not have to be entered again.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await PostFormAsync(client, "/", "/Actions?handler=Add", new[]
        {
            Field("title", title), Field("priority", "High"), Field("estimateHours", "1.5"), Field("returnUrl", "/Backlog")
        });

        string html = await response.Content.ReadAsStringAsync();
        Assert.Equal(0, await CountTasksAsync(factory));
        Assert.Equal("/Backlog", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("id=\"quick-add-error\"", html);
        Assert.Contains("Not added: A task needs a title.", html);
        Assert.Contains("name=\"estimateHours\" value=\"1.5\"", html);
        Assert.Contains("<option value=\"High\" selected=\"selected\">", html);
    }

    [Fact]
    public async Task Quick_add_with_a_title_only_creates_the_task_under_exactly_that_title()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        await PostFormAsync(client, "/", "/Actions?handler=Add", new[] { Field("title", "  Call the vendor  "), Field("returnUrl", "/") });

        WorkTask saved = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.SingleAsync());
        Assert.Equal("Call the vendor", saved.Title);
        Assert.Equal(TaskPriority.Medium, saved.Priority);
    }

    [Fact]
    public async Task A_post_without_the_antiforgery_token_is_refused()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/Actions?handler=Add", new FormUrlEncodedContent(new[] { Field("title", "Forged") }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountTasksAsync(factory));
    }

    [Fact]
    public async Task Time_logged_from_today_is_saved_and_returns_to_today()
    {
        // The point of the Today time section is not having to leave the page to keep the log current.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        string today = factory.Today.ToString("yyyy-MM-dd");

        HttpResponseMessage response = await PostFormAsync(client, "/", "/Actions?handler=TimeAdd", new[]
        {
            Field("date", today), Field("start", "08:00"), Field("end", "09:30"), Field("returnUrl", "/")
        });

        string html = await response.Content.ReadAsStringAsync();
        Assert.Equal("/", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("Time today: 1.5 h", html);
        TimeEntry saved = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().TimeEntries.SingleAsync());
        Assert.Equal(new TimeOnly(9, 30), saved.EndTime);
    }

    [Fact]
    public async Task The_backlog_address_carries_the_filter_so_the_same_link_reopens_the_same_slice()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Project alpha = await factory.WithServicesAsync(services => services.GetRequiredService<ProjectService>().AddAsync("Alpha"));
        await AddTaskAsync(factory, "Alpha urgent thing", TaskPriority.High, alpha.Id);
        await AddTaskAsync(factory, "Alpha someday thing", TaskPriority.Low, alpha.Id);
        await AddTaskAsync(factory, "Loose urgent thing", TaskPriority.High);

        string html = await client.GetStringAsync($"/Backlog?Project={alpha.Id}&Priority=High");

        Assert.Contains("Alpha urgent thing", html);
        Assert.DoesNotContain("Alpha someday thing", html);
        Assert.DoesNotContain("Loose urgent thing", html);
        Assert.Contains("showing 1 of 3 open", html);
    }

    [Fact]
    public async Task The_backlog_page_loads_the_script_that_remembers_collapsed_projects()
    {
        // Collapse memory and mass-edit keyboard movement live in this script; a broken link would fail silently in the browser.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        string html = await client.GetStringAsync("/Backlog");
        System.Text.RegularExpressions.Match script = System.Text.RegularExpressions.Regex.Match(html, "<script src=\"([^\"]*backlog[^\"]*)\"");
        Assert.True(script.Success, "The Backlog page does not reference the backlog script.");
        HttpResponseMessage response = await client.GetAsync(WebUtility.HtmlDecode(script.Groups[1].Value));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("backlog.collapsedProjects", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Mass_edit_saves_every_changed_row_with_one_post_and_keeps_the_filter()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        WorkTask first = await AddTaskAsync(factory, "First", TaskPriority.High);
        WorkTask second = await AddTaskAsync(factory, "Second", TaskPriority.High);

        HttpResponseMessage response = await PostFormAsync(client, "/Backlog?Edit=true&Priority=High", "/Backlog?handler=SaveAll", new[]
        {
            Field("Priority", "High"),
            Field("rows[0].Id", first.Id.ToString()), Field("rows[0].Priority", "Critical"), Field("rows[0].DueDate", "2026-12-01"), Field("rows[0].EstimateHours", "2"),
            Field("rows[1].Id", second.Id.ToString()), Field("rows[1].Priority", "High"), Field("rows[1].DueDate", string.Empty), Field("rows[1].EstimateHours", "0.75")
        });

        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("2 tasks updated.", html);
        Assert.Equal("?Priority=High", response.RequestMessage!.RequestUri!.Query);
        List<WorkTask> saved = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.OrderBy(t => t.Id).ToListAsync());
        Assert.Equal(TaskPriority.Critical, saved[0].Priority);
        Assert.Equal(new DateOnly(2026, 12, 1), saved[0].DueDate);
        Assert.Equal(2, saved[0].EstimateHours);
        Assert.Equal(0.75, saved[1].EstimateHours);
    }

    [Fact]
    public async Task Mass_edit_handles_several_hundred_tasks_in_one_save()
    {
        // The framework stops reading a form after 1024 values by default, which mass edit passes at about 150 tasks.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        const int taskCount = 300;
        List<int> ids = await factory.WithServicesAsync(async services =>
        {
            TaskService tasks = services.GetRequiredService<TaskService>();
            List<int> created = new();
            for (int number = 1; number <= taskCount; number++)
            {
                created.Add((await tasks.AddAsync(new TaskDetails($"Task {number}", null, TaskPriority.Medium, null, null))).Id);
            }

            return created;
        });
        List<KeyValuePair<string, string>> fields = new();
        for (int index = 0; index < ids.Count; index++)
        {
            fields.Add(Field($"rows[{index}].Id", ids[index].ToString()));
            fields.Add(Field($"rows[{index}].Priority", "Low"));
            fields.Add(Field($"rows[{index}].OriginalPriority", "Medium"));
            fields.Add(Field($"rows[{index}].DueDate", string.Empty));
            fields.Add(Field($"rows[{index}].OriginalDueDate", string.Empty));
            fields.Add(Field($"rows[{index}].EstimateHours", "0.5"));
            fields.Add(Field($"rows[{index}].OriginalEstimateHours", string.Empty));
        }

        HttpResponseMessage response = await PostFormAsync(client, "/Backlog?Edit=true", "/Backlog?handler=SaveAll", fields);

        Assert.Contains($"{taskCount} tasks updated.", await response.Content.ReadAsStringAsync());
        int stillMedium = await factory.WithServicesAsync(services =>
            services.GetRequiredService<AppDbContext>().Tasks.CountAsync(t => t.Priority == TaskPriority.Medium));
        Assert.Equal(0, stillMedium);
    }

    [Fact]
    public async Task A_stale_mass_edit_page_does_not_revert_a_change_made_elsewhere()
    {
        // The Backlog tab can sit open in edit mode while a task is re-prioritised from Today.
        // Saving the old tab must only write the rows that were actually edited in it.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        WorkTask edited = await AddTaskAsync(factory, "Edited in the stale tab");
        WorkTask changedElsewhere = await AddTaskAsync(factory, "Changed elsewhere");
        await factory.WithServicesAsync(async services =>
        {
            await services.GetRequiredService<TaskService>().UpdateDetailsAsync(
                changedElsewhere.Id, new TaskDetails("Changed elsewhere", null, TaskPriority.Critical, null, 4));
            return true;
        });

        // The form still holds what the page showed when it loaded: both tasks Medium and unsized.
        await PostFormAsync(client, "/Backlog?Edit=true", "/Backlog?handler=SaveAll", new[]
        {
            Field("rows[0].Id", edited.Id.ToString()), Field("rows[0].Priority", "High"), Field("rows[0].OriginalPriority", "Medium"),
            Field("rows[1].Id", changedElsewhere.Id.ToString()), Field("rows[1].Priority", "Medium"), Field("rows[1].OriginalPriority", "Medium")
        });

        List<WorkTask> saved = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.OrderBy(t => t.Id).ToListAsync());
        Assert.Equal(TaskPriority.High, saved[0].Priority);
        Assert.Equal(TaskPriority.Critical, saved[1].Priority);
        Assert.Equal(4, saved[1].EstimateHours);
        List<TaskFieldChange> history = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().TaskFieldChanges.ToListAsync());
        Assert.DoesNotContain(history, c => c.WorkTaskId == changedElsewhere.Id && c.OldValue == "Critical");
    }

    [Fact]
    public async Task Mass_edit_with_one_bad_row_saves_nothing_and_shows_the_form_again_as_typed()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        WorkTask good = await AddTaskAsync(factory, "Good row");
        WorkTask bad = await AddTaskAsync(factory, "Bad row");

        HttpResponseMessage response = await PostFormAsync(client, "/Backlog?Edit=true", "/Backlog?handler=SaveAll", new[]
        {
            Field("rows[0].Id", good.Id.ToString()), Field("rows[0].Priority", "Critical"), Field("rows[0].EstimateHours", "2"),
            Field("rows[1].Id", bad.Id.ToString()), Field("rows[1].Priority", "Medium"), Field("rows[1].EstimateHours", "0.1")
        });

        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Nothing was saved.", html);
        Assert.Contains("Bad row", html);
        // Losing a column of typed values to one bad row is the tedium mass edit exists to remove.
        Assert.Contains("id=\"mass-edit\"", html);
        Assert.Contains("name=\"rows[0].EstimateHours\" value=\"2\"", html);
        Assert.Contains("name=\"rows[1].EstimateHours\" value=\"0.1\"", html);
        Assert.Contains("<option value=\"Critical\" selected=\"selected\">", html);
        List<WorkTask> saved = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.ToListAsync());
        Assert.All(saved, t => Assert.Equal(TaskPriority.Medium, t.Priority));
    }

    [Fact]
    public async Task The_time_import_offers_the_rollover_figure_and_only_applies_it_when_asked()
    {
        // The balance drives every week's "overall remaining"; it changes only on an explicit yes.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using (XLWorkbook workbook = new())
        {
            IXLWorksheet sheet = workbook.AddWorksheet(TimeLogImporter.SheetName);
            sheet.Cell("A3").Value = "Rollover";
            sheet.Cell("D3").Value = 12.5;
            sheet.Cell("A5").Value = new DateTime(2026, 11, 16);
            sheet.Cell("B5").Value = 8 / 24.0;
            sheet.Cell("C5").Value = 12 / 24.0;
            workbook.SaveAs(factory.WorkbookPath);
        }

        HttpResponseMessage imported = await PostFormAsync(client, "/Settings", "/Settings?handler=ImportTime", Array.Empty<KeyValuePair<string, string>>());
        string afterImport = await imported.Content.ReadAsStringAsync();
        double balanceBefore = await factory.WithServicesAsync(async services => (await services.GetRequiredService<SettingsService>().GetAsync()).OpeningRolloverHours);

        HttpResponseMessage applied = await PostFormWithTokenFromAsync(client, afterImport, "/Settings?handler=ApplyRollover", new[] { Field("hours", "12.5") });
        double balanceAfter = await factory.WithServicesAsync(async services => (await services.GetRequiredService<SettingsService>().GetAsync()).OpeningRolloverHours);

        Assert.Contains("Time import finished: 1 entries added", afterImport);
        // Razor encodes the plus sign, so compare against the decoded page text.
        Assert.Contains("Set opening balance to +12.5 h", WebUtility.HtmlDecode(afterImport));
        Assert.Equal(0, balanceBefore);
        Assert.True(applied.IsSuccessStatusCode);
        Assert.Equal(12.5, balanceAfter);
    }
}
