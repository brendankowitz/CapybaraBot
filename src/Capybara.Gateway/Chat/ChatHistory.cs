using System.Collections.Concurrent;
using System.Text.Json;
using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Storage;

namespace Capybara.Gateway.Chat;

/// <summary>
/// The per-agent chat the PWA and TUI show (FR-CLI-02): the user's direct messages and the agent's replies, correlated
/// by inbox message id (a user line carries its id, every reply line the id it answers).
/// </summary>
/// <remarks>
/// <para>
/// Lines are persisted to <c>{state}/chat/{agent}.jsonl</c>, outside every agent's sandbox, so no agent can write a line
/// that the clients would show as the user's. Incognito lines are kept in memory only and are gone after a restart
/// (FR-MEM-12). The newest <see cref="RecentLines"/> lines per agent are held in memory for reads.
/// </para>
/// <para>Thread-safe: every agent's log has its own lock, and a reply's lines are added under one acquisition, so a
/// reader sees all of them or none.</para>
/// </remarks>
public sealed class ChatHistory(string directory)
{
    public const int RecentLines = 500;

    public const string UserRole = "user";
    public const string AssistantRole = "assistant";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, AgentLog> _logs = new(StringComparer.Ordinal);

    /// <summary>Records a user's message once; a second call with the same id (a redelivery or the reply path) is a no-op.</summary>
    public void RecordUser(AgentId agent, string messageId, string content, bool incognito, DateTimeOffset at)
    {
        var log = Load(agent);
        lock (log.Gate)
        {
            AddUserLocked(agent, log, messageId, content, incognito, at);
        }
    }

    /// <summary>
    /// Records the reply to <paramref name="answered"/> and to every message <paramref name="joined"/> into the same turn,
    /// so each sender finds a reply under its own id. Messages that reached the inbox some other way (a phone channel)
    /// get their user line first.
    /// </summary>
    public void RecordReply(AgentId agent, FleetMessage answered, IReadOnlyList<FleetMessage> joined, string content, bool incognito, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(answered);
        ArgumentNullException.ThrowIfNull(joined);
        var messages = joined.Prepend(answered).ToList();
        var log = Load(agent);
        lock (log.Gate)
        {
            foreach (var message in messages)
            {
                AddUserLocked(agent, log, message.Id, message.Body, incognito, message.CreatedAt);
            }

            foreach (var message in messages)
            {
                AddLocked(agent, log, new ChatLine(at, AssistantRole, content, null, message.Id, incognito));
            }
        }
    }

    /// <summary>The newest <paramref name="limit"/> lines, oldest first.</summary>
    public IReadOnlyList<ChatEntryDto> Read(AgentId agent, int limit)
    {
        var log = Load(agent);
        lock (log.Gate)
        {
            return [.. log.Recent.Skip(Math.Max(0, log.Recent.Count - limit)).Select(l => new ChatEntryDto(l.At, l.Role, l.Content, l.Incognito, l.MessageId, l.InReplyTo))];
        }
    }

    /// <summary>Forgets the agent's chat, on disk and in memory (a deleted agent, FR-AGT-07).</summary>
    public void Delete(AgentId agent)
    {
        var log = Load(agent);
        lock (log.Gate)
        {
            File.Delete(PathOf(agent));
            log.Recent.Clear();
            log.UserIds.Clear();
        }
    }

    private void AddUserLocked(AgentId agent, AgentLog log, string messageId, string content, bool incognito, DateTimeOffset at)
    {
        if (log.UserIds.Add(messageId))
        {
            AddLocked(agent, log, new ChatLine(at, UserRole, content, messageId, null, incognito));
        }
    }

    private void AddLocked(AgentId agent, AgentLog log, ChatLine line)
    {
        if (!line.Incognito)
        {
            // Durable before it is visible: a line a client has seen survives a restart.
            JsonlFile.Append(PathOf(agent), line, Json);
        }

        log.Recent.Add(line);
        if (log.Recent.Count > RecentLines)
        {
            log.Recent.RemoveRange(0, log.Recent.Count - RecentLines);
        }
    }

    private AgentLog Load(AgentId agent) => _logs.GetOrAdd(agent.Value, _ =>
    {
        Directory.CreateDirectory(directory);
        var lines = JsonlFile.Replay<ChatLine>(PathOf(agent), Json);
        var log = new AgentLog();
        log.UserIds.UnionWith(lines.Where(l => l.MessageId is not null).Select(l => l.MessageId!));
        log.Recent.AddRange(lines.Skip(Math.Max(0, lines.Count - RecentLines)));
        return log;
    });

    private string PathOf(AgentId agent) => Path.Combine(directory, $"{agent.Value}.jsonl");

    private sealed record ChatLine(DateTimeOffset At, string Role, string Content, string? MessageId, string? InReplyTo, bool Incognito = false);

    private sealed class AgentLog
    {
        public Lock Gate { get; } = new();

        public List<ChatLine> Recent { get; } = [];

        public HashSet<string> UserIds { get; } = new(StringComparer.Ordinal);
    }
}
