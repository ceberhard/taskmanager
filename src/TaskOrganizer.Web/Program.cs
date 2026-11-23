using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
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
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();

/// <summary>Declared so the test project can start the real application.</summary>
public partial class Program
{
}
