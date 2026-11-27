using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Integrations;
using TaskOrganizer.Web.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The database lives beside the app unless configured otherwise. It holds real work data
// and is excluded from source control.
string databasePath = Path.GetFullPath(
    builder.Configuration["Database:Path"] ?? "App_Data/taskorganizer.db",
    builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

builder.Services.AddRazorPages();
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<LocalClock>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<TimeLogService>();
builder.Services.AddScoped<PunchdownImporter>();
builder.Services.AddScoped<TimeLogImporter>();
builder.Services.AddScoped<DaySuggestionService>();
builder.Services.AddScoped<HoldReasonService>();
builder.Services.AddScoped<ExternalTaskService>();

// Integrations. Tokens live in the credential folder outside the repository, never in the database.
builder.Services.Configure<CredentialOptions>(builder.Configuration.GetSection(CredentialOptions.SectionName));
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddScoped<IntegrationSettingsService>();
builder.Services.AddScoped<ReferenceImportService>();
TimeSpan integrationTimeout = TimeSpan.FromSeconds(20);
builder.Services.AddHttpClient<JiraClient>(client => client.Timeout = integrationTimeout);
builder.Services.AddHttpClient<AzureDevOpsClient>(client => client.Timeout = integrationTimeout);
builder.Services.Configure<UiOptions>(builder.Configuration.GetSection(UiOptions.SectionName));

WebApplication app = builder.Build();

// Schema changes go through migrations only; the database file is never recreated.
using (IServiceScope scope = app.Services.CreateScope())
{
    AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    IEnumerable<string> pendingMigrations = await db.Database.GetPendingMigrationsAsync();
    if (pendingMigrations.Any())
    {
        string? backupPath = DatabaseBackup.Create(databasePath, DateTime.Now);
        if (backupPath is not null)
        {
            app.Logger.LogInformation("Database backed up to {BackupPath} before applying migrations.", backupPath);
        }
    }

    await db.Database.MigrateAsync();

    // Local tools (the Slack capture skill) read this key from the credential folder; create it up front so it is always there.
    scope.ServiceProvider.GetRequiredService<CredentialStore>().GetOrCreateLocalApiKey();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapLocalApi();

app.Run();

/// <summary>Declared so the test project can start the real application.</summary>
public partial class Program
{
}
