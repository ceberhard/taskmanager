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
        await session.TaskRow("Write the report").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Done", Exact = true }).ClickAsync();

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

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 }).GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Suggest tasks" }).ClickAsync();
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

    [Fact]
    public async Task A_long_link_in_a_title_wraps_inside_the_title_and_leaves_the_buttons_on_one_line()
    {
        // Rows of uneven height with buttons in different places made the list hard to scan and easy to mis-click.
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        await page.SetViewportSizeAsync(1280, 900);
        string longLink = "https://example.com/" + string.Join("/", Enumerable.Repeat("a-very-long-path-segment", 12));
        await AddTaskAsync(session, "Short title");
        await AddTaskAsync(session, $"Review the pull request at {longLink} and reply to every comment on it");

        foreach (string url in new[] { "/Backlog", "/" })
        {
            await session.App.WithServicesAsync(async services =>
            {
                TaskService tasks = services.GetRequiredService<TaskService>();
                foreach (WorkTask task in await services.GetRequiredService<AppDbContext>().Tasks.ToListAsync())
                {
                    await tasks.PlanForTodayAsync(task.Id);
                }

                return true;
            });
            await page.GotoAsync(url);

            foreach (string title in new[] { "Short title", "Review the pull request" })
            {
                ILocator row = session.TaskRow(title);
                ILocator buttons = row.Locator(".task-actions button");
                LocatorBoundingBoxResult rowBox = (await row.BoundingBoxAsync())!;
                LocatorBoundingBoxResult first = (await buttons.First.BoundingBoxAsync())!;
                LocatorBoundingBoxResult last = (await buttons.Last.BoundingBoxAsync())!;

                Assert.True(Math.Abs(first.Y - last.Y) < 2, $"Buttons wrapped onto more than one line for '{title}' on {url}.");
                Assert.True(first.Y - rowBox.Y < 12, $"Buttons were pushed below the title for '{title}' on {url}.");
                Assert.True(last.X + last.Width <= rowBox.X + rowBox.Width + 1, $"Buttons overflow the row for '{title}' on {url}.");
            }
        }
    }

    [Fact]
    public async Task A_task_is_put_on_hold_with_a_typed_reason_shows_it_and_is_resumed()
    {
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        WorkTask task = await AddTaskAsync(session, "Review the pull request");
        ILocator row = session.TaskRow("Review the pull request");
        await page.GotoAsync("/Backlog");

        await row.GetByText("Edit, hold, more").ClickAsync();
        await Expect(row.GetByLabel("Hold reason")).ToHaveAttributeAsync("list", "hold-reasons");
        await row.GetByLabel("Hold reason").FillAsync("AWAITING FEEDBACK FROM SAM");
        await row.GetByLabel("Check back on").FillAsync("2026-11-20");
        await row.GetByLabel("remember this reason").CheckAsync();
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Put on hold" }).ClickAsync();

        await Expect(row).ToContainTextAsync("ON HOLD: AWAITING FEEDBACK FROM SAM");
        await Expect(row).ToContainTextAsync("check back Fri 20 Nov");
        await Expect(row).ToContainClassAsync("on-hold");
        await Expect(row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Start" })).ToHaveCountAsync(0);
        await Expect(page.Locator("#hold-reasons option[value='AWAITING FEEDBACK FROM SAM']")).ToHaveCountAsync(1);

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Resume" }).ClickAsync();
        await Expect(row).Not.ToContainTextAsync("ON HOLD");
        await Expect(row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Start" })).ToBeVisibleAsync();

        await row.GetByText("Edit, hold, more").ClickAsync();
        await row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "History" }).ClickAsync();
        await Expect(page.Locator("#task-history")).ToContainTextAsync("To do → On hold");
        await Expect(page.Locator("#task-history")).ToContainTextAsync("AWAITING FEEDBACK FROM SAM");
        await Expect(page.Locator("#task-history")).ToContainTextAsync("On hold → To do");
        Assert.Equal(WorkTaskStatus.ToDo, (await SavedTasksAsync(session)).Single(t => t.Id == task.Id).Status);
    }

    [Fact]
    public async Task Work_marked_done_for_today_stays_on_show_and_is_reviewed_the_next_day()
    {
        // The whole workflow from the spreadsheet: green today, then tomorrow I decide what it really was.
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        foreach (string title in new[] { "Really finished", "Not finished after all", "Pick up later" })
        {
            WorkTask task = await AddTaskAsync(session, title);
            await session.App.WithServicesAsync(async services =>
            {
                await services.GetRequiredService<TaskService>().PlanForTodayAsync(task.Id);
                return true;
            });
        }

        await page.GotoAsync("/");
        foreach (string title in new[] { "Really finished", "Not finished after all", "Pick up later" })
        {
            await session.TaskRow(title).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Done for today" }).ClickAsync();
        }

        ILocator doneForToday = page.Locator("#done-for-today li.task-row");
        await Expect(doneForToday).ToHaveCountAsync(3);
        await Expect(doneForToday.First).ToContainClassAsync("done-for-today");
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "To do today (0)" })).ToBeVisibleAsync();
        await Expect(page.Locator("#awaiting-review")).ToHaveCountAsync(0);

        session.App.Clock.Advance(TimeSpan.FromDays(1));
        await page.GotoAsync("/Suggest");
        await Expect(page.Locator("#before-planning #awaiting-review li.task-row")).ToHaveCountAsync(3);
        await page.GotoAsync("/");
        ILocator review = page.Locator("#awaiting-review li.task-row");
        await Expect(review).ToHaveCountAsync(3);

        await review.Filter(new LocatorFilterOptions { HasText = "Really finished" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Done", Exact = true }).ClickAsync();
        await review.Filter(new LocatorFilterOptions { HasText = "Not finished after all" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Re-open for today" }).ClickAsync();
        await review.Filter(new LocatorFilterOptions { HasText = "Pick up later" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Backlog" }).ClickAsync();

        await Expect(page.Locator("#awaiting-review")).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "To do today (1)" })).ToBeVisibleAsync();
        await Expect(session.TaskRow("Not finished after all")).ToBeVisibleAsync();
        await Expect(session.TaskRow("Really finished")).ToHaveCountAsync(0);
        List<WorkTask> saved = await SavedTasksAsync(session);
        WorkTask finished = saved.Single(t => t.Title == "Really finished");
        Assert.Equal(WorkTaskStatus.Completed, finished.Status);
        Assert.Equal(new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc), finished.CompletedAtUtc);
        Assert.Equal(new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc), finished.ConfirmedAtUtc);
        Assert.Null(saved.Single(t => t.Title == "Pick up later").PlannedFor);
    }

    [Fact]
    public async Task Starting_tasks_gives_them_a_clear_in_progress_look_and_a_count_with_a_level()
    {
        // The count is the guard against quietly having too many things open at once.
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        foreach (int number in Enumerable.Range(1, 4))
        {
            WorkTask task = await AddTaskAsync(session, $"Task {number}");
            await session.App.WithServicesAsync(async services =>
            {
                await services.GetRequiredService<TaskService>().PlanForTodayAsync(task.Id);
                return true;
            });
        }

        ILocator indicator = page.Locator("#in-progress-indicator");
        await page.GotoAsync("/");
        await Expect(indicator).ToHaveTextAsync("None in progress");

        await session.TaskRow("Task 1").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Start" }).ClickAsync();
        await Expect(indicator).ToHaveTextAsync("1 in progress · OK");
        await Expect(session.TaskRow("Task 1")).ToContainClassAsync("in-progress");
        await Expect(session.TaskRow("Task 1")).ToContainTextAsync("IN PROGRESS");
        await Expect(session.TaskRow("Task 2")).Not.ToContainClassAsync("in-progress");

        foreach (int number in Enumerable.Range(2, 3))
        {
            await session.TaskRow($"Task {number}").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Start" }).ClickAsync();
        }

        await Expect(indicator).ToHaveTextAsync("4 in progress · Warning");
    }

    [Fact]
    public async Task A_projects_tasks_get_one_priority_and_date_in_a_single_step_and_can_still_be_adjusted_before_saving()
    {
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        Project alpha = await AddProjectAsync(session, "Alpha");
        Project beta = await AddProjectAsync(session, "Beta");
        foreach (int number in Enumerable.Range(1, 3))
        {
            await AddTaskAsync(session, $"Alpha task {number}", projectId: alpha.Id);
        }

        await AddTaskAsync(session, "Beta task 1", projectId: beta.Id);
        await AddTaskAsync(session, "Beta task 2", projectId: beta.Id);
        ILocator alphaGroup = page.Locator($"details.project-group[data-project='{alpha.Id}']");
        await page.GotoAsync("/Backlog?Edit=true");

        await alphaGroup.GetByLabel("Priority for every task in Alpha").SelectOptionAsync("High");
        await alphaGroup.GetByLabel("Expected delivery date for every task in Alpha").FillAsync("2026-12-04");
        await alphaGroup.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Apply to these rows" }).ClickAsync();
        await Expect(alphaGroup.Locator("[data-apply-result]")).ToContainTextAsync("Filled 3 rows");
        await alphaGroup.Locator("[data-col='priority']").Nth(1).SelectOptionAsync("Critical");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save all changes" }).First.ClickAsync();

        await Expect(session.Message).ToHaveTextAsync("3 tasks updated.");
        List<WorkTask> saved = await SavedTasksAsync(session);
        List<WorkTask> alphaTasks = saved.Where(t => t.ProjectId == alpha.Id).ToList();
        Assert.Equal(new[] { TaskPriority.High, TaskPriority.Critical, TaskPriority.High }, alphaTasks.Select(t => t.Priority));
        Assert.All(alphaTasks, t => Assert.Equal(new DateOnly(2026, 12, 4), t.DueDate));
        Assert.All(saved.Where(t => t.ProjectId == beta.Id), t => Assert.Equal((TaskPriority.Medium, (DateOnly?)null), (t.Priority, t.DueDate)));
    }

    [Fact]
    public async Task A_task_is_deleted_only_after_the_confirmation_is_ticked()
    {
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;

        // The title must not echo the words of the confirmation label, which every row contains.
        await AddTaskAsync(session, "Duplicate entry");
        await AddTaskAsync(session, "Real task");
        ILocator row = session.TaskRow("Duplicate entry");
        await page.GotoAsync("/Backlog");

        await row.GetByText("Edit, hold, more").ClickAsync();
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete" }).ClickAsync();
        Assert.Equal(2, (await SavedTasksAsync(session)).Count);

        await row.GetByLabel("delete for good").CheckAsync();
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete" }).ClickAsync();

        await Expect(session.Message).ToHaveTextAsync("Task deleted.");
        await Expect(session.TaskRow("Duplicate entry")).ToHaveCountAsync(0);
        Assert.Equal("Real task", Assert.Single(await SavedTasksAsync(session)).Title);
    }

    [Fact]
    public async Task Interruption_takes_what_is_typed_in_quick_add_and_works_with_nothing_typed()
    {
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        await AddProjectAsync(session, "Support");
        await page.GotoAsync("/");

        await page.Locator("#quick-title").FillAsync("Export the report for Sam");
        await page.GetByLabel("Project", new PageGetByLabelOptions { Exact = true }).First.SelectOptionAsync(new SelectOptionValue { Label = "Support" });
        await page.GetByLabel("Priority", new PageGetByLabelOptions { Exact = true }).First.SelectOptionAsync("Critical");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Interruption" }).ClickAsync();

        ILocator typed = session.TaskRow("Export the report for Sam");
        await Expect(typed).ToContainTextAsync("Support");
        await Expect(typed).ToContainTextAsync("Critical");
        await Expect(typed).ToContainTextAsync("interruption");
        await Expect(page.Locator("#quick-title")).ToHaveValueAsync(string.Empty);

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Interruption" }).ClickAsync();
        await Expect(session.TaskRow("Interruption at 10:00")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task While_the_clock_runs_the_time_figures_move_on_without_reloading_the_page()
    {
        // A reload would lose anything half-typed, so only the figures are refreshed.
        await using AppSession session = await AppSession.StartAsync(_browser.Browser);
        IPage page = session.Page;
        ILocator timeHeading = page.Locator("#today-time").GetByRole(AriaRole.Heading);
        await page.GotoAsync("/");
        await page.Locator("#today-time").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Start" }).ClickAsync();
        await Expect(timeHeading).ToHaveTextAsync("Time today: 0 h");
        await page.Locator("#quick-title").FillAsync("Half-typed task");

        session.App.Clock.Advance(TimeSpan.FromMinutes(90));

        await Expect(timeHeading).ToHaveTextAsync("Time today: 1.5 h", new LocatorAssertionsToHaveTextOptions { Timeout = 10000 });
        await Expect(page.Locator("#capture-bar [data-live-time='today']")).ToHaveTextAsync("1.5 h");
        await Expect(page.Locator("#quick-title")).ToHaveValueAsync("Half-typed task");
    }
}
