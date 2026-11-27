using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Integrations;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Tests;

/// <summary>Jira, pull requests, the shared outside-item rule, credentials and Interruption with typed fields.</summary>
public class IntegrationTests
{
    private static readonly DateTime Monday9am = new(2026, 11, 16, 9, 0, 0);
    private const string JiraIssuePath = "/rest/api/3/issue/";
    private const string PullRequestPath = "/example-org/_apis/git/pullrequests/";

    // ---- Reading what was typed ----

    [Theory]
    [InlineData("ROC-4698", new[] { "ROC-4698" }, new int[0])]
    [InlineData("roc-4698, changereq-8311", new[] { "ROC-4698", "CHANGEREQ-8311" }, new int[0])]
    [InlineData("https://example.atlassian.net/browse/SYSCHG-1144", new[] { "SYSCHG-1144" }, new int[0])]
    [InlineData("26512", new string[0], new[] { 26512 })]
    [InlineData("#26512 PR 26513 PR-26514", new string[0], new[] { 26512, 26513, 26514 })]
    [InlineData("https://dev.azure.com/org/Proj/_git/repo/pullrequest/27622", new string[0], new[] { 27622 })]
    [InlineData("ROC-1 26512 ROC-1 26512", new[] { "ROC-1" }, new[] { 26512 })]
    [InlineData("https://example.com/docs/v2/page 26512", new string[0], new[] { 26512 })]
    public void Jira_keys_and_pull_request_numbers_are_read_from_whatever_was_typed_or_pasted(string input, string[] expectedKeys, int[] expectedPullRequests)
    {
        // Pasting a link or a short list is how references arrive from Slack and email; making me retype them defeats the point.
        (List<string> keys, List<int> pullRequests) = ReferenceImportService.Parse(input);

        Assert.Equal(expectedKeys, keys);
        Assert.Equal(expectedPullRequests, pullRequests);
    }

    [Theory]
    [InlineData("Rakesh Kumar", "Rakesh")]
    [InlineData("Kumar, Rakesh", "Rakesh")]
    [InlineData("Maria", "Maria")]
    [InlineData("  ", "Unknown")]
    public void A_pull_request_author_is_named_by_first_name_whichever_way_the_name_is_written(string displayName, string expected)
    {
        Assert.Equal(expected, ReferenceImportService.FirstName(displayName));
    }

    [Fact]
    public async Task Typing_nothing_recognisable_says_what_is_expected()
    {
        using TestHost host = new(Monday9am);

        DomainRuleException error = await Assert.ThrowsAsync<DomainRuleException>(() => host.References.ImportAsync("hello there"));

        Assert.Contains("ROC-4698", error.Message);
    }

    // ---- Jira ----

    [Fact]
    public async Task A_jira_key_becomes_a_task_titled_with_the_key_and_summary_with_its_due_date_project_and_link()
    {
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        Project roc = await host.Projects.AddAsync("Roadmap work");
        await host.Integrations.AddMappingAsync("roc", roc.Id);
        host.JiraServer.AnswerJson(JiraIssuePath + "ROC-4698", FakeHttpHandler.JiraIssueJson("ROC-4698", "Sync scheduled offers", "2026-12-04"));

        List<ReferenceOutcome> outcomes = await host.References.ImportAsync("roc-4698");

        ReferenceOutcome outcome = Assert.Single(outcomes);
        Assert.True(outcome.Created);
        WorkTask task = await host.Db.Tasks.SingleAsync();
        Assert.Equal("ROC-4698: Sync scheduled offers", task.Title);
        Assert.Equal(new DateOnly(2026, 12, 4), task.DueDate);
        Assert.Equal(roc.Id, task.ProjectId);
        Assert.Equal(TaskSource.Jira, task.Source);
        Assert.Equal(TaskPriority.Medium, task.Priority);
        Assert.Equal("https://example.atlassian.net/browse/ROC-4698", task.ExternalUrl);
        Assert.Equal("jira:ROC-4698", task.ImportKey);
        string expectedAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("someone@example.com:fake-jira-token"));
        Assert.Equal("Basic " + expectedAuth, host.JiraServer.Requests.Single().Headers.Authorization!.ToString());
        Assert.Equal(HttpMethod.Get, host.JiraServer.Requests.Single().Method);
    }

    [Fact]
    public async Task A_ticket_with_an_unmapped_prefix_and_no_due_date_goes_to_unfiled_with_no_date()
    {
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        host.JiraServer.AnswerJson(JiraIssuePath + "SYSCHG-1144", FakeHttpHandler.JiraIssueJson("SYSCHG-1144", "Scheduled task"));

        await host.References.ImportAsync("SYSCHG-1144");

        WorkTask task = await host.Db.Tasks.SingleAsync();
        Assert.Null(task.ProjectId);
        Assert.Null(task.DueDate);
    }

    [Fact]
    public async Task Adding_the_same_ticket_again_creates_nothing_and_says_it_is_already_a_task_even_if_it_was_finished()
    {
        // A ticket mentioned twice must not become two pieces of work.
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        host.JiraServer.AnswerJson(JiraIssuePath + "ROC-1", FakeHttpHandler.JiraIssueJson("ROC-1", "First"));
        await host.References.ImportAsync("ROC-1");
        WorkTask task = await host.Db.Tasks.SingleAsync();
        await host.Tasks.CompleteAsync(task.Id);

        ReferenceOutcome again = Assert.Single(await host.References.ImportAsync("ROC-1"));

        Assert.False(again.Created);
        Assert.Contains("already a task (Completed)", again.Message);
        Assert.Equal(1, await host.Db.Tasks.CountAsync());
    }

    [Fact]
    public async Task One_unknown_ticket_is_reported_and_the_others_are_still_added()
    {
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        host.JiraServer.AnswerJson(JiraIssuePath + "ROC-1", FakeHttpHandler.JiraIssueJson("ROC-1", "First"));
        host.JiraServer.AnswerJson(JiraIssuePath + "ROC-3", FakeHttpHandler.JiraIssueJson("ROC-3", "Third"));

        List<ReferenceOutcome> outcomes = await host.References.ImportAsync("ROC-1 ROC-2 ROC-3");

        Assert.Equal(new[] { true, false, true }, outcomes.Select(o => o.Created));
        Assert.Contains("ROC-2 was not found", outcomes[1].Message);
        Assert.Equal(2, await host.Db.Tasks.CountAsync());
    }

    [Fact]
    public async Task A_refused_token_stops_the_jira_keys_with_the_reason_and_changes_nothing()
    {
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        host.JiraServer.AnswerEverything(HttpStatusCode.Unauthorized);

        List<ReferenceOutcome> outcomes = await host.References.ImportAsync("ROC-1 ROC-2");

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Contains("did not accept the email and API token", o.Message));
        Assert.Single(host.JiraServer.Requests);
        Assert.Equal(0, await host.Db.Tasks.CountAsync());
    }

    [Fact]
    public async Task An_unreachable_or_slow_jira_is_reported_and_changes_nothing()
    {
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();

        host.JiraServer.FailWith = new HttpRequestException("No such host is known.");
        ReferenceOutcome unreachable = Assert.Single(await host.References.ImportAsync("ROC-1"));
        host.JiraServer.FailWith = new TaskCanceledException("timed out");
        ReferenceOutcome slow = Assert.Single(await host.References.ImportAsync("ROC-1"));

        Assert.Contains("could not be reached", unreachable.Message);
        Assert.Contains("did not answer in time", slow.Message);
        Assert.Equal(0, await host.Db.Tasks.CountAsync());
    }

    [Fact]
    public async Task Without_jira_set_up_the_message_says_where_to_set_it_up_and_nothing_is_called()
    {
        using TestHost host = new(Monday9am);

        ReferenceOutcome outcome = Assert.Single(await host.References.ImportAsync("ROC-1"));

        Assert.Contains("Jira is not set up yet", outcome.Message);
        Assert.Empty(host.JiraServer.Requests);
    }

    // ---- Pull requests ----

    [Fact]
    public async Task A_pull_request_number_becomes_a_review_task_named_like_review_sessions_with_the_agreed_defaults()
    {
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        Project reviews = await host.Projects.AddAsync("Reviews");
        await host.Integrations.SavePullRequestDefaultsAsync(reviews.Id, 0.5, TaskPriority.High);
        host.AzureDevOpsServer.AnswerJson(PullRequestPath + "26512", FakeHttpHandler.PullRequestJson(26512, "Add retry to the sync", "Sam Example"));

        ReferenceOutcome outcome = Assert.Single(await host.References.ImportAsync("26512"));

        Assert.True(outcome.Created);
        WorkTask task = await host.Db.Tasks.SingleAsync();
        Assert.Equal("PR 26512 - Sam - Add retry to the sync", task.Title);
        Assert.Equal(0.5, task.EstimateHours);
        Assert.Equal(TaskPriority.High, task.Priority);
        Assert.Equal(reviews.Id, task.ProjectId);
        Assert.Equal(TaskSource.PullRequest, task.Source);
        Assert.Equal("https://dev.azure.com/example-org/Example/_git/example-repo/pullrequest/26512", task.ExternalUrl);
        HttpRequestMessage request = host.AzureDevOpsServer.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("api-version=7.1", request.RequestUri!.Query.TrimStart('?'));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":fake-ado-token")), request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Pull_request_defaults_are_half_an_hour_at_high_priority_until_changed()
    {
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        host.AzureDevOpsServer.AnswerJson(PullRequestPath + "7", FakeHttpHandler.PullRequestJson(7, "Small fix", "Sam Example"));

        await host.References.ImportAsync("PR 7");

        WorkTask task = await host.Db.Tasks.SingleAsync();
        Assert.Equal((0.5, TaskPriority.High, (int?)null), (task.EstimateHours!.Value, task.Priority, task.ProjectId));
    }

    [Fact]
    public async Task The_same_pull_request_twice_is_one_task_and_an_unknown_number_creates_nothing()
    {
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        host.AzureDevOpsServer.AnswerJson(PullRequestPath + "26512", FakeHttpHandler.PullRequestJson(26512, "Add retry", "Sam Example"));
        await host.References.ImportAsync("26512");

        List<ReferenceOutcome> outcomes = await host.References.ImportAsync("26512 99999");

        Assert.Contains("PR 26512 is already a task", outcomes[0].Message);
        Assert.Contains("PR 99999 was not found", outcomes[1].Message);
        Assert.Equal(1, await host.Db.Tasks.CountAsync());
    }

    [Fact]
    public async Task A_token_azure_devops_does_not_accept_is_reported_even_when_it_answers_with_a_sign_in_page()
    {
        // Azure DevOps often answers a bad token with "203" and a web page rather than a clean refusal.
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        host.AzureDevOpsServer.AnswerEverything(HttpStatusCode.NonAuthoritativeInformation, "<html>Sign in</html>", "text/html");

        ReferenceOutcome outcome = Assert.Single(await host.References.ImportAsync("26512"));

        Assert.Contains("did not accept the personal access token", outcome.Message);
        Assert.Equal(0, await host.Db.Tasks.CountAsync());
    }

    [Fact]
    public async Task A_project_set_in_settings_is_used_in_the_pull_request_address()
    {
        using TestHost host = new(Monday9am);
        await host.Integrations.SaveAzureDevOpsAsync("https://dev.azure.com/example-org/", "My Project", "fake-ado-token");
        host.AzureDevOpsServer.AnswerJson("/example-org/My%20Project/_apis/git/pullrequests/5", FakeHttpHandler.PullRequestJson(5, "Fix", "Sam Example"));

        ReferenceOutcome outcome = Assert.Single(await host.References.ImportAsync("5"));

        Assert.True(outcome.Created, outcome.Message);
    }

    [Fact]
    public async Task Jira_keys_and_pull_requests_can_be_added_together()
    {
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        host.JiraServer.AnswerJson(JiraIssuePath + "ROC-1", FakeHttpHandler.JiraIssueJson("ROC-1", "First"));
        host.AzureDevOpsServer.AnswerJson(PullRequestPath + "26512", FakeHttpHandler.PullRequestJson(26512, "Add retry", "Sam Example"));

        List<ReferenceOutcome> outcomes = await host.References.ImportAsync("ROC-1, 26512");

        Assert.All(outcomes, o => Assert.True(o.Created, o.Message));
        Assert.Equal(new[] { TaskSource.Jira, TaskSource.PullRequest }, (await host.Db.Tasks.OrderBy(t => t.Id).ToListAsync()).Select(t => t.Source));
    }

    [Theory]
    [InlineData("""{"key":"ROC-1"}""")]
    [InlineData("""{"fields":{"summary":"No key"}}""")]
    [InlineData("""{"key":42,"fields":{"summary":"Key is a number"}}""")]
    [InlineData("""{"key":"ROC-1","fields":{"summary":["not","text"]}}""")]
    [InlineData("""[]""")]
    public async Task A_jira_answer_of_the_wrong_shape_is_reported_and_creates_nothing(string json)
    {
        // A wrong site address can answer with some other JSON; that must read as a set-up problem, not crash the page.
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        host.JiraServer.AnswerJson(JiraIssuePath + "ROC-1", json);

        ReferenceOutcome outcome = Assert.Single(await host.References.ImportAsync("ROC-1"));

        Assert.False(outcome.Created);
        Assert.Contains("could not be read", outcome.Message);
        Assert.Equal(0, await host.Db.Tasks.CountAsync());
    }

    [Theory]
    [InlineData("""{"pullRequestId":5,"title":"No repository","createdBy":{"displayName":"Sam Example"}}""")]
    [InlineData("""{"pullRequestId":5,"title":7,"createdBy":{"displayName":"Sam Example"},"repository":{"name":"r","webUrl":"https://dev.azure.com/o/p/_git/r"}}""")]
    [InlineData("""{"pullRequestId":5,"title":"Fix","createdBy":"Sam","repository":{"name":"r","webUrl":"https://dev.azure.com/o/p/_git/r"}}""")]
    [InlineData("""{"pullRequestId":5,"title":"Fix","createdBy":{"displayName":"Sam"},"repository":"r"}""")]
    [InlineData("""[]""")]
    public async Task An_azure_devops_answer_of_the_wrong_shape_is_reported_and_creates_nothing(string json)
    {
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        host.AzureDevOpsServer.AnswerJson(PullRequestPath + "5", json);

        ReferenceOutcome outcome = Assert.Single(await host.References.ImportAsync("5"));

        Assert.False(outcome.Created);
        Assert.Contains("could not be read", outcome.Message);
        Assert.Equal(0, await host.Db.Tasks.CountAsync());
    }

    [Fact]
    public async Task A_jira_ticket_with_no_due_date_field_or_an_empty_summary_still_imports()
    {
        // Optional parts of an answer may be absent; only what the task cannot do without is required.
        using TestHost host = new(Monday9am);
        await host.ConnectIntegrationsAsync();
        host.JiraServer.AnswerJson(JiraIssuePath + "ROC-2", """{"key":"ROC-2","fields":{"summary":"Summary only"}}""");

        ReferenceOutcome outcome = Assert.Single(await host.References.ImportAsync("ROC-2"));

        Assert.True(outcome.Created, outcome.Message);
        Assert.Null((await host.Db.Tasks.SingleAsync()).DueDate);
    }

    // ---- Settings for the integrations ----

    [Fact]
    public async Task Integration_addresses_must_be_full_https_addresses_and_jira_needs_an_email()
    {
        using TestHost host = new(Monday9am);

        await Assert.ThrowsAsync<DomainRuleException>(() => host.Integrations.SaveJiraAsync("example.atlassian.net", "someone@example.com", "t"));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.Integrations.SaveJiraAsync("http://example.atlassian.net", "someone@example.com", "t"));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.Integrations.SaveJiraAsync("https://example.atlassian.net", "not an email", "t"));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.Integrations.SaveAzureDevOpsAsync("dev.azure.com/org", null, "t"));
        Assert.False(host.Integrations.HasJiraToken);
    }

    [Fact]
    public async Task Saving_settings_with_an_empty_token_box_keeps_the_saved_token()
    {
        // The token is never shown again, so re-saving the address must not wipe it.
        using TestHost host = new(Monday9am);
        await host.Integrations.SaveJiraAsync("https://example.atlassian.net", "someone@example.com", "first-token");

        await host.Integrations.SaveJiraAsync("https://other.atlassian.net", "someone@example.com", "   ");

        Assert.Equal("first-token", (await host.Integrations.GetJiraConnectionAsync()).ApiToken);
        Assert.Equal("https://other.atlassian.net", (await host.Integrations.GetJiraConnectionAsync()).SiteUrl);
    }

    [Fact]
    public async Task A_token_that_cannot_be_written_leaves_the_saved_address_and_token_unchanged()
    {
        // A new site address paired with the old site's token would send that token to the wrong place.
        using TestHost host = new(Monday9am);
        await host.Integrations.SaveJiraAsync("https://first.atlassian.net", "someone@example.com", "first-token");
        string blockingFile = Path.Combine(Path.GetTempPath(), "taskorganizer-not-a-folder-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blockingFile, "a file where the credential folder should be");
        try
        {
            CredentialStore unwritable = new(new EphemeralDataProtectionProvider(), Options.Create(new CredentialOptions { Folder = blockingFile }));
            IntegrationSettingsService integrations = new(host.Db, host.Settings, unwritable);

            ExternalServiceException error = await Assert.ThrowsAsync<ExternalServiceException>(
                () => integrations.SaveJiraAsync("https://second.atlassian.net", "other@example.com", "second-token"));

            Assert.Contains("could not be saved", error.Message);
            AppSettings settings = await host.Settings.GetAsync();
            Assert.Equal(("https://first.atlassian.net", "someone@example.com"), (settings.JiraSiteUrl!, settings.JiraEmail!));
            Assert.Equal("first-token", (await host.Integrations.GetJiraConnectionAsync()).ApiToken);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public async Task Prefix_mappings_are_upper_case_unique_and_validated()
    {
        using TestHost host = new(Monday9am);
        Project project = await host.Projects.AddAsync("Roadmap work");

        await host.Integrations.AddMappingAsync(" roc- ", project.Id);
        await Assert.ThrowsAsync<DomainRuleException>(() => host.Integrations.AddMappingAsync("ROC", project.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.Integrations.AddMappingAsync("R O C", project.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.Integrations.AddMappingAsync("NEW", 999));

        Assert.Equal("ROC", Assert.Single(await host.Integrations.ListMappingsAsync()).Prefix);
        Assert.Equal(project.Id, await host.Integrations.ProjectForJiraKeyAsync("ROC-12"));
        Assert.Null(await host.Integrations.ProjectForJiraKeyAsync("OTHER-12"));
    }

    // ---- Credentials ----

    [Fact]
    public void A_saved_token_is_encrypted_on_disk_and_can_be_read_back_only_through_the_store()
    {
        // A token sitting in plain text in a file is one careless copy away from being leaked.
        string folder = Path.Combine(Path.GetTempPath(), "taskorganizer-credentials-" + Guid.NewGuid().ToString("N"));
        try
        {
            EphemeralDataProtectionProvider protection = new();
            CredentialStore store = new(protection, Options.Create(new CredentialOptions { Folder = folder }));

            store.Save(CredentialStore.JiraApiToken, "  very-secret-token-value  ");
            string onDisk = File.ReadAllText(Path.Combine(folder, "credentials.json"));
            CredentialStore otherKeys = new(new EphemeralDataProtectionProvider(), Options.Create(new CredentialOptions { Folder = folder }));

            Assert.DoesNotContain("very-secret-token-value", onDisk);
            Assert.Equal("very-secret-token-value", store.Read(CredentialStore.JiraApiToken));
            Assert.Null(otherKeys.Read(CredentialStore.JiraApiToken));
            store.Remove(CredentialStore.JiraApiToken);
            Assert.False(store.Has(CredentialStore.JiraApiToken));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void The_local_api_key_is_long_random_and_stays_the_same_once_created()
    {
        using TestHost host = new(Monday9am);

        string first = host.Credentials.GetOrCreateLocalApiKey();
        string second = host.Credentials.GetOrCreateLocalApiKey();

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(first, File.ReadAllText(host.Credentials.LocalApiKeyPath));
    }

    // ---- The shared rule for outside items ----

    [Fact]
    public async Task An_outside_item_needs_a_web_link_and_a_key()
    {
        using TestHost host = new(Monday9am);

        await Assert.ThrowsAsync<DomainRuleException>(() => host.External.AddOrGetAsync(new ExternalItem("slack:C1/1", "Title", "javascript:alert(1)", TaskSource.Slack)));
        await Assert.ThrowsAsync<DomainRuleException>(() => host.External.AddOrGetAsync(new ExternalItem("  ", "Title", "https://example.com", TaskSource.Slack)));
        Assert.Equal(0, await host.Db.Tasks.CountAsync());
    }

    [Fact]
    public async Task A_deleted_task_leaves_no_trace_so_its_item_can_be_added_again()
    {
        using TestHost host = new(Monday9am);
        ExternalItem item = new("slack:C1/1700000000.1", "Answer the question", "https://example.slack.com/archives/C1/p1700000000100000", TaskSource.Slack);
        ExternalAddResult first = await host.External.AddOrGetAsync(item);
        await host.Tasks.DeleteAsync(first.Task.Id);

        ExternalAddResult again = await host.External.AddOrGetAsync(item);

        Assert.True(again.Created);
        Assert.Equal(1, await host.Db.Tasks.CountAsync());
    }

    // ---- Interruption with typed fields ----

    [Fact]
    public async Task An_interruption_takes_the_typed_title_project_priority_date_and_size()
    {
        // When the interruption is already known ("Sam needs the export now"), typing it once must be enough.
        using TestHost host = new(Monday9am);
        Project project = await host.Projects.AddAsync("Support");

        WorkTask task = await host.Tasks.AddInterruptionAsync(new TaskDetails("Export the report for Sam", project.Id, TaskPriority.Critical, new DateOnly(2026, 11, 16), 0.5));

        Assert.Equal("Export the report for Sam", task.Title);
        Assert.Equal((project.Id, TaskPriority.Critical, new DateOnly(2026, 11, 16), 0.5), (task.ProjectId!.Value, task.Priority, task.DueDate!.Value, task.EstimateHours!.Value));
        Assert.Equal(TaskSource.Interruption, task.Source);
        Assert.Equal(new DateOnly(2026, 11, 16), task.PlannedFor);
    }

    [Fact]
    public async Task An_interruption_with_only_other_fields_typed_still_gets_the_timestamp_title()
    {
        using TestHost host = new(Monday9am);

        WorkTask task = await host.Tasks.AddInterruptionAsync(new TaskDetails("  ", null, TaskPriority.High, null, 1));

        Assert.Equal("Interruption at 09:00", task.Title);
        Assert.Equal(TaskPriority.High, task.Priority);
    }
}
