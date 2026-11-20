using Microsoft.Data.Sqlite;

namespace TaskOrganizer.Web.Data;

/// <summary>
/// Copies the database file before a schema change. The database is the only copy of the
/// data, so a migration must never run without a way back.
/// </summary>
public static class DatabaseBackup
{
    public const int BackupsToKeep = 10;
    private const string BackupFolderName = "backups";

    /// <summary>
    /// Writes a timestamped copy into a "backups" folder beside the database and removes the
    /// oldest copies beyond <see cref="BackupsToKeep"/>. Returns the backup path, or null when
    /// there is nothing to back up yet.
    /// </summary>
    public static string? Create(string databasePath, DateTime localNow)
    {
        FileInfo database = new(databasePath);
        if (!database.Exists || database.Length == 0)
        {
            return null;
        }

        string backupFolder = Path.Combine(database.DirectoryName!, BackupFolderName);
        Directory.CreateDirectory(backupFolder);
        string baseName = Path.GetFileNameWithoutExtension(database.Name);
        string backupPath = Path.Combine(backupFolder, $"{baseName}-{localNow:yyyyMMdd-HHmmss}{database.Extension}");

        // SQLite's own backup is used instead of a file copy so that changes still sitting in
        // the write-ahead log (for example after a crash) are included.
        using (SqliteConnection source = new(ConnectionString(databasePath, SqliteOpenMode.ReadOnly)))
        using (SqliteConnection destination = new(ConnectionString(backupPath, SqliteOpenMode.ReadWriteCreate)))
        {
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }

        Prune(backupFolder, baseName, database.Extension);
        return backupPath;
    }

    private static void Prune(string backupFolder, string baseName, string extension)
    {
        // The timestamp format sorts by name in date order.
        IEnumerable<string> surplus = Directory
            .GetFiles(backupFolder, $"{baseName}-*{extension}")
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Skip(BackupsToKeep);
        foreach (string path in surplus)
        {
            File.Delete(path);
        }
    }

    private static string ConnectionString(string path, SqliteOpenMode mode)
    {
        // Pooling is off so the files are released as soon as the copy finishes.
        return new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
    }
}
