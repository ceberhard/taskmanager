using System.Text.RegularExpressions;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Integrations;

/// <summary>What happened to one typed reference. Failed means the service could not be used; not found or already a task is neither created nor failed.</summary>
public record ReferenceOutcome(string Reference, string Message, bool Created, bool Failed = false);

/// <summary>
/// Turns what was typed in the "Jira key or PR #" box into tasks. Jira keys (ROC-4698, or a
/// ticket link) and pull request numbers (26512, #26512, PR 26512, or a pull request link) can
/// be mixed, several at a time. One reference that is not found is reported and the rest carry on;
/// a service that cannot be used at all stops that service's references with the reason.
/// </summary>
public partial class ReferenceImportService
{
    private const int MaxReferencesPerRequest = 25;

    private readonly ExternalTaskService _external;
    private readonly IntegrationSettingsService _integrations;
    private readonly SettingsService _settings;
    private readonly JiraClient _jira;
    private readonly AzureDevOpsClient _azureDevOps;
    private readonly ILogger<ReferenceImportService> _logger;

    public ReferenceImportService(ExternalTaskService external, IntegrationSettingsService integrations, SettingsService settings, JiraClient jira, AzureDevOpsClient azureDevOps, ILogger<ReferenceImportService> logger)
    {
        _logger = logger;
        _external = external;
        _integrations = integrations;
        _settings = settings;
        _jira = jira;
        _azureDevOps = azureDevOps;
    }

    public async Task<List<ReferenceOutcome>> ImportAsync(string? input)
    {
        (List<string> jiraKeys, List<int> pullRequestIds) = Parse(input);
        if (jiraKeys.Count + pullRequestIds.Count == 0)
        {
            throw new DomainRuleException("Type a Jira key such as ROC-4698, or a pull request number such as 26512.");
        }

        if (jiraKeys.Count + pullRequestIds.Count > MaxReferencesPerRequest)
        {
            throw new DomainRuleException($"Add at most {MaxReferencesPerRequest} at a time.");
        }

        List<ReferenceOutcome> outcomes = new();
        if (jiraKeys.Count > 0)
        {
            outcomes.AddRange(await ImportJiraAsync(jiraKeys));
        }

        if (pullRequestIds.Count > 0)
        {
            outcomes.AddRange(await ImportPullRequestsAsync(pullRequestIds));
        }

        int added = outcomes.Count(o => o.Created);
        int failed = outcomes.Count(o => o.Failed);
        _logger.LogInformation("Reference import: {Added} added, {AlreadyPresent} already present, {Failed} failed.", added, outcomes.Count - added - failed, failed);
        return outcomes;
    }

    /// <summary>Picks Jira keys and pull request numbers out of free text, without duplicates, in the order typed.</summary>
    public static (List<string> JiraKeys, List<int> PullRequestIds) Parse(string? input)
    {
        string text = input ?? string.Empty;
        List<int> pullRequestIds = new();

        // Pull request links and "PR 123" / "#123" first, then strip them so their digits are not read again.
        foreach (Match match in PullRequestPattern().Matches(text))
        {
            AddPullRequest(pullRequestIds, match.Groups["id"].Value);
        }

        string withoutPullRequests = PullRequestPattern().Replace(text, " ");
        List<string> jiraKeys = JiraKeyPattern().Matches(withoutPullRequests)
            .Select(m => m.Value.ToUpperInvariant())
            .Distinct()
            .ToList();

        // A bare number counts as a pull request only outside links, so "/v2/" in some other link is not read as PR 2.
        string withoutKeys = JiraKeyPattern().Replace(LinkPattern().Replace(withoutPullRequests, " "), " ");
        foreach (Match match in BareNumberPattern().Matches(withoutKeys))
        {
            AddPullRequest(pullRequestIds, match.Value);
        }

        return (jiraKeys, pullRequestIds);
    }

    private static void AddPullRequest(List<int> ids, string digits)
    {
        if (int.TryParse(digits, out int id) && id > 0 && !ids.Contains(id))
        {
            ids.Add(id);
        }
    }

    private async Task<List<ReferenceOutcome>> ImportJiraAsync(List<string> keys)
    {
        List<ReferenceOutcome> outcomes = new();
        JiraConnection connection;
        try
        {
            connection = await _integrations.GetJiraConnectionAsync();
        }
        catch (ExternalServiceException ex)
        {
            return keys.Select(key => Failure("Jira", key, ex)).ToList();
        }

        for (int index = 0; index < keys.Count; index++)
        {
            string key = keys[index];
            try
            {
                JiraIssue? issue = await _jira.GetIssueAsync(connection, key);
                if (issue is null)
                {
                    outcomes.Add(new ReferenceOutcome(key, $"{key} was not found in Jira, or this account cannot see it.", Created: false));
                    continue;
                }

                ExternalItem item = new(
                    "jira:" + issue.Key,
                    $"{issue.Key}: {issue.Summary}",
                    issue.BrowseUrl,
                    TaskSource.Jira,
                    await _integrations.ProjectForJiraKeyAsync(issue.Key),
                    issue.DueDate);
                outcomes.Add(Describe(issue.Key, await _external.AddOrGetAsync(item)));
            }
            catch (ExternalServiceException ex)
            {
                // The service itself failed; the remaining keys would fail the same way.
                outcomes.AddRange(keys.Skip(index).Select(rest => Failure("Jira", rest, ex)));
                break;
            }
        }

        return outcomes;
    }

    private async Task<List<ReferenceOutcome>> ImportPullRequestsAsync(List<int> ids)
    {
        List<ReferenceOutcome> outcomes = new();
        AzureDevOpsConnection connection;
        try
        {
            connection = await _integrations.GetAzureDevOpsConnectionAsync();
        }
        catch (ExternalServiceException ex)
        {
            return ids.Select(id => Failure("Azure DevOps", $"PR {id}", ex)).ToList();
        }

        AppSettings settings = await _settings.GetAsync();
        for (int index = 0; index < ids.Count; index++)
        {
            int id = ids[index];
            try
            {
                PullRequestInfo? pullRequest = await _azureDevOps.GetPullRequestAsync(connection, id);
                if (pullRequest is null)
                {
                    outcomes.Add(new ReferenceOutcome($"PR {id}", $"PR {id} was not found in Azure DevOps, or this token cannot see it.", Created: false));
                    continue;
                }

                ExternalItem item = new(
                    $"ado-pr:{id}",
                    PullRequestTitle(pullRequest),
                    pullRequest.WebUrl,
                    TaskSource.PullRequest,
                    settings.PullRequestProjectId,
                    DueDate: null,
                    settings.PullRequestDefaultPriority,
                    settings.PullRequestDefaultHours);
                outcomes.Add(Describe($"PR {id}", await _external.AddOrGetAsync(item)));
            }
            catch (ExternalServiceException ex)
            {
                outcomes.AddRange(ids.Skip(index).Select(rest => Failure("Azure DevOps", $"PR {rest}", ex)));
                break;
            }
        }

        return outcomes;
    }

    /// <summary>"PR 26512 - Rakesh - Add retry to the sync", the same naming as PR review sessions.</summary>
    public static string PullRequestTitle(PullRequestInfo pullRequest)
    {
        return $"PR {pullRequest.Id} - {FirstName(pullRequest.AuthorDisplayName)} - {pullRequest.Title}";
    }

    /// <summary>The first name from a display name, whether written "First Last" or "Last, First".</summary>
    public static string FirstName(string displayName)
    {
        string name = displayName.Trim();
        if (name.Length == 0)
        {
            return "Unknown";
        }

        int comma = name.IndexOf(',');
        string firstPart = comma >= 0 ? name[(comma + 1)..].Trim() : name;
        string[] words = firstPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? name : words[0];
    }

    /// <summary>Records why a reference could not be fetched, with the service and the reference, never the token.</summary>
    private ReferenceOutcome Failure(string service, string reference, ExternalServiceException ex)
    {
        _logger.LogWarning("{Service} lookup for {Reference} failed: {Reason}", service, reference, ex.Message);
        return new ReferenceOutcome(reference, ex.Message, Created: false, Failed: true);
    }

    private static ReferenceOutcome Describe(string reference, ExternalAddResult result)
    {
        return result.Created
            ? new ReferenceOutcome(reference, $"Added: {result.Task.Title}", Created: true)
            : new ReferenceOutcome(reference, $"{reference} is already a task ({TextFormat.Status(result.Task.Status)}): {result.Task.Title}", Created: false);
    }

    // A Jira key: letters (then letters or digits), a dash, digits. Matches inside links too.
    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9]{0,19}-\d+\b")]
    private static partial Regex JiraKeyPattern();

    // A pull request link (".../pullrequest/123"), "PR 123", "PR123", "PR-123" or "#123".
    [GeneratedRegex(@"(?:/pullrequest/|\bPR[\s#-]*|#)(?<id>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PullRequestPattern();

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex BareNumberPattern();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();
}
