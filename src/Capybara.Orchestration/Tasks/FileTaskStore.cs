using System.Runtime.CompilerServices;
using System.Text.Json;
using Capybara.Contracts.Tasks;

namespace Capybara.Orchestration.Tasks;

/// <summary>One JSON file per task, written atomically.</summary>
public sealed class FileTaskStore : ITaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root;

    public FileTaskStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async ValueTask SaveAsync(FleetTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var file = PathFor(task.Id);
        var temp = file + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, task, JsonOptions, cancellationToken);
        }

        File.Move(temp, file, overwrite: true);
    }

    public async ValueTask<FleetTask?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var file = PathFor(taskId);
        if (!File.Exists(file))
        {
            return null;
        }

        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<FleetTask>(stream, JsonOptions, cancellationToken);
    }

    public async IAsyncEnumerable<FleetTask> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
        {
            await using var stream = File.OpenRead(file);
            if (await JsonSerializer.DeserializeAsync<FleetTask>(stream, JsonOptions, cancellationToken) is { } task)
            {
                yield return task;
            }
        }
    }

    private string PathFor(string taskId)
    {
        // Task ids are generated as 32 hex chars; reject anything else so ids can never address other files.
        if (taskId.Length != 32 || !taskId.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("Invalid task id.", nameof(taskId));
        }

        return Path.Combine(_root, taskId + ".json");
    }
}
