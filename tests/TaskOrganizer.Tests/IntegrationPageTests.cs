using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Integrations;
using TaskOrganizer.Web.Services;
using static TaskOrganizer.Tests.AppFactory;

namespace TaskOrganizer.Tests;

/// <summary>The integrations, the local endpoints and Interruption, through the real pages and HTTP.</summary>
public class IntegrationPageTests
{
    private const string SecretJiraToken = "jira-token-that-must-never-leak-123";
    private const string SecretAdoToken = "ado-token-that-must-never-leak-456";

    private static Task<List<WorkTask>> TasksAsync(AppFactory factory)
    {
        return factory.WithServicesAsync(services => services.GetRequiredService<AppDbContext>().Tasks.OrderBy(t => t.Id).ToListAsync());
    }

    private static async Task ConnectAsync(AppFactory factory, HttpClient client)
    {
        await PostFormAsync(client, "/Settings", "/Settings?handler=SaveJira", new[]
        {
            Field("jiraSiteUrl", "https://example.atlassian.net"), Field("jiraEmail", "someone@example.com"), Field("jiraApiToken", SecretJiraToken)
        });
        await PostFormAsync(client, "/Settings", "/Settings?handler=SaveAzureDevOps", new[]
        {
            Field("azureDevOpsOrganizationUrl", "https://dev.azure.com/example-org"), Field("azureDevOpsProject", string.Empty), Field("azureDevOpsToken", SecretAdoToken)
        });
    }

    private static HttpRequestMessage LocalApiRequest(string? key, object body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/external-tasks") { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add(LocalApi.KeyHeader, key);
        }

        return request;
    }

    private static object SlackItem(string ts = "1700000000.000100") => new
    {
        source = "slack",
        key = $"C0123456/{ts}",
        title = "Answer the question about the export",
        url = $"https://example.slack.com/archives/C0123456/p{ts.Replace(".", string.Empty)}"
    };

    // ---- Interruption ----

    [Fact]
    public async Task Interruption_uses_what_was_typed_in_the_quick_add_fields()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Project project = await factory.WithServicesAsync(services => services.GetRequiredService<ProjectService>().AddAsync("Support"));

        HttpResponseMessage response = await PostFormAsync(client, "/", "/Actions?handler=Interrupt", new[]
        {
            Field("title", "Export the report for Sam"), Field("projectId", project.Id.ToString()), Field("priority", "Critical"),
            Field("dueDate", "2026-11-19"), Field("estimateHours", "0.5"), Field("returnUrl", "/")
        });

        WorkTask task = Assert.Single(await TasksAsync(factory));
        Assert.Equal("Export the report for Sam", task.Title);
        Assert.Equal((project.Id, TaskPriority.Critical, 0.5), (task.ProjectId!.Value, task.Priority, task.EstimateHours!.Value));
        Assert.Equal(TaskSource.Interruption, task.Source);
        Assert.Equal(factory.Today, task.PlannedFor);
        Assert.Contains("Interruption logged and added to Today: Export the report for Sam", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Interruption_with_nothing_typed_is_still_one_click()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        await PostFormAsync(client, "/", "/Actions?handler=Interrupt", new[] { Field("title", string.Empty), Field("projectId", string.Empty), Field("returnUrl", "/") });

        Assert.Equal("Interruption at 10:00", Assert.Single(await TasksAsync(factory)).Title);
    }

    // ---- Jira / PR box ----

    [Fact]
    public async Task The_jira_and_pr_box_adds_tasks_and_shows_a_link_back_on_the_task()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await ConnectAsync(factory, client);
        factory.JiraServer.AnswerJson("/rest/api/3/issue/ROC-4698", FakeHttpHandler.JiraIssueJson("ROC-4698", "Sync scheduled offers", "2026-12-04"));
        factory.AzureDevOpsServer.AnswerJson("/example-org/_apis/git/pullrequests/26512", FakeHttpHandler.PullRequestJson(26512, "Add retry", "Sam Example"));

        HttpResponseMessage response = await PostFormAsync(client, "/Backlog", "/Actions?handler=Fetch", new[] { Field("reference", "ROC-4698 26512"), Field("returnUrl", "/Backlog") });
        string html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("Added: ROC-4698: Sync scheduled offers", html);
        Assert.Contains("Added: PR 26512 - Sam - Add retry", html);
        Assert.Contains("href=\"https://example.atlassian.net/browse/ROC-4698\"", html);
        Assert.Contains("open in Jira", html);
        Assert.Contains("open pull request", html);
        Assert.Equal(2, (await TasksAsync(factory)).Count);
    }

    [Fact]
    public async Task When_nothing_could_be_added_the_box_reports_it_as_a_problem()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await PostFormAsync(client, "/", "/Actions?handler=Fetch", new[] { Field("reference", "ROC-1"), Field("returnUrl", "/") });
        string html = await response.Content.ReadAsStringAsync();

        Assert.Contains("alert alert-danger", html);
        Assert.Contains("Jira is not set up yet", html);
        Assert.Empty(await TasksAsync(factory));
    }

    // ---- Credentials through Settings ----

    [Fact]
    public async Task Tokens_saved_in_settings_never_reach_the_database_the_page_or_the_credential_file_in_plain_text()
    {
        // The repository goes to GitHub and the database is copied for backups; neither may carry a token.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        await ConnectAsync(factory, client);
        string settingsPage = await client.GetStringAsync("/Settings");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        byte[] database = await File.ReadAllBytesAsync(factory.DatabasePath);
        string credentialFile = await File.ReadAllTextAsync(Path.Combine(factory.CredentialFolder, "credentials.json"));

        foreach (string secret in new[] { SecretJiraToken, SecretAdoToken })
        {
            Assert.DoesNotContain(secret, settingsPage);
            Assert.DoesNotContain(secret, credentialFile);
            Assert.False(ContainsBytes(database, Encoding.UTF8.GetBytes(secret)), "A token was found in the database file.");
        }

        Assert.Contains("API token (saved)", settingsPage);
        Assert.Contains("Personal access token (saved)", settingsPage);
        Assert.Contains("someone@example.com", settingsPage);
    }

    [Fact]
    public async Task The_test_connection_button_reports_who_the_token_belongs_to_or_why_it_failed()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await ConnectAsync(factory, client);
        factory.JiraServer.AnswerJson("/rest/api/3/myself", """{"displayName":"Someone Example"}""");
        factory.AzureDevOpsServer.AnswerEverything(HttpStatusCode.Unauthorized);

        string jira = await (await PostFormAsync(client, "/Settings", "/Settings?handler=TestJira", Array.Empty<KeyValuePair<string, string>>())).Content.ReadAsStringAsync();
        string ado = await (await PostFormAsync(client, "/Settings", "/Settings?handler=TestAzureDevOps", Array.Empty<KeyValuePair<string, string>>())).Content.ReadAsStringAsync();

        Assert.Contains("Jira works: signed in as Someone Example.", jira);
        Assert.Contains("Azure DevOps did not accept the personal access token", ado);
    }

    // ---- Local endpoint for the Slack skill ----

    [Fact]
    public async Task The_local_endpoint_adds_a_slack_task_once_and_returns_the_existing_one_after_that()
    {
        // The skill runs on a schedule and sees the same reacted messages every time; each must stay one task.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        string key = await factory.WithServicesAsync(services => Task.FromResult(services.GetRequiredService<CredentialStore>().GetOrCreateLocalApiKey()));

        HttpResponseMessage first = await client.SendAsync(LocalApiRequest(key, SlackItem()));
        HttpResponseMessage second = await client.SendAsync(LocalApiRequest(key, SlackItem()));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        ExternalTaskResponse firstBody = (await first.Content.ReadFromJsonAsync<ExternalTaskResponse>())!;
        ExternalTaskResponse secondBody = (await second.Content.ReadFromJsonAsync<ExternalTaskResponse>())!;
        Assert.True(firstBody.Created);
        Assert.False(secondBody.Created);
        Assert.Equal(firstBody.Id, secondBody.Id);
        WorkTask task = Assert.Single(await TasksAsync(factory));
        Assert.Equal((TaskSource.Slack, "slack:C0123456/1700000000.000100"), (task.Source, task.ImportKey!));
        Assert.Equal("https://example.slack.com/archives/C0123456/p1700000000000100", task.ExternalUrl);
    }

    [Fact]
    public async Task The_local_endpoint_refuses_requests_without_the_right_key()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage missing = await client.SendAsync(LocalApiRequest(null, SlackItem()));
        HttpResponseMessage wrong = await client.SendAsync(LocalApiRequest("not-the-key", SlackItem()));

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Empty(await TasksAsync(factory));
    }

    [Fact]
    public async Task A_web_page_cannot_add_tasks_through_the_local_endpoint()
    {
        // A page in the browser can post an ordinary form to localhost without any permission; it cannot set a custom header or send JSON cross-site.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await factory.WithServicesAsync(services => Task.FromResult(services.GetRequiredService<CredentialStore>().GetOrCreateLocalApiKey()));

        HttpResponseMessage formPost = await client.PostAsync("/api/external-tasks", new FormUrlEncodedContent(new[]
        {
            Field("source", "slack"), Field("key", "C1/1"), Field("title", "Injected"), Field("url", "https://example.com")
        }));
        HttpResponseMessage preflight = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/external-tasks")
        {
            Headers = { { "Origin", "https://evil.example" }, { "Access-Control-Request-Method", "POST" }, { "Access-Control-Request-Headers", LocalApi.KeyHeader } }
        });

        Assert.False(formPost.IsSuccessStatusCode);
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Empty(await TasksAsync(factory));
    }

    [Fact]
    public async Task The_local_endpoint_only_accepts_slack_items_with_a_title_and_a_web_link()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        string key = await factory.WithServicesAsync(services => Task.FromResult(services.GetRequiredService<CredentialStore>().GetOrCreateLocalApiKey()));

        HttpResponseMessage jira = await client.SendAsync(LocalApiRequest(key, new { source = "jira", key = "ROC-1", title = "x", url = "https://example.com" }));
        HttpResponseMessage noTitle = await client.SendAsync(LocalApiRequest(key, new { source = "slack", key = "C1/1", title = " ", url = "https://example.com" }));
        HttpResponseMessage badLink = await client.SendAsync(LocalApiRequest(key, new { source = "slack", key = "C1/1", title = "x", url = "file:///c:/secret.txt" }));

        Assert.Equal(HttpStatusCode.BadRequest, jira.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noTitle.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badLink.StatusCode);
        Assert.Empty(await TasksAsync(factory));
    }

    [Fact]
    public async Task The_local_endpoint_refuses_an_item_without_a_key_instead_of_merging_it_with_others()
    {
        // Without a key every such request would map to the same task, and each later message would be lost as "already a task".
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        string key = await factory.WithServicesAsync(services => Task.FromResult(services.GetRequiredService<CredentialStore>().GetOrCreateLocalApiKey()));

        HttpResponseMessage missing = await client.SendAsync(LocalApiRequest(key, new { source = "slack", title = "First message", url = "https://example.slack.com/archives/C1/p1" }));
        HttpResponseMessage blank = await client.SendAsync(LocalApiRequest(key, new { source = "slack", key = "   ", title = "Second message", url = "https://example.slack.com/archives/C1/p2" }));

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("key", await missing.Content.ReadAsStringAsync());
        Assert.Empty(await TasksAsync(factory));
    }

    [Fact]
    public async Task Integration_failures_and_refused_local_calls_are_logged_without_any_secret()
    {
        // When a scheduled run starts failing overnight the log is the only trace, and a log must never carry a token or the key.
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await ConnectAsync(factory, client);
        string localKey = await factory.WithServicesAsync(services => Task.FromResult(services.GetRequiredService<CredentialStore>().GetOrCreateLocalApiKey()));
        factory.JiraServer.AnswerEverything(HttpStatusCode.Unauthorized);
        factory.AzureDevOpsServer.AnswerJson("/example-org/_apis/git/pullrequests/26512", FakeHttpHandler.PullRequestJson(26512, "Add retry", "Sam Example"));

        await PostFormAsync(client, "/", "/Actions?handler=Fetch", new[] { Field("reference", "ROC-1 26512"), Field("returnUrl", "/") });
        await client.SendAsync(LocalApiRequest("wrong-key", SlackItem()));
        await client.SendAsync(LocalApiRequest(localKey, new { source = "slack", title = "No key", url = "https://example.slack.com/archives/C1/p1" }));

        List<(LogLevel Level, string Category, string Message)> entries = factory.Logs.Entries.ToList();
        Assert.Contains(entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Jira") && e.Message.Contains("ROC-1") && e.Message.Contains("did not accept"));
        Assert.Contains(entries, e => e.Level == LogLevel.Information && e.Message.Contains("1 added") && e.Message.Contains("1 failed"));
        Assert.Contains(entries, e => e.Level == LogLevel.Warning && e.Message.Contains("local key"));
        Assert.Contains(entries, e => e.Level == LogLevel.Warning && e.Message.Contains("key identifying the message"));
        foreach (string secret in new[] { SecretJiraToken, SecretAdoToken, localKey, "No key" })
        {
            Assert.DoesNotContain(entries, e => e.Message.Contains(secret));
        }
    }

    // ---- Live time ----

    [Fact]
    public async Task The_time_endpoint_gives_the_figures_a_page_shows_while_the_clock_runs()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await PostFormAsync(client, "/", "/Actions?handler=ClockIn", new[] { Field("returnUrl", "/") });
        factory.Clock.Advance(TimeSpan.FromMinutes(45));

        TimeTodayResponse time = (await client.GetFromJsonAsync<TimeTodayResponse>("/api/time/today"))!;
        string page = await client.GetStringAsync("/");

        Assert.True(time.Running);
        Assert.Equal("0.75 h", time.TodayHours);
        Assert.Equal("0.75 h", time.WeekHours);
        Assert.Contains("data-time-refresh-seconds=\"1\"", page);
    }

    [Fact]
    public async Task Pages_do_not_poll_for_time_when_the_clock_is_not_running()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        string page = await client.GetStringAsync("/");

        Assert.Contains("data-time-refresh-seconds=\"0\"", page);
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }
}
