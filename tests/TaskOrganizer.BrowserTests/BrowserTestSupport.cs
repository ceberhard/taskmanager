using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TaskOrganizer.Tests;

namespace TaskOrganizer.BrowserTests;

/// <summary>
/// One browser for a whole test class. It uses a browser already installed on the machine
/// (Edge by default), so nothing has to be downloaded. Set TASKORGANIZER_BROWSER_CHANNEL to
/// "chrome" to use Chrome, and TASKORGANIZER_BROWSER_HEADED to "1" to watch the tests run.
/// </summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private const string ChannelVariable = "TASKORGANIZER_BROWSER_CHANNEL";
    private const string HeadedVariable = "TASKORGANIZER_BROWSER_HEADED";
    private const string DefaultChannel = "msedge";

    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = Environment.GetEnvironmentVariable(ChannelVariable) ?? DefaultChannel,
            Headless = Environment.GetEnvironmentVariable(HeadedVariable) != "1"
        });
    }

    public async Task DisposeAsync()
    {
        await Browser.DisposeAsync();
        _playwright?.Dispose();
    }
}

/// <summary>
/// One test's world: the real application listening on its own local port with an empty
/// throwaway database and a fixed clock, plus a fresh browser page pointed at it.
/// </summary>
public sealed class AppSession : IAsyncDisposable
{
    private readonly IBrowserContext _context;

    private AppSession(AppFactory app, IBrowserContext context, IPage page)
    {
        App = app;
        _context = context;
        Page = page;
    }

    public AppFactory App { get; }

    public IPage Page { get; }

    public static async Task<AppSession> StartAsync(IBrowser browser)
    {
        AppFactory app = new();

        // Port 0 asks the operating system for any free port, so tests can run side by side.
        app.UseKestrel(0);
        app.StartServer();
        string boundAddress = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        // The server reports the loopback IP it bound to, but the app only answers to the host
        // name "localhost" (AllowedHosts), so the browser is sent there on the same port.
        if (!Uri.TryCreate(boundAddress, UriKind.Absolute, out Uri? bound))
        {
            throw new InvalidOperationException($"The test server reported an unusable address: {boundAddress}");
        }

        string baseUrl = $"http://localhost:{bound.Port}";

        IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = baseUrl });
        IPage page = await context.NewPageAsync();
        return new AppSession(app, context, page);
    }

    /// <summary>The task rows on the current page whose text contains the given title.</summary>
    public ILocator TaskRow(string title) => Page.Locator("li.task-row", new PageLocatorOptions { HasText = title });

    /// <summary>The green or red message shown at the top of a page after an action.</summary>
    public ILocator Message => Page.Locator(".alert");

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await App.DisposeAsync();
    }
}
