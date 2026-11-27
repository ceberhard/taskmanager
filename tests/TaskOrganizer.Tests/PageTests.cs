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
        Assert.Contains("Time today: <span data-live-time=\"today\">1.5 h</span>", html);
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

    [Fact]
    public async Task Opening_a_proposal_shows_the_reason_for_each_task_and_changes_nothing()
    {
        // A proposal is only something to look at; the day changes when it is accepted and not before.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await AddTaskAsync(factory, "Urgent unsized thing", TaskPriority.Critical);
        await AddTaskAsync(factory, "Ordinary thing", TaskPriority.High);

        string html = WebUtility.HtmlDecode(await client.GetStringAsync("/Suggest?Proposed=true"));

        Assert.Contains("Urgent unsized thing", html);
        Assert.Contains("assumed 1 h", html);
        Assert.Contains("Critical</span>", html);
        Assert.Contains("High priority", html);
        List<WorkTask> saved = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.ToListAsync());
        Assert.All(saved, task => Assert.Null(task.PlannedFor));
        Assert.Equal(0, await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().TaskFieldChanges.CountAsync()));
    }

    [Fact]
    public async Task Accepting_a_proposal_plans_exactly_the_ticked_tasks_and_returns_to_today()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        WorkTask kept = await AddTaskAsync(factory, "Kept in the proposal");
        WorkTask removed = await AddTaskAsync(factory, "Unticked");
        WorkTask swappedIn = await AddTaskAsync(factory, "Swapped in");

        HttpResponseMessage response = await PostFormAsync(client, "/Suggest?Proposed=true", "/Suggest?handler=Commit", new[]
        {
            Field("taskIds", kept.Id.ToString()), Field("taskIds", swappedIn.Id.ToString())
        });

        string html = await response.Content.ReadAsStringAsync();
        Assert.Equal("/", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("2 tasks planned for today.", html);
        List<WorkTask> saved = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.OrderBy(t => t.Id).ToListAsync());
        Assert.Equal(factory.Today, saved.Single(t => t.Id == kept.Id).PlannedFor);
        Assert.Equal(factory.Today, saved.Single(t => t.Id == swappedIn.Id).PlannedFor);
        Assert.Null(saved.Single(t => t.Id == removed.Id).PlannedFor);
        int historyRows = await factory.WithServicesAsync(services =>
            services.GetRequiredService<AppDbContext>().TaskFieldChanges.CountAsync(c => c.Field == TaskField.PlannedFor));
        Assert.Equal(2, historyRows);
    }

    [Fact]
    public async Task Accepting_a_proposal_that_holds_a_task_closed_meanwhile_plans_nothing_and_returns_to_the_proposal()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        WorkTask open = await AddTaskAsync(factory, "Still open");
        WorkTask closed = await AddTaskAsync(factory, "Finished meanwhile");
        await factory.WithServicesAsync(async services =>
        {
            await services.GetRequiredService<TaskService>().CompleteAsync(closed.Id);
            return true;
        });

        HttpResponseMessage response = await PostFormAsync(client, "/Suggest?Proposed=true", "/Suggest?handler=Commit", new[]
        {
            Field("Hours", "6"), Field("taskIds", open.Id.ToString()), Field("taskIds", closed.Id.ToString())
        });

        string html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Nothing was planned.", html);
        Assert.Contains("Finished meanwhile", html);
        Assert.Equal("/Suggest", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("Hours=6", response.RequestMessage.RequestUri.Query);
        WorkTask stillOpen = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.SingleAsync(t => t.Id == open.Id));
        Assert.Null(stillOpen.PlannedFor);
    }

    [Fact]
    public async Task Suggestion_settings_saved_in_settings_are_used_by_the_next_proposal()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await AddTaskAsync(factory, "Unsized thing");

        HttpResponseMessage saved = await PostFormAsync(client, "/Settings", "/Settings?handler=SaveSuggestion", new[]
        {
            Field("focusSharePercent", "50"), Field("assumedUnsizedHours", "2.5"), Field("dueSoonWorkingDays", "5")
        });
        string proposal = await client.GetStringAsync("/Suggest?Proposed=true");

        Assert.Contains("Suggestion settings saved.", await saved.Content.ReadAsStringAsync());
        Assert.Contains("assumed 2.5 h", proposal);
        Assert.Contains("About 50% of the day", proposal);
    }

    [Fact]
    public async Task Hours_that_cannot_be_read_show_an_error_instead_of_a_proposal()
    {
        // Falling back to the default hours silently would give a plan for a day I did not describe.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await AddTaskAsync(factory, "Some task");

        string html = await client.GetStringAsync("/Suggest?Proposed=true&Hours=abc");

        Assert.Contains("The hours available could not be read.", html);
        Assert.DoesNotContain("id=\"proposal\"", html);
    }

    [Fact]
    public async Task Hours_outside_a_day_show_the_reason_instead_of_a_proposal()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await AddTaskAsync(factory, "Some task");

        string html = await client.GetStringAsync("/Suggest?Proposed=true&Hours=30");

        Assert.Contains("Hours available must be between 0 and 24.", html);
        Assert.DoesNotContain("id=\"proposal\"", html);
    }

    [Fact]
    public async Task Accepting_a_proposal_with_nothing_ticked_plans_nothing_and_says_so()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await AddTaskAsync(factory, "Left unticked");

        HttpResponseMessage response = await PostFormAsync(client, "/Suggest?Proposed=true", "/Suggest?handler=Commit", Array.Empty<KeyValuePair<string, string>>());

        Assert.Contains("0 tasks planned for today.", await response.Content.ReadAsStringAsync());
        WorkTask saved = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.SingleAsync());
        Assert.Null(saved.PlannedFor);
    }

    [Fact]
    public async Task A_selection_that_cannot_be_read_plans_nothing()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        WorkTask task = await AddTaskAsync(factory, "Real task");

        HttpResponseMessage response = await PostFormAsync(client, "/Suggest?Proposed=true", "/Suggest?handler=Commit", new[]
        {
            Field("taskIds", task.Id.ToString()), Field("taskIds", "not-a-number")
        });

        Assert.Contains("Nothing was planned. The selection could not be read.", await response.Content.ReadAsStringAsync());
        WorkTask saved = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.SingleAsync());
        Assert.Null(saved.PlannedFor);
    }

    [Fact]
    public async Task After_a_plan_is_accepted_today_shows_its_hours_and_the_suggest_page_starts_from_its_choices()
    {
        // The proposal said "of 6 h"; Today saying "of 9 h" a moment later would make one of them look wrong.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Project focus = await factory.WithServicesAsync(services => services.GetRequiredService<ProjectService>().AddAsync("Focus work"));
        WorkTask task = await AddTaskAsync(factory, "Unsized focus task", TaskPriority.Medium, focus.Id);

        HttpResponseMessage accepted = await PostFormAsync(client, $"/Suggest?Proposed=true&Focus={focus.Id}&Hours=6", "/Suggest?handler=Commit", new[]
        {
            Field("Focus", focus.Id.ToString()), Field("Hours", "6"), Field("taskIds", task.Id.ToString()), Field("suggestedIds", task.Id.ToString())
        });
        string today = WebUtility.HtmlDecode(await accepted.Content.ReadAsStringAsync());
        string suggestAgain = await client.GetStringAsync("/Suggest");

        Assert.Contains("Planned 1 h of 6 h available today", today);
        Assert.Contains("includes 1 h assumed for 1 unsized", today);
        Assert.Contains($"name=\"Focus\" value=\"{focus.Id}\" class=\"form-check-input\" checked=\"checked\"", suggestAgain);
        Assert.Contains("name=\"Hours\" value=\"6\"", suggestAgain);
    }

    [Fact]
    public async Task Tasks_left_out_of_a_proposal_are_offered_by_project_with_focus_projects_first()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Project other = await factory.WithServicesAsync(services => services.GetRequiredService<ProjectService>().AddAsync("Other work"));
        Project focus = await factory.WithServicesAsync(services => services.GetRequiredService<ProjectService>().AddAsync("Focus work"));
        await AddTaskAsync(factory, "Other task", TaskPriority.Medium, other.Id);
        await AddTaskAsync(factory, "Focus task", TaskPriority.Medium, focus.Id);

        string html = await client.GetStringAsync($"/Suggest?Proposed=true&Focus={focus.Id}&Hours=0");

        int focusGroup = html.IndexOf("Focus work <small", StringComparison.Ordinal);
        int otherGroup = html.IndexOf("Other work <small", StringComparison.Ordinal);
        Assert.True(focusGroup > 0 && otherGroup > focusGroup, "The focus project should be listed before the other project in step 3.");
        Assert.Contains("data-initially-open=\"true\"", html);
        Assert.Contains("data-initially-open=\"false\"", html);
    }

    [Fact]
    public async Task An_over_full_day_says_how_far_over_it_is_and_marks_what_does_not_fit()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await AddTaskAsync(factory, "First critical thing", TaskPriority.Critical);
        await AddTaskAsync(factory, "Second critical thing", TaskPriority.Critical);
        await AddTaskAsync(factory, "Ordinary thing", TaskPriority.High);

        string html = WebUtility.HtmlDecode(await client.GetStringAsync("/Suggest?Proposed=true&Hours=1"));

        Assert.Contains("Must do (2) · 2 h", html);
        Assert.Contains("Must-do work alone is 1 h over the 1 h available", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "past today's hours"));
        Assert.DoesNotContain("id=\"proposed-tasks\"", html);
    }

    [Fact]
    public async Task Deleting_without_the_confirmation_deletes_nothing()
    {
        // Delete cannot be undone, so a request that does not carry the confirmation must be refused by the server too.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        WorkTask task = await AddTaskAsync(factory, "Keep me");

        HttpResponseMessage refused = await PostFormAsync(client, "/Backlog", "/Actions?handler=Delete", new[] { Field("id", task.Id.ToString()), Field("returnUrl", "/Backlog") });
        string afterRefusal = await refused.Content.ReadAsStringAsync();
        HttpResponseMessage deleted = await PostFormAsync(client, "/Backlog", "/Actions?handler=Delete", new[]
        {
            Field("id", task.Id.ToString()), Field("confirmDelete", "true"), Field("returnUrl", "/Backlog")
        });

        Assert.Contains("Nothing was deleted.", afterRefusal);
        Assert.Contains("Keep me", afterRefusal);
        Assert.Contains("Task deleted.", await deleted.Content.ReadAsStringAsync());
        Assert.Equal(0, await CountTasksAsync(factory));
    }

    [Fact]
    public async Task The_history_page_lists_status_changes_with_reasons_and_size_changes_in_order()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        WorkTask task = await AddTaskAsync(factory, "Tracked task");
        await factory.WithServicesAsync(async services =>
        {
            // Each step happens a little later than the one before, as it would in real use.
            TaskService tasks = services.GetRequiredService<TaskService>();
            factory.Clock.Advance(TimeSpan.FromMinutes(10));
            await tasks.UpdateDetailsAsync(task.Id, new TaskDetails("Tracked task", null, TaskPriority.High, null, 2));
            factory.Clock.Advance(TimeSpan.FromMinutes(10));
            await tasks.HoldAsync(task.Id, "COMMENTS POSTED", null);
            factory.Clock.Advance(TimeSpan.FromMinutes(10));
            await tasks.HoldAsync(task.Id, "NEEDS TESTING", null);
            factory.Clock.Advance(TimeSpan.FromMinutes(10));
            await tasks.ResumeAsync(task.Id);
            return true;
        });

        string html = WebUtility.HtmlDecode(await client.GetStringAsync($"/History?id={task.Id}"));
        HttpResponseMessage missing = await client.GetAsync("/History?id=9999");

        int created = html.IndexOf("Created as To do", StringComparison.Ordinal);
        int sized = html.IndexOf("Size (hours): none → 2", StringComparison.Ordinal);
        int held = html.IndexOf("To do → On hold", StringComparison.Ordinal);
        int reasonChanged = html.IndexOf("On hold: reason changed", StringComparison.Ordinal);
        int resumed = html.IndexOf("On hold → To do", StringComparison.Ordinal);
        Assert.True(created > 0 && created < sized && sized < held && held < reasonChanged && reasonChanged < resumed, "History events are missing or out of order.");
        Assert.Contains("COMMENTS POSTED", html);
        Assert.Contains("NEEDS TESTING", html);
        Assert.Contains("Priority: Medium → High", html);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_backlog_can_be_narrowed_to_tasks_on_hold_and_shows_why_each_is_held()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await AddTaskAsync(factory, "Active thing");
        WorkTask parked = await AddTaskAsync(factory, "Parked thing");
        await factory.WithServicesAsync(async services =>
        {
            await services.GetRequiredService<TaskService>().HoldAsync(parked.Id, "AWAITING FEEDBACK", null);
            return true;
        });

        string html = await client.GetStringAsync("/Backlog?Status=OnHold");

        Assert.Contains("Parked thing", html);
        Assert.Contains("ON HOLD: AWAITING FEEDBACK", html);
        Assert.DoesNotContain("Active thing", html);
        Assert.Contains("showing 1 of 2 open", html);
    }

    [Fact]
    public async Task Hold_reasons_added_in_settings_are_offered_on_every_page_and_can_be_removed()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage added = await PostFormAsync(client, "/Settings", "/Settings?handler=AddHoldReason", new[] { Field("text", "Blocked by release freeze") });
        string backlog = await client.GetStringAsync("/Backlog");
        int id = await factory.WithServicesAsync(async services =>
            (await services.GetRequiredService<HoldReasonService>().ListAsync()).Single(r => r.Text == "Blocked by release freeze").Id);
        await PostFormAsync(client, "/Settings", "/Settings?handler=RemoveHoldReason", new[] { Field("id", id.ToString()) });
        string backlogAfterRemoval = await client.GetStringAsync("/Backlog");

        Assert.Contains("Hold reason added.", await added.Content.ReadAsStringAsync());
        Assert.Contains("<option value=\"Blocked by release freeze\">", backlog);
        Assert.Contains("<option value=\"NEEDS TESTING\">", backlog);
        Assert.DoesNotContain("Blocked by release freeze", backlogAfterRemoval);
    }

    [Theory]
    [InlineData(0, "None in progress")]
    [InlineData(1, "OK")]
    [InlineData(3, "OK")]
    [InlineData(4, "Warning")]
    [InlineData(5, "Warning")]
    [InlineData(6, "Too many")]
    [InlineData(7, "Too many")]
    [InlineData(8, "Danger")]
    [InlineData(20, "Danger")]
    public void The_in_progress_level_follows_the_agreed_bands(int count, string expectedLabel)
    {
        // 1 to 3 is fine, 4 to 5 a warning, 6 to 7 too many, 8 or more danger: the bands Chris chose.
        Assert.Equal(expectedLabel, TaskOrganizer.Web.Pages.Display.InProgressLevel(count).Label);
    }

    [Fact]
    public async Task Time_can_be_logged_and_corrected_as_pto_holiday_or_sick_and_stands_out_on_the_page()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        string today = factory.Today.ToString("yyyy-MM-dd");

        await PostFormAsync(client, "/Time", "/Actions?handler=TimeAdd", new[]
        {
            Field("date", today), Field("start", "08:00"), Field("end", "16:00"), Field("kind", "Pto"), Field("returnUrl", "/Time")
        });
        await PostFormAsync(client, "/Time", "/Actions?handler=TimeAdd", new[]
        {
            Field("date", today), Field("start", "10:00"), Field("end", "11:00"), Field("returnUrl", "/Time")
        });
        TimeEntry pto = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().TimeEntries.SingleAsync(e => e.Kind == TimeEntryKind.Pto));
        await PostFormAsync(client, "/Time", "/Actions?handler=TimeUpdate", new[]
        {
            Field("id", pto.Id.ToString()), Field("start", "08:00"), Field("end", "16:00"), Field("kind", "Sick"), Field("returnUrl", "/Time")
        });
        string html = await client.GetStringAsync("/Time");

        List<TimeEntry> saved = await factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().TimeEntries.OrderBy(e => e.StartTime).ToListAsync());
        Assert.Equal(new[] { TimeEntryKind.Sick, TimeEntryKind.Work }, saved.Select(e => e.Kind));
        Assert.Contains("time-entry kind-sick", html);
        Assert.Contains("Weekly total</th><td class=\"text-end\">9 h", html);
        Assert.Contains("Sick 8 h", html);
    }
}
