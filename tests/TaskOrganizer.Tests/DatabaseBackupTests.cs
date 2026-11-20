using Microsoft.Data.Sqlite;
using TaskOrganizer.Web.Data;

namespace TaskOrganizer.Tests;

public sealed class DatabaseBackupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "taskorganizer-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public DatabaseBackupTests()
    {
        Directory.CreateDirectory(_folder);
        _databasePath = Path.Combine(_folder, "work.db");
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private void CreateDatabaseWithOneNote(string note)
    {
        using SqliteConnection connection = Open(_databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS Notes (Text TEXT); INSERT INTO Notes VALUES ($note);";
        command.Parameters.AddWithValue("$note", note);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void A_backup_holds_the_data_as_it_was_before_the_change()
    {
        // The backup is the only way back from a bad migration, so it has to be a readable copy of the real data.
        CreateDatabaseWithOneNote("before migration");

        string? backupPath = DatabaseBackup.Create(_databasePath, new DateTime(2026, 11, 19, 7, 30, 0));
        CreateDatabaseWithOneNote("after migration");

        Assert.NotNull(backupPath);
        Assert.EndsWith(Path.Combine("backups", "work-20261119-073000.db"), backupPath);
        using SqliteConnection backup = Open(backupPath);
        using SqliteCommand command = backup.CreateCommand();
        command.CommandText = "SELECT group_concat(Text) FROM Notes;";
        Assert.Equal("before migration", command.ExecuteScalar());
    }

    [Fact]
    public void Nothing_is_backed_up_before_the_database_exists()
    {
        string? backupPath = DatabaseBackup.Create(_databasePath, new DateTime(2026, 11, 19, 7, 30, 0));

        Assert.Null(backupPath);
        Assert.False(Directory.Exists(Path.Combine(_folder, "backups")));
    }

    [Fact]
    public void Only_the_newest_backups_are_kept_so_the_folder_does_not_grow_forever()
    {
        CreateDatabaseWithOneNote("data");
        DateTime first = new(2026, 11, 1, 8, 0, 0);

        for (int day = 0; day < DatabaseBackup.BackupsToKeep + 3; day++)
        {
            DatabaseBackup.Create(_databasePath, first.AddDays(day));
        }

        List<string> kept = Directory.GetFiles(Path.Combine(_folder, "backups")).Select(path => Path.GetFileName(path)).Order().ToList();
        Assert.Equal(DatabaseBackup.BackupsToKeep, kept.Count);
        Assert.Equal("work-20261104-080000.db", kept.First());
        Assert.Equal("work-20261113-080000.db", kept.Last());
    }
}
