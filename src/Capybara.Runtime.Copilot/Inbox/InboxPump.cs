using System.Threading.Channels;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Tasks;
using Capybara.Observability.Alerts;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Inbox;
using Capybara.Orchestration.Tasks;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Inbox;

/// <summary>
/// The single consumer of one agent's durable inbox (FR-AGT-05). Messages run one turn at a time, oldest first, and a
/// message is acknowledged only after its turn completed — the runtime has persisted it — so a crash loses nothing
/// (NFR-02). A user DM pre-empts background work: it is taken before any queued background item, and while a
/// main-session turn is in flight — the pump's own or a routine heartbeat — it is interjected into that turn in
/// <c>immediate</c> mode and acknowledged once that turn has completed. Dry-run turns are never joined.
/// </summary>
/// <remarks>
/// <para>
/// Routing: a message addressed to one of the agent's task sessions runs there (a delegated task's assignment, answers
/// and approval outcomes, or a sub-task's question and result for the session that delegated it); an isolated routine
/// run resumes for its approval outcome; everything else runs in the main session. A message about a task that has
/// already finished is acknowledged without a turn. Finished task sessions are deleted.
/// </para>
/// <para>
/// Failures: a paused or over-budget agent keeps its messages and is polled again; a failed turn is retried with
/// exponential back-off, and after <see cref="InboxPumpOptions.MaxAttempts"/> the message is dead-lettered (kept on
/// disk, out of the queue) and the user alerted, so one poisoned message cannot block the inbox forever.
/// </para>
/// </remarks>
public sealed partial class InboxPump
{
    public const string DeadLetterAlertKind = "inbox.dead_letter";

    private readonly IAgentRuntime _runtime;
    private readonly IInboxStore _inbox;
    private readonly A2ACoordinator _coordinator;
    private readonly IAlertSink _alerts;
    private readonly InboxPumpOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _notBefore = new(StringComparer.Ordinal);
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);
    private readonly List<FleetMessage> _steered = [];
    private readonly List<Task> _steering = [];
    private string? _mainTurnMessageId;

    public InboxPump(
        IAgentRuntime runtime,
        IInboxStore inbox,
        A2ACoordinator coordinator,
        IAlertSink alerts,
        InboxPumpOptions options,
        TimeProvider time,
        ILogger<InboxPump> logger)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _inbox = inbox;
        _coordinator = coordinator;
        _alerts = alerts;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public AgentId Agent => _runtime.Agent;

    /// <summary>Raised after a message's turn completed and the message was acknowledged.</summary>
    public event EventHandler<InboxTurnCompletedEventArgs>? TurnCompleted;

    /// <summary>Consumes the inbox until <paramref name="stoppingToken"/> is cancelled. Call once.</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _inbox.MessageAvailable += OnMessageAvailable;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var (next, wakeAt) = await NextAsync(stoppingToken);
                if (next is null)
                {
                    await WaitAsync(wakeAt, stoppingToken);
                    continue;
                }

                await ProcessAsync(next, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _inbox.MessageAvailable -= OnMessageAvailable;
        }
    }

    private static bool IsUserDm(FleetMessage message) => message.Origin == MessageOrigin.User;

    private static bool IsIncognito(FleetMessage message) => SessionIds.TryParseIncognito(message.SessionId, out _, out _);

    /// <summary>The oldest ready user DM, else the oldest ready background message, else when to look again.</summary>
    private async Task<(FleetMessage? Next, DateTimeOffset? WakeAt)> NextAsync(CancellationToken cancellationToken)
    {
        var pending = await _inbox.ListAsync(Agent, cancellationToken);
        var now = _time.GetUtcNow();
        DateTimeOffset? wakeAt = null;
        lock (_gate)
        {
            bool Ready(FleetMessage m)
            {
                if (!_notBefore.TryGetValue(m.Id, out var at) || at <= now)
                {
                    return true;
                }

                wakeAt = wakeAt is { } w && w < at ? w : at;
                return false;
            }

            if (pending.Where(IsUserDm).FirstOrDefault(Ready) is { } dm)
            {
                return (dm, null);
            }

            // Background work is strictly FIFO: a backing-off head blocks the messages behind it.
            if (pending.FirstOrDefault(m => !IsUserDm(m)) is { } head && Ready(head))
            {
                return (head, null);
            }
        }

        return (null, wakeAt);
    }

    private async Task WaitAsync(DateTimeOffset? wakeAt, CancellationToken stoppingToken)
    {
        using var timer = wakeAt is { } at ? new CancellationTokenSource(Max(at - _time.GetUtcNow(), TimeSpan.Zero), _time) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timer.Token);
        try
        {
            await _wake.Reader.WaitToReadAsync(linked.Token);
            _wake.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The back-off elapsed.
        }
    }

    private async Task ProcessAsync(FleetMessage message, CancellationToken stoppingToken)
    {
        var plan = await PlanAsync(message, stoppingToken);
        if (plan is null)
        {
            LogSkipped(_logger, message.Id, Agent.Value);
            await _inbox.AcknowledgeAsync(Agent, message.Id, CancellationToken.None);
            Forget(message.Id);
            return;
        }

        TurnResult result;
        IReadOnlyList<FleetMessage> steered = [];
        if (IsUserDm(message) && plan.Request.Target.Kind == SessionKind.Main
            && await _runtime.SteerAsync(message.Body, MessageOrigin.User, stoppingToken) is { } joined)
        {
            // A main turn that is not ours (a routine heartbeat) was in flight: the DM joined it and shares its outcome.
            result = joined;
        }
        else
        {
            BeginTurn(message, plan.Request.Target);
            try
            {
                result = await _runtime.RunTurnAsync(plan.Request, stoppingToken);
            }
            finally
            {
                steered = await EndTurnAsync();
            }
        }

        switch (result.Status)
        {
            case TurnStatus.Completed or TurnStatus.LimitExceeded:
                // A turn stopped at its own credit limit was handled as far as its budget allowed; rerunning it would
                // only overspend again.
                // The turn is persisted: only now may the messages leave the inbox (NFR-02).
                await _inbox.AcknowledgeAsync(Agent, message.Id, CancellationToken.None);
                foreach (var dm in steered)
                {
                    await _inbox.AcknowledgeAsync(Agent, dm.Id, CancellationToken.None);
                }

                Forget(message.Id);
                await AfterTurnAsync(plan, result);
                Raise(new InboxTurnCompletedEventArgs(Agent, message, result, steered));
                break;

            case TurnStatus.Blocked or TurnStatus.OverBudget or TurnStatus.Throttled:
                LogBlocked(_logger, Agent.Value, result.Error);
                Defer(message.Id, _options.BlockedPollInterval);
                break;

            default:
                await RetryOrDeadLetterAsync(message, result.Error);
                break;
        }
    }

    private async Task<TurnPlan?> PlanAsync(FleetMessage message, CancellationToken cancellationToken)
    {
        var target = TurnTarget.Main;
        var assignment = false;
        if (SessionIds.TryParseIncognito(message.SessionId, out var owner, out var conversation) && owner == Agent)
        {
            target = TurnTarget.Incognito(conversation);
        }
        else if (SessionIds.TryParseTask(message.SessionId, out owner, out var key) && owner == Agent)
        {
            if (!FileTaskStore.IsTaskId(key))
            {
                // An isolated routine run, resumed for its approval outcome.
                target = TurnTarget.Isolated(key);
            }
            else if (await _coordinator.GetOwnedTaskAsync(Agent, key, cancellationToken) is { } task)
            {
                if (task.State.IsTerminal())
                {
                    // About this finished task itself: nothing left to do. About a sub-task: keep it, in main.
                    if (message.TaskId == task.Id)
                    {
                        return null;
                    }
                }
                else
                {
                    target = TurnTarget.ForTask(task);
                    assignment = message.TaskId == task.Id && message.From == task.RequestedBy && task.State == FleetTaskState.Submitted;
                }
            }
        }

        var prompt = message.Origin switch
        {
            MessageOrigin.User => message.Body,
            MessageOrigin.Agent when assignment => MessagePrompts.ForTaskAssignment(target.Task!),
            MessageOrigin.Agent => MessagePrompts.ForAgentMessage(message),
            _ => MessagePrompts.ForSystemMessage(message),
        };

        return new TurnPlan(
            new AgentTurnRequest
            {
                Prompt = prompt,
                Origin = message.Origin,
                Principal = message.Envelope?.OriginPrincipal ?? (message.Origin == MessageOrigin.User ? "user" : "system"),
                Target = target,
                From = message.From,
                Taint = message.Taint,
                RootTaskId = target.Kind == SessionKind.Main ? message.Envelope?.RootTaskId : null,
            },
            assignment);
    }

    /// <summary>Marks a started task as working and deletes sessions whose work has ended.</summary>
    private async Task AfterTurnAsync(TurnPlan plan, TurnResult result)
    {
        var target = plan.Request.Target;
        try
        {
            if (target.Task is { } task)
            {
                if (plan.Assignment)
                {
                    await _coordinator.MarkWorkingAsync(Agent, task.Id, CancellationToken.None);
                }

                if (await _coordinator.GetOwnedTaskAsync(Agent, task.Id, CancellationToken.None) is not { } current || current.State.IsTerminal())
                {
                    await _runtime.EndSessionAsync(target, CancellationToken.None);
                }
            }
            else if (target.Kind == SessionKind.Task && result.Evidence.PendingApprovals.Count == 0)
            {
                await _runtime.EndSessionAsync(target, CancellationToken.None);
            }
        }
#pragma warning disable CA1031 // The message is already acknowledged; housekeeping must not crash the pump.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHousekeepingFailed(_logger, ex, Agent.Value);
        }
    }

    private async Task RetryOrDeadLetterAsync(FleetMessage message, string? error)
    {
        int attempts;
        lock (_gate)
        {
            attempts = _attempts[message.Id] = _attempts.GetValueOrDefault(message.Id) + 1;
        }

        if (attempts < _options.MaxAttempts)
        {
            var delay = Min(_options.InitialRetryDelay * Math.Pow(2, attempts - 1), _options.MaxRetryDelay);
            LogRetry(_logger, message.Id, Agent.Value, attempts, delay, error);
            Defer(message.Id, delay);
            return;
        }

        LogDeadLetter(_logger, message.Id, Agent.Value, attempts, error);
        await _inbox.DeadLetterAsync(Agent, message.Id, CancellationToken.None);
        Forget(message.Id);

        // Content-free: the alert names the agent and the count, never the message.
        await _alerts.PublishAsync(
            new Alert(DeadLetterAlertKind, AlertSeverity.Warning, $"A message for {Agent} failed {attempts} times and was moved to its dead-letter folder.", _time.GetUtcNow(), Agent.Value),
            CancellationToken.None);
    }

    private void Defer(string messageId, TimeSpan delay)
    {
        lock (_gate)
        {
            _notBefore[messageId] = _time.GetUtcNow() + delay;
        }
    }

    private void Forget(string messageId)
    {
        lock (_gate)
        {
            _attempts.Remove(messageId);
            _notBefore.Remove(messageId);
        }
    }

    private void BeginTurn(FleetMessage message, TurnTarget target)
    {
        lock (_gate)
        {
            _mainTurnMessageId = target.Kind == SessionKind.Main ? message.Id : null;
            _claimed.Clear();
            _steered.Clear();
            _claimed.Add(message.Id);
        }
    }

    /// <summary>Stops new steering, waits for steering in progress, and returns the DMs that made it into the turn.</summary>
    private async Task<IReadOnlyList<FleetMessage>> EndTurnAsync()
    {
        Task[] steering;
        lock (_gate)
        {
            _mainTurnMessageId = null;
            steering = [.. _steering];
            _steering.Clear();
        }

        await Task.WhenAll(steering);
        lock (_gate)
        {
            List<FleetMessage> steered = [.. _steered];
            _steered.Clear();
            _claimed.Clear();
            return steered;
        }
    }

    private void OnMessageAvailable(object? sender, AgentId agent)
    {
        if (agent != Agent)
        {
            return;
        }

        _wake.Writer.TryWrite(true);
        lock (_gate)
        {
            if (_mainTurnMessageId is { } inFlight)
            {
                // Started on the pool, not inline: this runs on the enqueuer's thread, under the lock.
                _steering.Add(Task.Run(() => SteerPendingDmsAsync(inFlight)));
            }
        }
    }

    /// <summary>FR-AGT-05: user DMs that arrive during a main-session turn are interjected into it.</summary>
    private async Task SteerPendingDmsAsync(string inFlight)
    {
        try
        {
            var claimed = new List<FleetMessage>();
            foreach (var dm in (await _inbox.ListAsync(Agent, CancellationToken.None)).Where(m => IsUserDm(m) && !IsIncognito(m)))
            {
                lock (_gate)
                {
                    if (_mainTurnMessageId == inFlight && _claimed.Add(dm.Id))
                    {
                        claimed.Add(dm);
                    }
                }
            }

            // Sent in arrival order; each call then waits for the turn they all joined.
            var outcomes = await Task.WhenAll(claimed.Select(dm => _runtime.SteerAsync(dm.Body, MessageOrigin.User, CancellationToken.None)));
            lock (_gate)
            {
                for (var i = 0; i < claimed.Count; i++)
                {
                    if (outcomes[i] is not null)
                    {
                        _steered.Add(claimed[i]);
                    }
                    else
                    {
                        _claimed.Remove(claimed[i].Id);
                    }
                }
            }
        }
#pragma warning disable CA1031 // A DM that could not be steered simply runs as its own turn next.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSteerFailed(_logger, ex, Agent.Value);
        }
    }

    private void Raise(InboxTurnCompletedEventArgs args)
    {
        if (TurnCompleted is not { } handler)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<InboxTurnCompletedEventArgs>>())
        {
            try
            {
                subscriber(this, args);
            }
#pragma warning disable CA1031 // A failing subscriber must not stop the pump or the other subscribers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSubscriberFailed(_logger, ex, Agent.Value);
            }
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private sealed record TurnPlan(AgentTurnRequest Request, bool Assignment);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} for {Agent} concerns a finished task; acknowledged without a turn")]
    private static partial void LogSkipped(ILogger logger, string messageId, string agent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbox of {Agent} is on hold: {Reason}")]
    private static partial void LogBlocked(ILogger logger, string agent, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Turn for message {MessageId} of {Agent} failed (attempt {Attempt}); retrying in {Delay}: {Error}")]
    private static partial void LogRetry(ILogger logger, string messageId, string agent, int attempt, TimeSpan delay, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} of {Agent} failed {Attempts} times; dead-lettered: {Error}")]
    private static partial void LogDeadLetter(ILogger logger, string messageId, string agent, int attempts, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not steer a DM into the turn in flight for {Agent}; it runs next")]
    private static partial void LogSteerFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Post-turn housekeeping for {Agent} failed")]
    private static partial void LogHousekeepingFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Error, Message = "A TurnCompleted subscriber failed for {Agent}")]
    private static partial void LogSubscriberFailed(ILogger logger, Exception exception, string agent);
}
