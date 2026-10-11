using Microsoft.Data.Sqlite;

namespace Capybara.Scheduling.Durable;

/// <summary>
/// The SQLite file shared by Quartz, the durable run history, auto-pause interaction times and the user's routine
/// enable/disable decisions (FR-RTN-04, FR-RTN-05).
/// Pooling is disabled so closing a connection releases the file handle immediately; scheduling volume is low enough
/// that pooling has no benefit.
/// </summary>
internal static class SchedulingDatabase
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS capybara_routine_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            routine_id TEXT NOT NULL,
            started_at TEXT NOT NULL,
            outcome TEXT NOT NULL,
            summary TEXT NULL,
            credits TEXT NOT NULL,
            evidence_json TEXT NULL);
        CREATE INDEX IF NOT EXISTS ix_capybara_routine_runs_routine ON capybara_routine_runs (routine_id, id);
        CREATE TABLE IF NOT EXISTS capybara_routine_interactions (
            routine_id TEXT PRIMARY KEY,
            at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS capybara_routine_overrides (
            routine_id TEXT PRIMARY KEY,
            enabled INTEGER NOT NULL,
            acknowledged_credits TEXT NULL,
            spec_enabled INTEGER NOT NULL);
        """;

    public static string ConnectionString(string path) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    /// <summary>Creates the directory and tables if they do not exist yet.</summary>
    public static void Initialize(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();
    }

    public static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        return connection;
    }
}
