using System.Text.Json;
using Capybara.Contracts.Agents;

namespace Capybara.Gateway.Wiring;

/// <summary>
/// Makes a human's per-agent pause survive a restart (FR-SAF-06): <see cref="Policy.Safety.KillSwitch"/> keeps pauses
/// in memory, but an agent the user paused from a client must not come back running after a reboot. Composition
/// re-applies the journal before any runtime starts; every later pause or resume rewrites it.
/// </summary>
/// <remarks>
/// <para>
/// Only human pauses are journaled. A failed self-test pause is re-derived on every start (the test runs again), and an
/// agent removed by a lifecycle operation is dropped from the journal with the rest of its state.
/// </para>
/// <para>
/// Fails closed like <see cref="KillSwitchJournal"/>: a journal that cannot be read or parsed reads as "every agent
/// given as the fallback is paused", <see cref="LoadFailure"/> says why, and the next change rewrites the file from that
/// set. Thread-safe.
/// </para>
/// </remarks>
public sealed class PausedAgentsJournal(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private readonly HashSet<AgentId> _paused = [];

    /// <summary>Why <see cref="Load"/> could not read the journal, or <see langword="null"/> when it could.</summary>
    public string? LoadFailure { get; private set; }

    /// <summary>
    /// The agents a human paused before the restart; <paramref name="fallback"/> (the whole fleet) when the journal
    /// exists but cannot be read.
    /// </summary>
    public IReadOnlySet<AgentId> Load(IEnumerable<AgentId> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        lock (_gate)
        {
            _paused.Clear();
            LoadFailure = null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var ids = JsonSerializer.Deserialize<string[]>(stream, Json) ?? throw new JsonException("The journal holds null.");
                _paused.UnionWith(ids.Select(AgentId.Parse));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Never written: nobody paused anything.
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                LoadFailure = ex.GetType().Name;
                _paused.UnionWith(fallback);
            }

            return _paused.ToHashSet();
        }
    }

    /// <exception cref="IOException">The journal could not be written; nothing changed.</exception>
    public void Add(AgentId agent) => Update(paused => paused.Add(agent));

    /// <exception cref="IOException">The journal could not be written; nothing changed.</exception>
    public void Remove(AgentId agent) => Update(paused => paused.Remove(agent));

    private void Update(Func<HashSet<AgentId>, bool> change)
    {
        lock (_gate)
        {
            var next = _paused.ToHashSet();
            if (!change(next) && LoadFailure is null)
            {
                return;
            }

            Write(next);
            _paused.Clear();
            _paused.UnionWith(next);
            LoadFailure = null;
        }
    }

    private void Write(HashSet<AgentId> paused)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, paused.Select(a => a.Value).Order(StringComparer.Ordinal).ToArray(), Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
