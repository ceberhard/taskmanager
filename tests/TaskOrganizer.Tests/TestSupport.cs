using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Tests;

/// <summary>A clock the test moves by hand, in a chosen time zone.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly TimeZoneInfo _zone;
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTime localNow, TimeZoneInfo? zone = null)
    {
        _zone = zone ?? TimeZoneInfo.Utc;
        SetLocal(localNow);
    }

    public override TimeZoneInfo LocalTimeZone => _zone;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetLocal(DateTime localNow)
    {
        DateTime unspecified = DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified);
        _utcNow = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, _zone), TimeSpan.Zero);
    }

    public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
}

/// <summary>
/// A real SQLite database held in memory and built by the same migrations the app runs,
/// so the tests also prove the migrations produce a working schema.
/// </summary>
public sealed class TestHost : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestHost(DateTime localNow, TimeZoneInfo? zone = null)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Time = new FakeTimeProvider(localNow, zone);
        Db = NewContext();
        Db.Database.Migrate();

        Clock = new LocalClock(Time);
        Settings = new SettingsService(Db);
        Projects = new ProjectService(Db, Clock);
        Tasks = new TaskService(Db, Clock, Settings);
        TimeLog = new TimeLogService(Db, Clock, Settings);
        Importer = new PunchdownImporter(Db, Clock);
    }

    public FakeTimeProvider Time { get; }
    public AppDbContext Db { get; }
    public LocalClock Clock { get; }
    public SettingsService Settings { get; }
    public ProjectService Projects { get; }
    public TaskService Tasks { get; }
    public TimeLogService TimeLog { get; }
    public PunchdownImporter Importer { get; }

    /// <summary>A second context on the same database, to read what was actually saved.</summary>
    public AppDbContext NewContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        return new AppDbContext(options);
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
