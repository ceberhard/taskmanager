using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;
using static Microsoft.Playwright.Assertions;

namespace TaskOrganizer.BrowserTests;

/// <summary>
/// End-to-end checks in a real browser, for the behaviour the other tests cannot see:
/// keyboard handling, focus, scripts and what is actually visible on the page.
/// </summary>
public class BrowserTests : IClassFixture<BrowserFixture>
{
    private readonly BrowserFixture _browser;

    public BrowserTests(BrowserFixture browser)
    {
        _browser = browser;
    }

    private static Task<WorkTask> AddTaskAsync(AppSession session, string title, TaskPriority priority = TaskPriority.Medium, int? projectId = null)
    {
        return session.App.WithServicesAsync(services =>
            services.GetRequiredService<TaskService>().AddAsync(new TaskDetails(title, projectId, priority, null, null)));
    }

    private static Task<Project> AddProjectAsync(AppSession session, string name)
    {
        return session.App.WithServicesAsync(services => services.GetRequiredService<ProjectService>().AddAsync(name));
    }

    private static Task<List<WorkTask>> SavedTasksAsync(AppSession session)
    {
        return session.App.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.OrderBy(t => t.Id).ToListAsync());
    }

    [Fact]
    public async Task A_task_can_be_added_from_any_page_with_the_keyboard_alone()
    {
        // Capturing a task must not need the mouse or a trip to another page, or it will not get captured.
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        await page.GotoAsync("/Time");

        await page.Keyboard.PressAsync("/");
        await Expect(page.Locator("#quick-title")).ToBeFocusedAsync();
        await page.Keyboard.TypeAsync("Order new cables");
        await page.Keyboard.PressAsync("Enter");

        await Expect(session.Message).ToHaveTextAsync("Added: Order new cables");
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Time$"));
        await page.GotoAsync("/Backlog");
        await Expect(session.TaskRow("Order new cables")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task An_empty_quick_add_is_stopped_by_the_browser_and_creates_nothing()
    {
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        await page.GotoAsync("/");

        await page.Locator("#quick-title").FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add", Exact = true }).ClickAsync();

        // No server message of either kind: the browser's own "required" check stopped the submit.
        await Expect(session.Message).ToHaveCountAsync(0);
        await Expect(page.Locator("#quick-add-error")).ToHaveCountAsync(0);
        await Expect(page.Locator("#quick-title")).ToHaveJSPropertyAsync("validity.valueMissing", true);
        Assert.Empty(await SavedTasksAsync(session));
    }

    [Fact]
    public async Task A_title_of_only_spaces_is_rejected_beside_the_box_with_focus_and_typed_values_kept()
    {
        // The error has to be where the eyes already are, and nothing typed may need typing again.
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        await page.GotoAsync("/Time");

        await page.Locator("#quick-title").FillAsync("   ");
        await page.GetByLabel("Rough size in hours").First.FillAsync("1.5");
        await page.Locator("#quick-title").PressAsync("Enter");

        await Expect(page.Locator("#quick-add-error")).ToContainTextAsync("A task needs a title.");
        await Expect(page.Locator("#quick-title")).ToBeFocusedAsync();
        await Expect(page.GetByLabel("Rough size in hours").First).ToHaveValueAsync("1.5");
        Assert.Empty(await SavedTasksAsync(session));
    }

    [Fact]
    public async Task One_click_on_interruption_puts_a_timestamped_task_in_today()
    {
        // An interruption is logged while it is happening; anything more than one click will be skipped.
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        await page.GotoAsync("/");

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Interruption" }).ClickAsync();

        ILocator row = session.TaskRow("Interruption at 10:00");
        await Expect(row).ToBeVisibleAsync();
        await Expect(row).ToContainTextAsync("interruption");
        await Expect(row).ToContainTextAsync("planned today");
    }

    [Fact]
    public async Task Time_can_be_started_and_stopped_from_today_and_the_total_follows_the_clock()
    {
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        ILocator timeSection = page.Locator("#today-time");
        await page.GotoAsync("/");

        await timeSection.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Start" }).ClickAsync();
        await Expect(timeSection.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Stop (running since 10:00)" })).ToBeVisibleAsync();
        session.App.Clock.Advance(TimeSpan.FromMinutes(90));
        await timeSection.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Stop" }).ClickAsync();

        await Expect(timeSection.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Time today: 1.5 h");
        await Expect(timeSection.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Start" })).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/$"));
    }

    [Fact]
    public async Task Time_can_be_logged_by_hand_on_today_without_leaving_the_page()
    {
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        ILocator timeSection = page.Locator("#today-time");
        await page.GotoAsync("/");

        await timeSection.GetByLabel("Start").Last.FillAsync("08:00");
        await timeSection.GetByLabel("End").Last.FillAsync("09:15");
        await timeSection.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Log time" }).ClickAsync();

        await Expect(timeSection.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Time today: 1.25 h");
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/$"));
    }

    [Fact]
    public async Task A_task_moves_from_the_backlog_into_today_and_then_to_done()
    {
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        await AddTaskAsync(session, "Write the report");
        await page.GotoAsync("/Backlog");

        await session.TaskRow("Write the report").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Today", Exact = true }).ClickAsync();
        await page.GotoAsync("/");
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "To do today (1)" })).ToBeVisibleAsync();
        await session.TaskRow("Write the report").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Done" }).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Done today (1)" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "To do today (0)" })).ToBeVisibleAsync();
        WorkTask saved = Assert.Single(await SavedTasksAsync(session));
        Assert.Equal(WorkTaskStatus.Completed, saved.Status);
    }

    [Fact]
    public async Task A_collapsed_project_stays_collapsed_after_the_page_is_reloaded()
    {
        // Collapsing is how the backlog is kept readable; having to redo it on every visit would make it pointless.
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        Project alpha = await AddProjectAsync(session, "Alpha");
        Project beta = await AddProjectAsync(session, "Beta");
        await AddTaskAsync(session, "Alpha task", projectId: alpha.Id);
        await AddTaskAsync(session, "Beta task", projectId: beta.Id);
        ILocator alphaGroup = page.Locator($"details.project-group[data-project='{alpha.Id}']");
        ILocator betaGroup = page.Locator($"details.project-group[data-project='{beta.Id}']");
        await page.GotoAsync("/Backlog");

        await alphaGroup.Locator("summary").First.ClickAsync();
        await Expect(session.TaskRow("Alpha task")).ToBeHiddenAsync();
        await page.ReloadAsync();

        await Expect(alphaGroup).ToHaveJSPropertyAsync("open", false);
        await Expect(session.TaskRow("Alpha task")).ToBeHiddenAsync();
        await Expect(betaGroup).ToHaveJSPropertyAsync("open", true);
        await Expect(session.TaskRow("Beta task")).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Expand all" }).ClickAsync();
        await page.ReloadAsync();
        await Expect(session.TaskRow("Alpha task")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Ticking_a_project_and_a_priority_narrows_the_backlog_and_puts_the_filter_in_the_address()
    {
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        Project alpha = await AddProjectAsync(session, "Alpha");
        Project beta = await AddProjectAsync(session, "Beta");
        await AddTaskAsync(session, "Alpha urgent thing", TaskPriority.High, alpha.Id);
        await AddTaskAsync(session, "Alpha someday thing", TaskPriority.Low, alpha.Id);
        await AddTaskAsync(session, "Beta urgent thing", TaskPriority.High, beta.Id);
        ILocator filter = page.Locator("#backlog-filter");
        await page.GotoAsync("/Backlog");

        await filter.GetByLabel("Alpha", new LocatorGetByLabelOptions { Exact = true }).CheckAsync();
        await filter.GetByLabel("High", new LocatorGetByLabelOptions { Exact = true }).CheckAsync();
        await filter.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Apply filter" }).ClickAsync();

        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($@"Project={alpha.Id}&Priority=High"));
        await Expect(session.TaskRow("Alpha urgent thing")).ToBeVisibleAsync();
        await Expect(session.TaskRow("Alpha someday thing")).ToHaveCountAsync(0);
        await Expect(session.TaskRow("Beta urgent thing")).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToContainTextAsync("showing 1 of 3 open");

        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Show everything" }).ClickAsync();
        await Expect(session.TaskRow("Beta urgent thing")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Mass_edit_changes_several_tasks_moving_down_a_column_by_keyboard_and_saves_once()
    {
        // Sizing a backlog is done a column at a time; the keyboard has to carry you down it.
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        await AddTaskAsync(session, "First task");
        await AddTaskAsync(session, "Second task");
        await AddTaskAsync(session, "Third task");
        ILocator hours = page.Locator("#mass-edit [data-col='hours']");
        await page.GotoAsync("/Backlog");

        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Edit priorities, dates and hours" }).ClickAsync();
        await hours.Nth(0).FillAsync("2");
        await hours.Nth(0).PressAsync("Control+ArrowDown");
        await Expect(hours.Nth(1)).ToBeFocusedAsync();
        await page.Keyboard.TypeAsync("0.5");
        await page.Locator("#mass-edit [data-col='priority']").Nth(0).SelectOptionAsync("Critical");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save all changes" }).First.ClickAsync();

        await Expect(session.Message).ToHaveTextAsync("2 tasks updated.");
        List<WorkTask> saved = await SavedTasksAsync(session);
        Assert.Equal((TaskPriority.Critical, 2.0), (saved[0].Priority, saved[0].EstimateHours!.Value));
        Assert.Equal((TaskPriority.Medium, 0.5), (saved[1].Priority, saved[1].EstimateHours!.Value));
        Assert.Null(saved[2].EstimateHours);
    }

    [Fact]
    public async Task A_rejected_mass_edit_keeps_what_was_typed_in_the_other_rows()
    {
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        await AddTaskAsync(session, "Good row");
        await AddTaskAsync(session, "Bad row");
        ILocator hours = page.Locator("#mass-edit [data-col='hours']");
        await page.GotoAsync("/Backlog?Edit=true");

        await hours.Nth(0).FillAsync("3");
        // The browser would stop 0.1 itself; switch that off to reach the server's own check.
        await page.Locator("#mass-edit").EvaluateAsync("form => form.noValidate = true");
        await hours.Nth(1).FillAsync("0.1");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save all changes" }).First.ClickAsync();

        await Expect(session.Message).ToContainTextAsync("Nothing was saved.");
        await Expect(session.Message).ToContainTextAsync("Bad row");
        await Expect(hours.Nth(0)).ToHaveValueAsync("3");
        Assert.All(await SavedTasksAsync(session), task => Assert.Null(task.EstimateHours));
    }

    [Fact]
    public async Task A_day_is_planned_by_choosing_focus_getting_a_proposal_adjusting_it_and_accepting()
    {
        // The three steps are the feature: say what today is about, see a plan, change it, commit it.
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        Project focus = await AddProjectAsync(session, "Focus work");
        Project other = await AddProjectAsync(session, "Other work");
        foreach (int number in Enumerable.Range(1, 5))
        {
            await AddTaskAsync(session, $"Focus task {number}", projectId: focus.Id);
            await AddTaskAsync(session, $"Other task {number}", projectId: other.Id);
        }

        ILocator total = page.Locator("#proposal-total");
        ILocator proposed = page.Locator("#proposed-tasks li.task-row");
        ILocator others = page.Locator("#other-tasks li.task-row");
        await page.GotoAsync("/");

        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Suggest tasks" }).ClickAsync();
        await page.Locator("#suggest-setup").GetByLabel("Focus work", new LocatorGetByLabelOptions { Exact = true }).CheckAsync();
        await page.GetByLabel("Hours available today").FillAsync("4");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Propose tasks" }).ClickAsync();

        // Ten unsized tasks at an assumed hour each, four hours available, 70% to the focus project.
        await Expect(proposed).ToHaveCountAsync(4);
        await Expect(proposed.Filter(new LocatorFilterOptions { HasText = "Focus work" })).ToHaveCountAsync(3);
        await Expect(proposed.First).ToContainTextAsync("assumed 1 h");
        await Expect(proposed.First).ToContainTextAsync("focus project");
        await Expect(total).ToHaveTextAsync("4 h of 4 h");

        await proposed.Filter(new LocatorFilterOptions { HasText = "Focus task 1" }).GetByRole(AriaRole.Checkbox).UncheckAsync();
        await Expect(total).ToHaveTextAsync("3 h of 4 h");

        // The other project's tasks sit in a collapsed group; the filter finds one by title and opens its group.
        ILocator filter = page.GetByLabel("Filter the tasks to swap in");
        await Expect(others.Filter(new LocatorFilterOptions { HasText = "Other task 5" })).ToBeHiddenAsync();
        await filter.FillAsync("other task 5");
        await Expect(others.Filter(new LocatorFilterOptions { Visible = true })).ToHaveCountAsync(1);
        await others.Filter(new LocatorFilterOptions { HasText = "Other task 5" }).GetByRole(AriaRole.Checkbox).CheckAsync();
        await filter.FillAsync("no such task");
        await Expect(page.Locator("#swap-no-match")).ToBeVisibleAsync();
        await filter.FillAsync("Other task 4");
        await others.Filter(new LocatorFilterOptions { HasText = "Other task 4" }).GetByRole(AriaRole.Checkbox).CheckAsync();
        await filter.FillAsync(string.Empty);
        await Expect(total).ToHaveTextAsync("5 h of 4 h (over by 1 h)");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Plan these for today" }).First.ClickAsync();

        await Expect(session.Message).ToHaveTextAsync("5 tasks planned for today.");
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "To do today (5)" })).ToBeVisibleAsync();
        await Expect(page.GetByText("Planned 5 h of 4 h available today")).ToBeVisibleAsync();
        await Expect(session.TaskRow("Other task 5")).ToBeVisibleAsync();
        await Expect(session.TaskRow("Focus task 1")).ToHaveCountAsync(0);
        List<WorkTask> saved = await SavedTasksAsync(session);
        Assert.Equal(5, saved.Count(task => task.PlannedFor is not null));
    }
}
