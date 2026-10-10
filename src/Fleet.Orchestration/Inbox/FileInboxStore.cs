using System.Globalization;
using System.Text.Json;
using Fleet.Contracts.Agents;
using Fleet.Contracts.Messaging;

namespace Fleet.Orchestration.Inbox;

/// <summary>File-per-message durable inbox: <c>{root}/{agent}/{sequence}-{id}.json</c>, written atomically.</summary>
public sealed class FileInboxStore : IInboxStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private long _sequence;

    public FileInboxStore(string root, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _sequence = time.GetUtcNow().UtcTicks;
    }

    public event EventHandler<AgentId>? MessageAvailable;

    public async ValueTask EnqueueAsync(FleetMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var dir = AgentDirectory(message.To);
        var sequence = Interlocked.Increment(ref _sequence);
        var file = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"{sequence:D20}-{message.Id}.json"));
        var temp = file + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, message, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temp, file);
        MessageAvailable?.Invoke(this, message.To);
    }

    public async ValueTask<FleetMessage?> PeekAsync(AgentId agent, CancellationToken cancellationToken = default)
    {
        var file = Files(agent).FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<FleetMessage>(stream, JsonOptions, cancellationToken);
    }

    public ValueTask AcknowledgeAsync(AgentId agent, string messageId, CancellationToken cancellationToken = default)
    {
        foreach (var file in Files(agent).Where(f => f.EndsWith($"-{messageId}.json", StringComparison.Ordinal)))
        {
            File.Delete(file);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> CountAsync(AgentId agent, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Files(agent).Count());

    private IEnumerable<string> Files(AgentId agent) =>
        Directory.EnumerateFiles(AgentDirectory(agent), "*.json").Order(StringComparer.Ordinal);

    private string AgentDirectory(AgentId agent)
    {
        var dir = Path.Combine(_root, agent.Value);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
