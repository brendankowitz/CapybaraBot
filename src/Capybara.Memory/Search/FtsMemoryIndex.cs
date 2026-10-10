using Capybara.Memory.Access;
using Capybara.Memory.Pages;
using Capybara.Memory.Paths;
using Capybara.Memory.Store;
using Microsoft.Data.Sqlite;

namespace Capybara.Memory.Search;

/// <summary>SQLite FTS5 index for wiki pages once linear search exceeds the configured budget (FR-MEM-11).</summary>
public sealed class FtsMemoryIndex
{
    private readonly string _databasePath;
    private bool _initialized;

    public FtsMemoryIndex(MemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _databasePath = Path.Combine(Path.GetFullPath(options.Root), ".memory-index.sqlite");
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = Open();
        await ExecuteAsync(connection, "CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(path UNINDEXED, title, description, body, scope UNINDEXED, updated UNINDEXED)", cancellationToken);
        _initialized = true;
    }

    public async Task RebuildAsync(IEnumerable<MemoryPage> pages, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = Open();
        await ExecuteAsync(connection, "DELETE FROM memory_fts", cancellationToken);
        foreach (var page in pages)
        {
            await UpsertAsync(connection, page, cancellationToken);
        }
    }

    public async ValueTask UpsertAsync(MemoryPage page, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = Open();
        await UpsertAsync(connection, page, cancellationToken);
    }

    public async ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_fts WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<int> ApproximateTokenCountAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(length(title) + length(description) + length(body)), 0) FROM memory_fts";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) / 4;
    }

    public async Task<IReadOnlyList<IndexEntry>> SearchAsync(MemoryAccessContext context, string query, int limit, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var safeLimit = Math.Clamp(limit, 1, 100);
        var result = new List<IndexEntry>();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path, description, updated
            FROM memory_fts
            WHERE memory_fts MATCH $query
            ORDER BY rank
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", ToFtsQuery(query));
        command.Parameters.AddWithValue("$limit", safeLimit * 4);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken) && result.Count < safeLimit)
        {
            var rawPath = reader.GetString(0);
            if (!MemoryPath.TryParse(rawPath, out var path, out _) || !MemoryAccessPolicy.CanRead(context, path))
            {
                continue;
            }

            var updated = DateTimeOffset.TryParse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
            result.Add(new IndexEntry(rawPath, reader.GetString(1), updated));
        }

        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }

    private static async ValueTask UpsertAsync(SqliteConnection connection, MemoryPage page, CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM memory_fts WHERE path = $path";
        delete.Parameters.AddWithValue("$path", page.Path.Value);
        await delete.ExecuteNonQueryAsync(cancellationToken);

        if (page.Metadata.ValidTo is not null)
        {
            return;
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO memory_fts(path, title, description, body, scope, updated) VALUES ($path, $title, $description, $body, $scope, $updated)";
        insert.Parameters.AddWithValue("$path", page.Path.Value);
        insert.Parameters.AddWithValue("$title", Path.GetFileNameWithoutExtension(page.Path.Value));
        insert.Parameters.AddWithValue("$description", page.Metadata.Description);
        insert.Parameters.AddWithValue("$body", page.Body);
        insert.Parameters.AddWithValue("$scope", page.Path.Scope.ToString());
        insert.Parameters.AddWithValue("$updated", page.Metadata.Updated.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ToFtsQuery(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Replace("\"", " ", StringComparison.Ordinal).Trim())
            .Where(t => t.Length > 0)
            .Select(t => '"' + t + '"');
        var fts = string.Join(" OR ", tokens);
        return string.IsNullOrWhiteSpace(fts) ? "\"\"" : fts;
    }
}
