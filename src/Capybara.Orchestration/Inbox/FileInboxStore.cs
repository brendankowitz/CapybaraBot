using System.Globalization;
using System.Text.Json;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Orchestration.Inbox;

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

    public event EventHandler<AgentId>? MessageQuarantined;

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
        foreach (var file in Files(agent))
        {
            if (await TryReadAsync(agent, file, cancellationToken) is { } message)
            {
                return message;
            }
        }

        return null;
    }

    public async ValueTask<IReadOnlyList<FleetMessage>> ListAsync(AgentId agent, CancellationToken cancellationToken = default)
    {
        var messages = new List<FleetMessage>();
        foreach (var file in Files(agent).ToList())
        {
            if (await TryReadAsync(agent, file, cancellationToken) is { } message)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    public ValueTask AcknowledgeAsync(AgentId agent, string messageId, CancellationToken cancellationToken = default)
    {
        foreach (var file in FilesFor(agent, messageId))
        {
            File.Delete(file);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Moves the message to <c>{root}/{agent}/dead/</c>, outside the queue.</summary>
    public ValueTask DeadLetterAsync(AgentId agent, string messageId, CancellationToken cancellationToken = default)
    {
        var dead = Path.Combine(AgentDirectory(agent), "dead");
        Directory.CreateDirectory(dead);
        foreach (var file in FilesFor(agent, messageId))
        {
            File.Move(file, Path.Combine(dead, Path.GetFileName(file)), overwrite: true);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> CountAsync(AgentId agent, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Files(agent).Count());

    /// <summary>
    /// A message acknowledged between listing and reading is simply gone. One that cannot be deserialized is moved to
    /// <c>dead/</c> and reported, instead of failing every read of the inbox.
    /// </summary>
    private async ValueTask<FleetMessage?> TryReadAsync(AgentId agent, string file, CancellationToken cancellationToken)
    {
        try
        {
            FleetMessage? message;
            await using (var stream = File.OpenRead(file))
            {
                message = await JsonSerializer.DeserializeAsync<FleetMessage>(stream, JsonOptions, cancellationToken);
            }

            if (message is not null)
            {
                return message;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            // Quarantined below.
        }

        var dead = Path.Combine(AgentDirectory(agent), "dead");
        Directory.CreateDirectory(dead);
        File.Move(file, Path.Combine(dead, Path.GetFileName(file)), overwrite: true);
        MessageQuarantined?.Invoke(this, agent);
        return null;
    }

    private List<string> FilesFor(AgentId agent, string messageId) =>
        [.. Files(agent).Where(f => f.EndsWith($"-{messageId}.json", StringComparison.Ordinal))];

    private IEnumerable<string> Files(AgentId agent) =>
        Directory.EnumerateFiles(AgentDirectory(agent), "*.json").Order(StringComparer.Ordinal);

    private string AgentDirectory(AgentId agent)
    {
        var dir = Path.Combine(_root, agent.Value);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
