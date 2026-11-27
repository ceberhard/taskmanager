using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TaskOrganizer.Web.Integrations;

/// <summary>Where Azure DevOps is and how to sign in. Project is optional.</summary>
public record AzureDevOpsConnection(string OrganizationUrl, string? Project, string PersonalAccessToken);

/// <summary>The parts of a pull request a task needs.</summary>
public record PullRequestInfo(int Id, string Title, string AuthorDisplayName, string WebUrl, string Status);

/// <summary>Reads pull requests from Azure DevOps over its REST API. Read-only: it never votes, comments or changes anything.</summary>
public class AzureDevOpsClient
{
    private const string ApiVersion = "7.1";
    private const string Service = "Azure DevOps";
    private const string SettingName = "organisation address";

    private readonly HttpClient _http;

    public AzureDevOpsClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>The pull request, or null when it does not exist or this token cannot see it.</summary>
    public async Task<PullRequestInfo?> GetPullRequestAsync(AzureDevOpsConnection connection, int pullRequestId, CancellationToken cancellationToken = default)
    {
        string organization = connection.OrganizationUrl.TrimEnd('/');
        string projectSegment = string.IsNullOrWhiteSpace(connection.Project) ? string.Empty : "/" + Uri.EscapeDataString(connection.Project.Trim());
        using HttpRequestMessage request = new(HttpMethod.Get, $"{organization}{projectSegment}/_apis/git/pullrequests/{pullRequestId}?api-version={ApiVersion}");
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
        string title = root.RequiredString("title");
        string author = root.RequiredObject("createdBy").OptionalString("displayName") ?? string.Empty;
        JsonAnswer repository = root.RequiredObject("repository");
        string status = root.OptionalString("status") ?? string.Empty;
        return new PullRequestInfo(pullRequestId, title, author, WebUrl(organization, repository, pullRequestId), status);
    }

    /// <summary>Checks the address and token by asking Azure DevOps who the token belongs to.</summary>
    public async Task<string> TestAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{connection.OrganizationUrl.TrimEnd('/')}/_apis/connectionData");
        request.Headers.Authorization = BasicAuth(connection);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
        ThrowIfRefused(response);
        using JsonDocument body = await ReadJsonAsync(response, cancellationToken);
        return JsonAnswer.Root(body, Service, SettingName).OptionalObject("authenticatedUser")?.OptionalString("providerDisplayName") ?? "unknown";
    }

    /// <summary>The browser address of the pull request: the repository's web address when given, otherwise built from its project and name.</summary>
    private static string WebUrl(string organization, JsonAnswer repository, int pullRequestId)
    {
        if (repository.OptionalString("webUrl") is string repositoryUrl && repositoryUrl.Length > 0)
        {
            return $"{repositoryUrl.TrimEnd('/')}/pullrequest/{pullRequestId}";
        }

        string repositoryName = repository.OptionalString("name") ?? string.Empty;
        string projectName = repository.OptionalObject("project")?.OptionalString("name") ?? string.Empty;
        return $"{organization}/{Uri.EscapeDataString(projectName)}/_git/{Uri.EscapeDataString(repositoryName)}/pullrequest/{pullRequestId}";
    }

    private static AuthenticationHeaderValue BasicAuth(AzureDevOpsConnection connection)
    {
        // Azure DevOps takes a personal access token as the password with an empty user name.
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + connection.PersonalAccessToken)));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException("Azure DevOps did not answer in time. Try again in a moment.");
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException($"Azure DevOps could not be reached ({ex.Message}). Check the organisation address in Settings.");
        }
    }

    private static void ThrowIfRefused(HttpResponseMessage response)
    {
        // A token Azure DevOps does not accept often gets a 203 with a sign-in web page instead of a 401.
        bool isJson = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation
            || (response.IsSuccessStatusCode && !isJson))
        {
            throw new ExternalServiceException("Azure DevOps did not accept the personal access token. Check it in Settings (it needs Code: Read).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalServiceException($"Azure DevOps answered with an error ({(int)response.StatusCode}). Try again later.");
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
            throw new ExternalServiceException("Azure DevOps sent an answer that could not be read. Check the organisation address in Settings.");
        }
    }
}
