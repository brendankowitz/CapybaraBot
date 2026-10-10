using System.Text.Json;

namespace Capybara.Orchestration.Coordination;

/// <summary>
/// Agent-to-agent exchange counts per pair under a root task (FR-A2A-11). With a file path, counts survive restarts, so a
/// restart cannot reset the ping-pong limit of a chain that has no task record (messages and consults under a
/// main-session root). Counts idle for longer than the retention are forgotten. Thread-safe.
/// </summary>
/// <remarks>
/// Persistence is best effort: if the file cannot be read or written, counting continues in memory (an unreadable file
/// starts empty rather than keeping capybarad from starting).
/// </remarks>
public sealed class ExchangeLedger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private readonly string? _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries;

    public ExchangeLedger(TimeProvider time, TimeSpan retention, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _retention = retention;
        _path = path is null ? null : Path.GetFullPath(path);
        _entries = Load(_path);
    }

    /// <summary>
    /// Counts one more exchange for <paramref name="key"/> and returns the new count. <paramref name="persisted"/> is a
    /// count kept elsewhere (a task record); the result is never below it.
    /// </summary>
    public int Count(string key, int persisted = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var expired in _entries.Where(e => now - e.Value.Touched > _retention).Select(e => e.Key).ToList())
            {
                _entries.Remove(expired);
            }

            var count = Math.Max(_entries.TryGetValue(key, out var current) ? current.Count : 0, persisted) + 1;
            _entries[key] = new Entry(count, now);
            Save();
            return count;
        }
    }

    private static Dictionary<string, Entry> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), Json) is { } loaded)
            {
                return new Dictionary<string, Entry>(loaded, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Best effort; see remarks.
        }

        return new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    /// <summary>Must be called under the lock.</summary>
    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; see remarks.
        }
    }

    private sealed record Entry(int Count, DateTimeOffset Touched);
}
