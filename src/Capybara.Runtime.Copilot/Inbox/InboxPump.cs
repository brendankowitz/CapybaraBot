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
/// <c>immediate</c> mode. A DM that joined background work then gets its own reply turn, and is acknowledged only once
/// that reply exists. Dry-run turns are never joined.
/// </summary>
/// <remarks>
/// <para>
/// Routing: a message addressed to one of the agent's task sessions runs there (a delegated task's assignment, answers
/// and approval outcomes, or a sub-task's question and result for the session that delegated it); an isolated routine
/// run resumes for its approval outcome, as its routine; everything else runs in the main session. A message about a
/// task that has finished, expired or run out of budget is acknowledged without a turn.
/// </para>
/// <para>
/// Failures never stop the pump: a paused, over-budget or throttled agent keeps its messages and is polled again; a
/// failed turn, or a message the pump cannot even plan, is retried with exponential back-off and after
/// <see cref="InboxPumpOptions.MaxAttempts"/> dead-lettered (kept on disk, out of the queue) with a content-free alert;
/// an acknowledgement that fails is retried without re-running the turn; an unreadable message file is quarantined by
/// the store and alerted. Anything else is logged and the loop continues.
/// </para>
/// </remarks>
public sealed partial class InboxPump
{
    public const string DeadLetterAlertKind = "inbox.dead_letter";

    public const string QuarantineAlertKind = "inbox.quarantined";

    /// <summary>Alert kind raised when a task turn's spend could not be debited from the task's budget.</summary>
    public const string SpendUnrecordedAlertKind = "inbox.spend_unrecorded";

    private readonly IAgentRuntime _runtime;
    private readonly IInboxStore _inbox;
    private readonly A2ACoordinator _coordinator;
    private readonly IAlertSink _alerts;
    private readonly InboxPumpOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CopilotRoutineExecutor? _routines;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _notBefore = new(StringComparer.Ordinal);

    /// <summary>Credits failed attempts at a message already spent from its carved budget (FR-A2A-05).</summary>
    private readonly Dictionary<string, decimal> _spent = new(StringComparer.Ordinal);

    /// <summary>Handled messages whose acknowledgement failed: never run again, acknowledged on a later pass.</summary>
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);

    /// <summary>Messages that could not even be dead-lettered: skipped for the life of this process.</summary>
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
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
        ILogger<InboxPump> logger,
        CopilotRoutineExecutor? routines = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _inbox = inbox;
        _coordinator = coordinator;
        _alerts = alerts;
        _options = options;
        _time = time;
        _logger = logger;
        _routines = routines;
    }

    public AgentId Agent => _runtime.Agent;

    /// <summary>
    /// Raised after a message's turn completed and the message was acknowledged. For user DMs that joined background
    /// work, raised once more with their reply turn.
    /// </summary>
    public event EventHandler<InboxTurnCompletedEventArgs>? TurnCompleted;

    /// <summary>
    /// Consumes the inbox until <paramref name="stoppingToken"/> is cancelled. May be called again after it ended
    /// (the hosted service restarts a pump that faulted); in-memory retry state carries over.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _inbox.MessageAvailable += OnMessageAvailable;
        _inbox.MessageQuarantined += OnMessageQuarantined;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                FleetMessage? current = null;
                try
                {
                    await AcknowledgeHandledAsync(stoppingToken);
                    var (next, wakeAt) = await NextAsync(stoppingToken);
                    if (next is null)
                    {
                        await WaitAsync(wakeAt, stoppingToken);
                        continue;
                    }

                    current = next;
                    await ProcessAsync(next, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031 // One bad message or a passing I/O fault must never stop the agent's inbox (NFR-02).
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogIterationFailed(_logger, ex, Agent.Value);
                    if (current is not null)
                    {
                        await FailAsync(current, $"{ex.GetType().Name}: {ex.Message}");
                    }
                    else
                    {
                        await PauseAsync(_options.BlockedPollInterval, stoppingToken);
                    }
                }
            }
        }
        finally
        {
            _inbox.MessageAvailable -= OnMessageAvailable;
            _inbox.MessageQuarantined -= OnMessageQuarantined;
        }
    }

    private static bool IsUserDm(FleetMessage message) => message.Origin == MessageOrigin.User;

    private static bool IsIncognito(FleetMessage message) => SessionIds.TryParseIncognito(message.SessionId, out _, out _);

    private static bool Handled(TurnResult result) => result.Status is TurnStatus.Completed or TurnStatus.LimitExceeded;

    /// <summary>The oldest ready user DM, else the oldest ready background message, else when to look again.</summary>
    private async Task<(FleetMessage? Next, DateTimeOffset? WakeAt)> NextAsync(CancellationToken cancellationToken)
    {
        var pending = await _inbox.ListAsync(Agent, cancellationToken);
        var now = _time.GetUtcNow();
        DateTimeOffset? wakeAt = null;
        lock (_gate)
        {
            var open = pending.Where(m => !_handled.Contains(m.Id) && !_skipped.Contains(m.Id)).ToList();
            bool Ready(FleetMessage m)
            {
                if (!_notBefore.TryGetValue(m.Id, out var at) || at <= now)
                {
                    return true;
                }

                wakeAt = wakeAt is { } w && w < at ? w : at;
                return false;
            }

            if (open.Where(IsUserDm).FirstOrDefault(Ready) is { } dm)
            {
                return (dm, null);
            }

            // Background work is strictly FIFO: a backing-off head blocks the messages behind it.
            if (open.FirstOrDefault(m => !IsUserDm(m)) is { } head && Ready(head))
            {
                return (head, null);
            }

            if (_handled.Count > 0)
            {
                // Acknowledgements are still owed: come back for them.
                var retry = now + _options.InitialRetryDelay;
                wakeAt = wakeAt is { } w && w < retry ? w : retry;
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
            await AcknowledgeAsync([message.Id]);
            Forget(message.Id);
            return;
        }

        if (IsUserDm(message) && plan.Request.Target.Kind == SessionKind.Main
            && await TrySteerAsync(message.Body, stoppingToken) is { } joined && Handled(joined))
        {
            // A main turn the pump did not start (a routine heartbeat) was in flight and the DM joined it.
            await AnswerJoinedDmsAsync([message], stoppingToken);
            return;
        }

        TurnResult result;
        IReadOnlyList<FleetMessage> steered;
        BeginTurn(message, plan.Request.Target);
        try
        {
            result = await _runtime.RunTurnAsync(plan.Request, stoppingToken);
        }
        finally
        {
            steered = await EndTurnAsync();
        }

        switch (result.Status)
        {
            case TurnStatus.Completed or TurnStatus.LimitExceeded:
                // The turn is persisted: only now may the message leave the inbox (NFR-02). A turn stopped at its own
                // credit limit was handled as far as its budget allowed; rerunning it would only overspend again.
                // Acknowledged before anything else can fail: a handled message never runs again.
                if (IsUserDm(message))
                {
                    // A user turn answers every user message in it.
                    await AcknowledgeAsync([message.Id, .. steered.Select(dm => dm.Id)]);
                    Forget(message.Id);
                    await RecordSpendAsync(message, plan, result);
                    await AfterTurnAsync(plan, result);
                    Raise(new InboxTurnCompletedEventArgs(Agent, message, result, steered));
                }
                else
                {
                    await AcknowledgeAsync([message.Id]);
                    Forget(message.Id);
                    await RecordSpendAsync(message, plan, result);
                    await AfterTurnAsync(plan, result);
                    Raise(new InboxTurnCompletedEventArgs(Agent, message, result, []));
                    if (steered.Count > 0)
                    {
                        await AnswerJoinedDmsAsync(steered, stoppingToken);
                    }
                }

                break;

            case TurnStatus.Blocked or TurnStatus.OverBudget or TurnStatus.Throttled:
                await RecordSpendAsync(message, plan, result);
                LogBlocked(_logger, Agent.Value, result.Error);
                Defer(message.Id, _options.BlockedPollInterval);
                break;

            default:
                await RecordSpendAsync(message, plan, result);
                await FailAsync(message, result.Error);
                break;
        }
    }

    /// <summary>
    /// User DMs that joined background work are in the conversation but have no reply of their own: one more main turn
    /// produces it (attributed to no routine). They are acknowledged only once that reply exists; if it fails they stay
    /// queued and later run as turns of their own.
    /// </summary>
    private async Task AnswerJoinedDmsAsync(IReadOnlyList<FleetMessage> dms, CancellationToken stoppingToken)
    {
        var reply = await _runtime.RunTurnAsync(
            new AgentTurnRequest
            {
                Prompt = MessagePrompts.ReplyToJoinedUserMessages,
                Origin = MessageOrigin.System,
                Principal = "user",
                Target = TurnTarget.Main,
            },
            stoppingToken);

        if (!Handled(reply))
        {
            LogReplyFailed(_logger, Agent.Value, reply.Error);
            return;
        }

        await AcknowledgeAsync([.. dms.Select(dm => dm.Id)]);
        foreach (var dm in dms)
        {
            Forget(dm.Id);
        }

        Raise(new InboxTurnCompletedEventArgs(Agent, dms[0], reply, [.. dms.Skip(1)]));
    }

    private async Task<TurnResult?> TrySteerAsync(string prompt, CancellationToken stoppingToken)
    {
        try
        {
            return await _runtime.SteerAsync(prompt, MessageOrigin.User, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Not joined: the DM runs as its own turn instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSteerFailed(_logger, ex, Agent.Value);
            return null;
        }
    }

    private async Task<TurnPlan?> PlanAsync(FleetMessage message, CancellationToken cancellationToken)
    {
        var target = TurnTarget.Main;
        var assignment = false;
        RoutineContinuation? continuation = null;
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
                continuation = _routines?.FindContinuation(Agent, key);
            }
            else if (await _coordinator.GetRunnableTaskAsync(Agent, key, cancellationToken) is { } task)
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

        Envelope? envelope = null;
        if (target.Task is null && message.Envelope is { } incoming)
        {
            // Only this pump's own failed attempts can use the message up; a message that arrives with little or nothing
            // left still gets its turn, under that limit.
            var spent = Spent(message.Id);
            if (spent > 0 && incoming.Budget.Credits - spent <= 0)
            {
                LogBudgetSpent(_logger, message.Id, Agent.Value);
                return null;
            }

            envelope = spent == 0 ? incoming : incoming with { Budget = incoming.Budget with { Credits = incoming.Budget.Credits - spent } };
        }

        var prompt = message.Origin switch
        {
            MessageOrigin.User => message.Body,
            MessageOrigin.Agent when assignment => MessagePrompts.ForTaskAssignment(target.Task!),
            MessageOrigin.Agent => MessagePrompts.ForAgentMessage(message),
            _ => MessagePrompts.ForSystemMessage(message),
        };

        // An isolated routine run resumes from its continuation; any other routine turn (a heartbeat in the main session)
        // from what the approval recorded. Only a host notice can carry these, and a boundary only adds approvals.
        var routineId = continuation?.Routine.Id ?? (message.Origin == MessageOrigin.System ? message.RoutineId : null);
        IReadOnlyList<string> boundary = continuation?.Routine.ApprovalBoundary ?? (message.Origin == MessageOrigin.System ? message.ApprovalBoundary : []);

        return new TurnPlan(
            new AgentTurnRequest
            {
                Prompt = prompt,
                Origin = message.Origin,
                Principal = continuation is { } run
                    ? $"routine:{run.Routine.Id}"
                    : message.Envelope?.OriginPrincipal ?? (message.Origin == MessageOrigin.User ? "user" : "system"),
                Target = target,
                From = message.From,
                Taint = message.Taint,
                Envelope = envelope,
                RoutineId = routineId,
                ApprovalBoundary = boundary,
                Model = continuation?.Model,
                Budget = continuation is { } c ? new Budget(c.RemainingCredits, 100, TimeSpan.FromMinutes(20)) : null,
            },
            assignment,
            continuation);
    }

    /// <summary>Marks a started task as working, debits its spend, and deletes sessions whose work has ended.</summary>
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

                if (await _coordinator.GetRunnableTaskAsync(Agent, task.Id, CancellationToken.None) is not { } current || current.State.IsTerminal())
                {
                    await _runtime.EndSessionAsync(target, CancellationToken.None);
                }
            }
            else if (plan.Continuation is { } continuation && _routines is { } routines)
            {
                await routines.CompleteContinuationAsync(continuation, _runtime, result);
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

    /// <summary>
    /// Every attempt's spend comes out of the budget carved for the work (FR-A2A-05), whatever the outcome, so retries
    /// cannot multiply it: a task's turns are debited from the task; a chained message that will be retried runs its next
    /// attempt with what is left of its envelope. Never throws: the turn has happened either way, and a debit the task
    /// store refuses must not make the pump treat a handled message as failed and run it again. The ledger still holds
    /// the spend against the agent's daily and monthly caps; the user is alerted that the task's own budget missed it.
    /// </summary>
    private async Task RecordSpendAsync(FleetMessage message, TurnPlan plan, TurnResult result)
    {
        var credits = result.Evidence.Credits;
        if (credits <= 0)
        {
            return;
        }

        if (plan.Request.Target.Task is { } task)
        {
            try
            {
                await _coordinator.RecordTaskSpendAsync(Agent, task.Id, credits, CancellationToken.None);
            }
#pragma warning disable CA1031 // See above: logged and alerted, never fatal to the pass.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSpendUnrecorded(_logger, ex, credits, task.Id, Agent.Value);
                await PublishAsync(new Alert(SpendUnrecordedAlertKind, AlertSeverity.Warning, $"{credits:0.##} credits {Agent} spent on task {task.Id} could not be debited from the task's budget.", _time.GetUtcNow(), Agent.Value));
            }
        }
        else if (message.Envelope is not null && !Handled(result))
        {
            lock (_gate)
            {
                _spent[message.Id] = _spent.GetValueOrDefault(message.Id) + credits;
            }
        }
    }

    /// <summary>What failed attempts at <paramref name="messageId"/> have spent.</summary>
    private decimal Spent(string messageId)
    {
        lock (_gate)
        {
            return _spent.GetValueOrDefault(messageId);
        }
    }

    /// <summary>
    /// Acknowledges handled messages. If the store fails, they are remembered as handled — never run again — and the
    /// acknowledgement is retried on later passes.
    /// </summary>
    private async Task AcknowledgeAsync(IReadOnlyList<string> messageIds)
    {
        foreach (var id in messageIds)
        {
            try
            {
                await _inbox.AcknowledgeAsync(Agent, id, CancellationToken.None);
            }
#pragma warning disable CA1031 // Retried by AcknowledgeHandledAsync.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogAckFailed(_logger, ex, id, Agent.Value);
                lock (_gate)
                {
                    _handled.Add(id);
                }
            }
        }
    }

    private async Task AcknowledgeHandledAsync(CancellationToken cancellationToken)
    {
        string[] owed;
        lock (_gate)
        {
            owed = [.. _handled];
        }

        foreach (var id in owed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _inbox.AcknowledgeAsync(Agent, id, CancellationToken.None);
                lock (_gate)
                {
                    _handled.Remove(id);
                }
            }
#pragma warning disable CA1031 // Still owed; tried again next pass.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogAckFailed(_logger, ex, id, Agent.Value);
            }
        }
    }

    /// <summary>Retries with back-off, then dead-letters; a message that cannot even be dead-lettered is skipped.</summary>
    private async Task FailAsync(FleetMessage message, string? error)
    {
        try
        {
            await RetryOrDeadLetterAsync(message, error);
        }
#pragma warning disable CA1031 // The store is failing: skip the message rather than spin on it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDeadLetterFailed(_logger, ex, message.Id, Agent.Value);
            lock (_gate)
            {
                _skipped.Add(message.Id);
            }

            Forget(message.Id);
            await PublishAsync(new Alert(DeadLetterAlertKind, AlertSeverity.Warning, $"A message for {Agent} kept failing and could not be moved to its dead-letter folder; it is skipped until capybarad restarts.", _time.GetUtcNow(), Agent.Value));
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
        await PublishAsync(new Alert(DeadLetterAlertKind, AlertSeverity.Warning, $"A message for {Agent} failed {attempts} times and was moved to its dead-letter folder.", _time.GetUtcNow(), Agent.Value));
    }

    private async Task PublishAsync(Alert alert)
    {
        try
        {
            await _alerts.PublishAsync(alert, CancellationToken.None);
        }
#pragma warning disable CA1031 // Alerting is best effort here; the pump goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAlertFailed(_logger, ex, Agent.Value);
        }
    }

    private async Task PauseAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, _time, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
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
            _spent.Remove(messageId);
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

    private void OnMessageQuarantined(object? sender, AgentId agent)
    {
        if (agent != Agent)
        {
            return;
        }

        LogQuarantined(_logger, Agent.Value);
        _ = PublishAsync(new Alert(QuarantineAlertKind, AlertSeverity.Warning, $"An unreadable message for {Agent} was moved to its dead-letter folder.", _time.GetUtcNow(), Agent.Value));
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
                    if (_mainTurnMessageId == inFlight && !_handled.Contains(dm.Id) && !_skipped.Contains(dm.Id) && _claimed.Add(dm.Id))
                    {
                        claimed.Add(dm);
                    }
                }
            }

            // Sent in arrival order; each call then waits for the turn they all joined.
            var outcomes = await Task.WhenAll(claimed.Select(dm => TrySteerAsync(dm.Body, CancellationToken.None)));
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

    private sealed record TurnPlan(AgentTurnRequest Request, bool Assignment, RoutineContinuation? Continuation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} for {Agent} concerns a finished task; acknowledged without a turn")]
    private static partial void LogSkipped(ILogger logger, string messageId, string agent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {MessageId} of {Agent} is dropped: failed attempts spent its whole budget")]
    private static partial void LogBudgetSpent(ILogger logger, string messageId, string agent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbox of {Agent} is on hold: {Reason}")]
    private static partial void LogBlocked(ILogger logger, string agent, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Turn for message {MessageId} of {Agent} failed (attempt {Attempt}); retrying in {Delay}: {Error}")]
    private static partial void LogRetry(ILogger logger, string messageId, string agent, int attempt, TimeSpan delay, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} of {Agent} failed {Attempts} times; dead-lettered: {Error}")]
    private static partial void LogDeadLetter(ILogger logger, string messageId, string agent, int attempts, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} of {Agent} could not be dead-lettered; skipping it")]
    private static partial void LogDeadLetterFailed(ILogger logger, Exception exception, string messageId, string agent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not acknowledge message {MessageId} of {Agent}; it will not run again and the acknowledgement is retried")]
    private static partial void LogAckFailed(ILogger logger, Exception exception, string messageId, string agent);

    [LoggerMessage(Level = LogLevel.Error, Message = "An inbox pass for {Agent} failed; continuing")]
    private static partial void LogIterationFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An unreadable message for {Agent} was quarantined")]
    private static partial void LogQuarantined(ILogger logger, string agent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not steer a DM into the turn in flight for {Agent}; it runs as its own turn")]
    private static partial void LogSteerFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The reply turn for user messages that joined background work of {Agent} failed ({Error}); they will run as their own turns")]
    private static partial void LogReplyFailed(ILogger logger, string agent, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Credits} credits spent on task {TaskId} by {Agent} could not be debited from the task's budget")]
    private static partial void LogSpendUnrecorded(ILogger logger, Exception exception, decimal credits, string taskId, string agent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Post-turn housekeeping for {Agent} failed")]
    private static partial void LogHousekeepingFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Error, Message = "A TurnCompleted subscriber failed for {Agent}")]
    private static partial void LogSubscriberFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not publish an inbox alert for {Agent}")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string agent);
}
