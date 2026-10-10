using System.Text.Json;
using System.Text.RegularExpressions;

namespace Capybara.Memory.Store;

/// <summary>
/// Durable staging area for changes awaiting human approval (FR-MEM-05, FR-MEM-08). One JSON file per change under
/// <c>{root}/.pending/</c>, written atomically, so staged changes survive restarts and are shared by every store
/// instance on the same root. Ids are validated before touching the file system.
/// </summary>
public sealed partial class FilePendingChangeStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string _directory;

    public FilePendingChangeStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _directory = Path.Combine(Path.GetFullPath(root), ".pending");
    }

    /// <summary>Stages <paramref name="change"/>. The id must be a 32-character lowercase hex string.</summary>
    public void Add(PendingUserChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var file = FileFor(change.Id) ?? throw new ArgumentException($"Invalid change id '{change.Id}'.", nameof(change));
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(change, Json));
            File.Move(temp, file, overwrite: true);
        }
    }

    /// <summary>Removes and returns the change, or <see langword="false"/> when the id is unknown or invalid.</summary>
    public bool TryRemove(string id, out PendingUserChange? change)
    {
        change = null;
        if (FileFor(id) is not { } file)
        {
            return false;
        }

        lock (_gate)
        {
            if (!File.Exists(file))
            {
                return false;
            }

            change = JsonSerializer.Deserialize<PendingUserChange>(File.ReadAllText(file), Json);
            File.Delete(file);
            return change is not null;
        }
    }

    /// <summary>All staged changes, oldest first.</summary>
    public IReadOnlyList<PendingUserChange> List()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            var changes = new List<PendingUserChange>();
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (JsonSerializer.Deserialize<PendingUserChange>(File.ReadAllText(file), Json) is { } change)
                {
                    changes.Add(change);
                }
            }

            return [.. changes.OrderBy(c => c.At).ThenBy(c => c.Id, StringComparer.Ordinal)];
        }
    }

    private string? FileFor(string id) =>
        IdPattern().IsMatch(id) ? Path.Combine(_directory, id + ".json") : null;

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
