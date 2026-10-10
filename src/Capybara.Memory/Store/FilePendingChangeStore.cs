using System.Text.Json;
using System.Text.RegularExpressions;
using Capybara.Memory.Pages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Memory.Store;

/// <summary>Outcome of <see cref="FilePendingChangeStore.TryAdd"/>.</summary>
internal enum PendingAddResult
{
    Added,

    /// <summary>An equivalent change (same kind, path and content) is already waiting.</summary>
    Duplicate,

    /// <summary>The proposer already has the maximum number of changes waiting.</summary>
    QueueFull,
}

/// <summary>
/// Durable staging area for changes awaiting human approval (FR-MEM-05, FR-MEM-08). One JSON file per change under
/// <c>{root}/.pending/</c>, written atomically and flushed to disk before the rename, so staged changes survive restarts
/// and are shared by every store instance on the same root. Ids are validated before touching the file system, and a
/// corrupt file is renamed to <c>*.corrupt</c> and skipped instead of breaking every later listing.
/// Reading is the only way in: it deliberately has no public surface outside the assembly.
/// </summary>
internal sealed partial class FilePendingChangeStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly ILogger _logger;

    public FilePendingChangeStore(string root, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _directory = Path.Combine(Path.GetFullPath(root), ".pending");
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Stages <paramref name="change"/> unless an equivalent one is waiting or <paramref name="change"/>'s proposer is at
    /// <paramref name="maxPerProposer"/>. The id must be a 32-character lowercase hex string.
    /// </summary>
    public PendingAddResult TryAdd(PendingUserChange change, int maxPerProposer)
    {
        ArgumentNullException.ThrowIfNull(change);
        var file = FileFor(change.Id) ?? throw new ArgumentException($"Invalid change id '{change.Id}'.", nameof(change));
        lock (_gate)
        {
            var current = ListCore();
            if (current.Any(c => IsEquivalent(c, change)))
            {
                return PendingAddResult.Duplicate;
            }

            if (current.Count(c => string.Equals(c.ProposedBy, change.ProposedBy, StringComparison.Ordinal)) >= maxPerProposer)
            {
                return PendingAddResult.QueueFull;
            }

            Directory.CreateDirectory(_directory);
            var temp = file + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, change, Json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, file, overwrite: true);
            return PendingAddResult.Added;
        }
    }

    /// <summary>Returns the change without removing it, or <see langword="null"/> when the id is unknown, invalid or its file is corrupt.</summary>
    public PendingUserChange? Find(string id)
    {
        if (FileFor(id) is not { } file)
        {
            return null;
        }

        lock (_gate)
        {
            return File.Exists(file) ? Read(file) : null;
        }
    }

    /// <summary>Removes the change; call only once it has been applied or deliberately discarded.</summary>
    public void Remove(string id)
    {
        if (FileFor(id) is not { } file)
        {
            return;
        }

        lock (_gate)
        {
            File.Delete(file);
        }
    }

    /// <summary>All readable staged changes, oldest first.</summary>
    public IReadOnlyList<PendingUserChange> List()
    {
        lock (_gate)
        {
            return ListCore();
        }
    }

    private static bool IsEquivalent(PendingUserChange existing, PendingUserChange candidate) =>
        existing.Kind == candidate.Kind
        && string.Equals(existing.Path, candidate.Path, StringComparison.Ordinal)
        && (candidate.Kind == PendingChangeKind.Write
            ? string.Equals(FrontmatterSerializer.Parse(existing.After).Body, FrontmatterSerializer.Parse(candidate.After).Body, StringComparison.Ordinal)
            : string.Equals(existing.After, candidate.After, StringComparison.Ordinal));

    private List<PendingUserChange> ListCore()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var changes = new List<PendingUserChange>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (Read(file) is { } change)
            {
                changes.Add(change);
            }
        }

        return [.. changes.OrderBy(c => c.At).ThenBy(c => c.Id, StringComparer.Ordinal)];
    }

    private PendingUserChange? Read(string file)
    {
        try
        {
            var change = JsonSerializer.Deserialize<PendingUserChange>(File.ReadAllText(file), Json);
            if (change is not null)
            {
                return change;
            }
        }
        catch (JsonException ex)
        {
            LogCorrupt(file, ex);
            Quarantine(file);
            return null;
        }

        LogCorrupt(file, null);
        Quarantine(file);
        return null;
    }

    private void Quarantine(string file)
    {
        try
        {
            File.Move(file, Path.ChangeExtension(file, ".corrupt"), overwrite: true);
        }
        catch (IOException ex)
        {
            // Leave it in place; it will be skipped again next time.
            LogQuarantineFailed(file, ex);
        }
    }

    private string? FileFor(string id) =>
        IdPattern().IsMatch(id) ? Path.Combine(_directory, id + ".json") : null;

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [LoggerMessage(Level = LogLevel.Error, Message = "Pending memory change file {File} is corrupt; moving it aside as .corrupt.")]
    private partial void LogCorrupt(string file, Exception? ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not move corrupt pending change file {File} aside.")]
    private partial void LogQuarantineFailed(string file, Exception ex);
}
