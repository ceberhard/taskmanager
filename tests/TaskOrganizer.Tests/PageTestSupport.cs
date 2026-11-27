using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TaskOrganizer.Web.Integrations;

namespace TaskOrganizer.Tests;

/// <summary>
/// Starts the real application against a throwaway database file, so tests go through
/// model binding, antiforgery, the handlers and the Razor views exactly as a browser does.
/// </summary>
public sealed partial class AppFactory : WebApplicationFactory<Program>
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "taskorganizer-pages-" + Guid.NewGuid().ToString("N"));

    public AppFactory()
    {
        Directory.CreateDirectory(_folder);
    }

    /// <summary>The app's clock, fixed so page tests never depend on the real date or on midnight.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTime(2026, 11, 19, 10, 0, 0));

    public DateOnly Today => DateOnly.FromDateTime(Clock.GetLocalNow().DateTime);

    public CapturingLoggerProvider Logs { get; } = new();

    public FakeHttpHandler JiraServer { get; } = new();

    public FakeHttpHandler AzureDevOpsServer { get; } = new();

    /// <summary>Throwaway folder standing in for the credential folder under %LOCALAPPDATA%.</summary>
    public string CredentialFolder => Path.Combine(_folder, "credentials");

    public string DatabasePath => Path.Combine(_folder, "pages.db");

    /// <summary>Where a test may put a workbook for the import handlers to read.</summary>
    public string WorkbookPath => Path.Combine(_folder, "import.xlsx");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Path", DatabasePath);
        builder.UseSetting("Import:WorkbookPath", WorkbookPath);
        builder.UseSetting("Credentials:Folder", CredentialFolder);
        builder.UseSetting("Ui:TimeRefreshSeconds", "1");
        builder.ConfigureLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Debug));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            // The app talks to fake Jira and Azure DevOps servers, never the real ones.
            services.AddHttpClient<JiraClient>().ConfigurePrimaryHttpMessageHandler(() => JiraServer);
            services.AddHttpClient<AzureDevOpsClient>().ConfigurePrimaryHttpMessageHandler(() => AzureDevOpsServer);
        });
    }

    /// <summary>Runs code against the app's own services, to arrange data or check what was saved.</summary>
    public async Task<TResult> WithServicesAsync<TResult>(Func<IServiceProvider, Task<TResult>> action)
    {
        using IServiceScope scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Loads a page, then posts a form to it the way a browser would, antiforgery token included.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageWithForm, string postUrl, IEnumerable<KeyValuePair<string, string>> fields)
    {
        string html = await client.GetStringAsync(pageWithForm);
        return await PostFormWithTokenFromAsync(client, html, postUrl, fields);
    }

    /// <summary>Posts a form using the antiforgery token of a page that was already fetched.</summary>
    public static async Task<HttpResponseMessage> PostFormWithTokenFromAsync(HttpClient client, string html, string postUrl, IEnumerable<KeyValuePair<string, string>> fields)
    {
        Match token = TokenPattern().Match(html);
        Assert.True(token.Success, "The page has no antiforgery token.");

        List<KeyValuePair<string, string>> form = new(fields)
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", WebUtility.HtmlDecode(token.Groups[1].Value))
        };
        return await client.PostAsync(postUrl, new FormUrlEncodedContent(form));
    }

    public static KeyValuePair<string, string> Field(string name, string value) => new(name, value);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // The base class can run this more than once, so the folder may already be gone.
        if (disposing && Directory.Exists(_folder))
        {
            // Pooled connections keep the database file open; release them before deleting it.
            SqliteConnection.ClearAllPools();
            Directory.Delete(_folder, recursive: true);
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
