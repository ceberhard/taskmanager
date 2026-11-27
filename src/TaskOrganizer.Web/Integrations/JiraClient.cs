using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TaskOrganizer.Web.Integrations;

/// <summary>Where Jira is and how to sign in. Built per call from Settings and the credential store.</summary>
public record JiraConnection(string SiteUrl, string Email, string ApiToken);

/// <summary>The parts of a Jira ticket a task needs.</summary>
public record JiraIssue(string Key, string Summary, DateOnly? DueDate, string BrowseUrl);

/// <summary>Reads Jira Cloud tickets over its REST API. Read-only: it never changes anything in Jira.</summary>
public class JiraClient
{
    private const string Service = "Jira";
    private const string SettingName = "site address";

    private readonly HttpClient _http;

    public JiraClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>The ticket, or null when Jira says it does not exist or this account cannot see it.</summary>
    public async Task<JiraIssue?> GetIssueAsync(JiraConnection connection, string key, CancellationToken cancellationToken = default)
    {
        string site = connection.SiteUrl.TrimEnd('/');
        using HttpRequestMessage request = new(HttpMethod.Get, $"{site}/rest/api/3/issue/{Uri.EscapeDataString(key)}?fields=summary,duedate");
        request.Headers.Authorization = BasicAuth(connection);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        ThrowIfRefused(response);
        using JsonDocument body = await ReadJsonAsync(response, cancellationToken);
        JsonAnswer root = JsonAnswer.Root(body, Service, SettingName);
        string issueKey = root.RequiredString("key");
        JsonAnswer fields = root.RequiredObject("fields");
        string summary = fields.RequiredString("summary");

        // The due date is optional in Jira; a missing or unreadable one simply leaves the task undated.
        DateOnly? dueDate = DateOnly.TryParseExact(fields.OptionalString("duedate"), "yyyy-MM-dd", out DateOnly parsedDue) ? parsedDue : null;

        return new JiraIssue(issueKey, summary, dueDate, $"{site}/browse/{issueKey}");
    }

    /// <summary>Checks the address and token by asking Jira who the token belongs to.</summary>
    public async Task<string> TestAsync(JiraConnection connection, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{connection.SiteUrl.TrimEnd('/')}/rest/api/3/myself");
        request.Headers.Authorization = BasicAuth(connection);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
        ThrowIfRefused(response);
        using JsonDocument body = await ReadJsonAsync(response, cancellationToken);
        return JsonAnswer.Root(body, Service, SettingName).OptionalString("displayName") ?? "unknown";
    }

    private static AuthenticationHeaderValue BasicAuth(JiraConnection connection)
    {
        string pair = $"{connection.Email}:{connection.ApiToken}";
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException("Jira did not answer in time. Try again in a moment.");
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException($"Jira could not be reached ({ex.Message}). Check the site address in Settings.");
        }
    }

    private static void ThrowIfRefused(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ExternalServiceException("Jira did not accept the email and API token. Check them in Settings.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalServiceException($"Jira answered with an error ({(int)response.StatusCode}). Try again later.");
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new ExternalServiceException("Jira sent an answer that could not be read. Check the site address in Settings.");
        }
    }
}
