using Fleet.Contracts.Storage;
using Microsoft.Data.Sqlite;

namespace Fleet.Hosting.Storage;

/// <summary>SQLite implementation of durable JSON state with WAL and parameterized SQL (FR-HST-06).</summary>
public sealed class SqliteStateStore : IStateStore
{
    private readonly string _connectionString;

    public SqliteStateStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        Initialize();
    }

    public string DatabasePath { get; }

    public async ValueTask<string?> GetAsync(string collection, string key, CancellationToken cancellationToken = default)
    {
        Validate(collection, key);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM state WHERE collection = $collection AND key = $key;";
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async ValueTask PutAsync(string collection, string key, string json, CancellationToken cancellationToken = default)
    {
        Validate(collection, key);
        ArgumentNullException.ThrowIfNull(json);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO state(collection, key, json, updated)
            VALUES ($collection, $key, $json, unixepoch())
            ON CONFLICT(collection, key) DO UPDATE SET json = excluded.json, updated = excluded.updated;
            """;
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<bool> DeleteAsync(string collection, string key, CancellationToken cancellationToken = default)
    {
        Validate(collection, key);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM state WHERE collection = $collection AND key = $key;";
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async ValueTask<IReadOnlyList<KeyValuePair<string, string>>> ListAsync(string collection, string? keyPrefix = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = keyPrefix is null
            ? "SELECT key, json FROM state WHERE collection = $collection ORDER BY key;"
            : @"SELECT key, json FROM state WHERE collection = $collection AND key LIKE $prefix ESCAPE '\' ORDER BY key;";
        command.Parameters.AddWithValue("$collection", collection);
        if (keyPrefix is not null)
        {
            command.Parameters.AddWithValue("$prefix", EscapeLike(keyPrefix) + "%");
        }

        var items = new List<KeyValuePair<string, string>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));
        }

        return items;
    }

    internal SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Validate(string collection, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS state(
                collection TEXT NOT NULL,
                key TEXT NOT NULL,
                json TEXT NOT NULL,
                updated INTEGER NOT NULL,
                PRIMARY KEY(collection, key)
            );
            """;
        command.ExecuteNonQuery();
    }
}
