using System.Security.Cryptography;
using System.Text;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Integrations;

/// <summary>A task offered by a local tool, such as the Slack capture skill.</summary>
public record ExternalTaskRequest(string? Source, string? Key, string? Title, string? Url);

public record ExternalTaskResponse(int Id, bool Created, string Title);

public record TimeTodayResponse(bool Running, string TodayHours, string WeekHours);

/// <summary>
/// The app's two small JSON endpoints. Neither uses cookies or forms, so a web page open in the
/// browser cannot drive them: adding a task needs the local key in a custom header, which a
/// browser will not send cross-site without a CORS permission this app never grants.
/// </summary>
public static class LocalApi
{
    public const string KeyHeader = "X-TaskOrganizer-Key";

    /// <summary>Sources a local tool may add tasks for. Jira and pull requests are fetched by the app itself.</summary>
    private static readonly IReadOnlyDictionary<string, TaskSource> AllowedSources = new Dictionary<string, TaskSource>(StringComparer.OrdinalIgnoreCase)
    {
        ["slack"] = TaskSource.Slack
    };

    public static void MapLocalApi(this WebApplication app)
    {
        app.MapPost("/api/external-tasks", AddExternalTaskAsync);
        app.MapGet("/api/time/today", GetTimeTodayAsync);
    }

    private static async Task<IResult> AddExternalTaskAsync(HttpRequest httpRequest, ExternalTaskRequest request, CredentialStore credentials, ExternalTaskService external, ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger(typeof(LocalApi));
        if (!KeyMatches(httpRequest.Headers[KeyHeader].ToString(), credentials.GetOrCreateLocalApiKey()))
        {
            logger.LogWarning("Local endpoint refused a request: missing or wrong local key.");
            return Results.Problem("Missing or wrong local key.", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (request.Source is null || !AllowedSources.TryGetValue(request.Source, out TaskSource source))
        {
            logger.LogWarning("Local endpoint refused a request: source {Source} is not accepted.", request.Source);
            return Results.Problem($"Source must be one of: {string.Join(", ", AllowedSources.Keys)}.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            logger.LogWarning("Local endpoint refused a request: no title.");
            return Results.Problem("A title is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        // The key is what keeps one message one task; without it, unrelated messages would all share "slack:".
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            logger.LogWarning("Local endpoint refused a request: no key identifying the message.");
            return Results.Problem("A key identifying the message is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            // Keys are namespaced by source so a Slack key can never collide with a Jira or PR key.
            string key = $"{request.Source.ToLowerInvariant()}:{request.Key.Trim()}";
            ExternalAddResult result = await external.AddOrGetAsync(new ExternalItem(key, request.Title, request.Url ?? string.Empty, source));
            ExternalTaskResponse body = new(result.Task.Id, result.Created, result.Task.Title);

            // Ids only: titles are written from other people's messages and stay out of the log.
            logger.LogInformation("Local endpoint: {Source} item {Outcome} as task {TaskId}.", source, result.Created ? "added" : "already present", result.Task.Id);
            return result.Created ? Results.Created($"/History?id={result.Task.Id}", body) : Results.Ok(body);
        }
        catch (DomainRuleException ex)
        {
            logger.LogWarning("Local endpoint refused a request: {Reason}", ex.Message);
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>The time figures shown on every page, so a page can keep them current while the clock runs.</summary>
    private static async Task<IResult> GetTimeTodayAsync(TimeLogService timeLog, LocalClock clock)
    {
        ClockStatus status = await timeLog.GetClockStatusAsync();
        WeekSummary week = await timeLog.GetWeekAsync(clock.Today);
        return Results.Ok(new TimeTodayResponse(status.RunningEntry is not null, TextFormat.Hours(status.TodayHours), TextFormat.Hours(week.WeeklyTotalHours)));
    }

    private static bool KeyMatches(string supplied, string expected)
    {
        return supplied.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }
}
