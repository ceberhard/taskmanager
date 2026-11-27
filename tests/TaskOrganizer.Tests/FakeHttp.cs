using System.Net;
using System.Text;
using System.Text.Json;

namespace TaskOrganizer.Tests;

/// <summary>
/// Stands in for Jira or Azure DevOps. Answers are registered by path; anything not registered
/// is a 404, as a real service answers for an item that does not exist. Every request is kept
/// so a test can check what was sent. No test ever reaches the network.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _answers = new(StringComparer.OrdinalIgnoreCase);

    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>When set, every request fails the way an unreachable or slow server does.</summary>
    public Exception? FailWith { get; set; }

    public void Answer(string path, HttpStatusCode status, string body, string contentType = "application/json")
    {
        _answers[path] = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
    }

    public void AnswerJson(string path, string json) => Answer(path, HttpStatusCode.OK, json);

    /// <summary>Every request gets this status, as when a token is refused.</summary>
    public void AnswerEverything(HttpStatusCode status, string body = "{}", string contentType = "application/json")
    {
        _answers["*"] = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (FailWith is not null)
        {
            throw FailWith;
        }

        if (_answers.TryGetValue("*", out Func<HttpResponseMessage>? everything))
        {
            return Task.FromResult(everything());
        }

        string path = request.RequestUri!.AbsolutePath;
        return Task.FromResult(_answers.TryGetValue(path, out Func<HttpResponseMessage>? answer)
            ? answer()
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    public static string JiraIssueJson(string key, string summary, string? dueDate = null)
    {
        return JsonSerializer.Serialize(new { key, fields = new { summary, duedate = dueDate } });
    }

    public static string PullRequestJson(int id, string title, string author, string repositoryWebUrl = "https://dev.azure.com/example-org/Example/_git/example-repo")
    {
        return JsonSerializer.Serialize(new
        {
            pullRequestId = id,
            title,
            status = "active",
            createdBy = new { displayName = author },
            repository = new { name = "example-repo", webUrl = repositoryWebUrl, project = new { name = "Example" } }
        });
    }
}
