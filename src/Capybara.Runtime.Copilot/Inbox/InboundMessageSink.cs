using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capybara.Channels;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Storage;
using Capybara.Orchestration.Inbox;
using Capybara.Orchestration.Registry;
using Capybara.Runtime.Copilot.Configuration;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Inbox;

/// <summary>
/// Accepted conversational-channel text (FR-CLI-04) becomes a user DM in the front-door agent's durable inbox. Channel
/// delivery is at-least-once, so messages are deduplicated on their stable id, durably: a redelivery after the DM was
/// already processed, or after a restart, is dropped.
/// </summary>
/// <remarks>
/// <para>
/// The inbox message id is derived from the channel message id, and the sink records a delivery only after the enqueue
/// is durable. A crash between the two leaves the DM in the inbox without a record; the redelivery then finds it there
/// by id. (If the DM was also processed and acknowledged before the redelivery arrives, that single message can run
/// twice; a lost user message would be worse.) Records are kept for <see cref="CopilotRuntimeOptions.InboundDedupeRetention"/>.
/// </para>
/// <para>Thread-safe; deliveries are serialized.</para>
/// </remarks>
public sealed partial class InboundMessageSink : IInboundMessageSink, IDisposable
{
    public const string FileName = "inbound-dedupe.jsonl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IInboxStore _inbox;
    private readonly AgentRegistry _registry;
    private readonly CopilotRuntimeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<InboundMessageSink> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private int _lines;

    public InboundMessageSink(IInboxStore inbox, AgentRegistry registry, CopilotRuntimeOptions options, TimeProvider time, ILogger<InboundMessageSink> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inbox = inbox;
        _registry = registry;
        _options = options;
        _time = time;
        _logger = logger;
        _path = Path.Combine(options.StateRoot, "channels", FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var entries = JsonlFile.Replay<Delivery>(_path, Json);
        _lines = entries.Count;
        foreach (var entry in entries)
        {
            _seen[entry.Key] = entry.At;
        }
    }

    /// <exception cref="InvalidOperationException">The front-door agent is not configured; the adapter redelivers later.</exception>
    public async ValueTask ReceiveAsync(string channel, string userId, string text, string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var frontDoor = FrontDoor();
        var key = KeyFor(channel, messageId);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Prune();
            if (_seen.ContainsKey(key))
            {
                LogDuplicate(_logger, channel);
                return;
            }

            var id = InboxIdFor(key);
            if ((await _inbox.ListAsync(frontDoor, cancellationToken)).Any(m => m.Id == id))
            {
                // Enqueued before a crash that lost the record.
                await RememberAsync(key);
                return;
            }

            await _inbox.EnqueueAsync(
                new FleetMessage
                {
                    Id = id,
                    To = frontDoor,
                    Origin = MessageOrigin.User,
                    Body = text,
                    Channel = channel,
                    Taint = Taint.Clean,
                    CreatedAt = _time.GetUtcNow(),
                },
                cancellationToken);
            await RememberAsync(key);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    /// <summary>The inbox message id for a channel delivery: stable, so a redelivery maps to the same message.</summary>
    internal static string InboxIdFor(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

    internal static string KeyFor(string channel, string messageId) => $"{channel}/{messageId}";

    private AgentId FrontDoor()
    {
        if (AgentId.TryParse(_options.FrontDoorAgent, out var id) && _registry.Find(id) is not null)
        {
            return id;
        }

        throw new InvalidOperationException($"The front-door agent '{_options.FrontDoorAgent}' is not configured.");
    }

    private async ValueTask RememberAsync(string key)
    {
        var at = _time.GetUtcNow();
        await JsonlFile.AppendAsync(_path, new Delivery(key, at), Json, CancellationToken.None);
        _seen[key] = at;
        _lines++;

        // Compact once expired records dominate the file.
        if (_lines > 1_000 && _lines > 2 * _seen.Count)
        {
            JsonlFile.Rewrite(_path, _seen.Select(kv => new Delivery(kv.Key, kv.Value)), Json);
            _lines = _seen.Count;
        }
    }

    private void Prune()
    {
        var cutoff = _time.GetUtcNow() - _options.InboundDedupeRetention;
        foreach (var expired in _seen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
        {
            _seen.Remove(expired);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped a redelivered {Channel} message")]
    private static partial void LogDuplicate(ILogger logger, string channel);

    private sealed record Delivery(string Key, DateTimeOffset At);
}
