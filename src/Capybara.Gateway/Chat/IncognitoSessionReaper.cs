using System.Collections.Concurrent;
using System.Threading.Channels;
using Capybara.Contracts.Agents;
using Capybara.Orchestration.Inbox;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Inbox;

namespace Capybara.Gateway.Chat;

/// <summary><c>Capybara:Incognito</c>: how long an incognito conversation's CLI session may sit unused (FR-MEM-12).</summary>
public sealed class IncognitoOptions
{
    public const string SectionName = "Capybara:Incognito";

    /// <summary>An incognito session not touched for this long is deleted.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often idle sessions are looked for.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Deletes incognito conversations' CLI sessions, so an incognito transcript never outlives the conversation
/// (FR-MEM-12). Ending a session also drops the runtime's taint and allowance state for it.
/// </summary>
/// <remarks>
/// <para>Three ways a session ends:</para>
/// <list type="bullet">
/// <item>One-shot conversations (the server chose the id, so no client can continue it) end right after their turn.</item>
/// <item>A user ends a conversation (<c>DELETE /api/agents/{id}/incognito/{conversationId}</c>).</item>
/// <item>The sweep, every <see cref="IncognitoOptions.SweepInterval"/>, ends sessions idle for
/// <see cref="IncognitoOptions.IdleTimeout"/>. Sessions last active before this capybarad started are ended whatever their age:
/// conversations do not survive a restart. The CLI's own session list is the source of truth, so sessions left by a crash
/// are found too.</item>
/// </list>
/// <para>
/// A session with a message still in the inbox is never swept (its turn would recreate it). Ends are queued and run one at
/// a time off the caller's thread: deleting waits for a turn in flight on that session.
/// </para>
/// </remarks>
public sealed partial class IncognitoSessionReaper(
    AgentRuntimePool runtimes,
    IInboxStore inbox,
    InboxPumpHostedService pumps,
    IncognitoOptions options,
    TimeProvider time,
    ILogger<IncognitoSessionReaper> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, byte> _oneShot = new(StringComparer.Ordinal);
    private readonly Channel<(AgentId Agent, string Conversation)> _ends = Channel.CreateUnbounded<(AgentId, string)>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Sessions last active before this are leftovers from an earlier capybarad.</summary>
    private DateTimeOffset _startedAt;

    /// <summary>Ends the conversation's session once its next turn completes (call before the message is queued).</summary>
    public void EndAfterTurn(AgentId agent, string conversationId) => _oneShot.TryAdd(SessionIds.Incognito(agent, conversationId), 0);

    /// <summary>Forgets a pending <see cref="EndAfterTurn"/> (the message was never queued).</summary>
    public void Cancel(AgentId agent, string conversationId) => _oneShot.TryRemove(SessionIds.Incognito(agent, conversationId), out _);

    /// <summary>
    /// Ends the conversation as soon as a turn in flight on it finishes. A message for it that is still queued starts a
    /// new session, which the sweep then ends once idle.
    /// </summary>
    public void End(AgentId agent, string conversationId) => _ends.Writer.TryWrite((agent, conversationId));

    /// <summary>One sweep; only the sweep loop runs it.</summary>
    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in runtimes.All)
        {
            IReadOnlyList<IncognitoSessionInfo> sessions;
            try
            {
                sessions = await runtime.ListIncognitoSessionsAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Not running yet (or restarting): the next sweep looks again.
                continue;
            }

            if (sessions.Count == 0)
            {
                continue;
            }

            var queued = (await inbox.ListAsync(runtime.Agent, cancellationToken)).Select(m => m.SessionId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var cutoff = time.GetUtcNow() - options.IdleTimeout;
            foreach (var session in sessions.Where(s => (s.LastActive < _startedAt || s.LastActive <= cutoff) && !queued.Contains(SessionIds.Incognito(runtime.Agent, s.ConversationId))))
            {
                await EndAsync(runtime, session.ConversationId, cancellationToken);
            }
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _startedAt = time.GetUtcNow();
        pumps.TurnCompleted += OnTurnCompleted;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        pumps.TurnCompleted -= OnTurnCompleted;
        _ends.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(EndQueuedAsync(stoppingToken), SweepLoopAsync(stoppingToken));

    private void OnTurnCompleted(object? sender, InboxTurnCompletedEventArgs e)
    {
        if (e.Message.SessionId is { } session && _oneShot.TryRemove(session, out _) && SessionIds.TryParseIncognito(session, out var agent, out var conversation))
        {
            _ends.Writer.TryWrite((agent, conversation));
        }
    }

    private async Task EndQueuedAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (agent, conversation) in _ends.Reader.ReadAllAsync(stoppingToken))
            {
                if (runtimes.Find(agent) is { } runtime)
                {
                    await EndAsync(runtime, conversation, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // A failed sweep is retried at the next interval; it must not stop the service.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogSweepFailed(logger, ex);
                }

                await Task.Delay(options.SweepInterval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task EndAsync(IAgentRuntime runtime, string conversation, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.EndSessionAsync(TurnTarget.Incognito(conversation), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // One session that cannot be deleted now is retried by the next sweep.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEndFailed(logger, ex, runtime.Agent.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not end an incognito session of {Agent}; the next sweep retries")]
    private static partial void LogEndFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Error, Message = "The incognito session sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
