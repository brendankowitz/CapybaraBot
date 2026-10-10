using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Tasks;
using Capybara.Orchestration.Envelopes;
using Capybara.Orchestration.Inbox;
using Capybara.Orchestration.Registry;
using Capybara.Orchestration.Tasks;
using Capybara.Orchestration.Thread;
using Capybara.Policy.Audit;
using Capybara.Policy.Provenance;
using Capybara.Policy.Safety;

namespace Capybara.Orchestration.Coordination;

/// <summary>
/// Hub-and-spoke agent-to-agent tools (FR-A2A-01..11): <c>ask_agent</c>, <c>send_message</c>, <c>delegate_task</c>,
/// with peer allowlists, envelope enforcement, concurrency caps, ping-pong limits, taint propagation, the fleet thread
/// and single termination ownership.
/// </summary>
public sealed class A2ACoordinator : IDisposable
{
    private readonly AgentRegistry _registry;
    private readonly EnvelopeFactory _envelopes;
    private readonly IInboxStore _inbox;
    private readonly ITaskStore _tasks;
    private readonly FleetThread _thread;
    private readonly TaintTracker _taint;
    private readonly KillSwitch _killSwitch;
    private readonly IAuditLog _audit;
    private readonly IAgentTurnExecutor _executor;
    private readonly A2AOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _fleetSessions;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _consultsPerAgent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _exchanges = new(StringComparer.Ordinal);

    public A2ACoordinator(
        AgentRegistry registry,
        EnvelopeFactory envelopes,
        IInboxStore inbox,
        ITaskStore tasks,
        FleetThread thread,
        TaintTracker taint,
        KillSwitch killSwitch,
        IAuditLog audit,
        IAgentTurnExecutor executor,
        A2AOptions options,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _registry = registry;
        _envelopes = envelopes;
        _inbox = inbox;
        _tasks = tasks;
        _thread = thread;
        _taint = taint;
        _killSwitch = killSwitch;
        _audit = audit;
        _executor = executor;
        _options = options;
        _time = time;
        _fleetSessions = new SemaphoreSlim(options.MaxConcurrentNonMainSessions, options.MaxConcurrentNonMainSessions);
    }

    public IReadOnlyList<AgentCard> ListAgents(AgentId caller) => _registry.ListFor(caller);

    /// <summary><c>ask_agent</c>: synchronous consult in a fresh, read-only session on the target's runtime.</summary>
    public async Task<A2AResult> AskAgentAsync(
        AgentId caller,
        string callerSessionId,
        Envelope parent,
        AgentId target,
        string question,
        CancellationToken cancellationToken = default)
    {
        var auth = Authorize(caller, callerSessionId, parent, target, A2AOperation.Consult, _options.DefaultConsultBudget);
        if (auth.IsRejected)
        {
            return await RejectAsync(caller, target, A2AOperation.Consult, parent, auth.Error);
        }

        var (targetAgent, child) = (auth.Target, auth.Child);

        var perAgent = _consultsPerAgent.GetOrAdd(target.Value, _ => new SemaphoreSlim(_options.MaxConcurrentConsultsPerAgent, _options.MaxConcurrentConsultsPerAgent));
        if (!await perAgent.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return await RejectAsync(caller, target, A2AOperation.Consult, parent, $"{target} already has {_options.MaxConcurrentConsultsPerAgent} consults in flight.");
        }

        try
        {
            if (!await _fleetSessions.WaitAsync(TimeSpan.Zero, cancellationToken))
            {
                return await RejectAsync(caller, target, A2AOperation.Consult, parent, "fleet-wide session limit reached.");
            }

            try
            {
                var sessionId = SessionIds.Consult(target, Guid.NewGuid());
                _taint.Apply(sessionId, child.Taint);
                Record(caller, target, A2AOperation.Consult, child, Truncate(question, 200));

                // The child's budget is spent once the consult starts, whatever its outcome (FR-A2A-05).
                var remainder = auth.ParentRemainder;
                await PersistTaskEnvelopeAsync(caller, callerSessionId, remainder, cancellationToken);

                ConsultReply reply;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _killSwitch.FleetToken);
                timeout.CancelAfter(child.Budget.Wallclock);
                try
                {
                    reply = await _executor.ConsultAsync(targetAgent, sessionId, question, child, timeout.Token);
                }
#pragma warning disable CA1031 // Any consult failure becomes a rejection; the budget it used stays spent.
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
                {
                    var reason = ex is OperationCanceledException ? "timed out or was stopped" : $"failed ({ex.GetType().Name})";
                    await _audit.AppendAsync(new AuditRecord("a2a.consult", "failed", caller, callerSessionId, FleetTools.AskAgent, Detail: $"→ {target}: {reason}", TraceId: child.TraceId), CancellationToken.None);
                    return new A2AResult(false, $"the consult {reason}.", ParentRemainder: remainder);
                }

                // The consult's taint flows back into the caller's session (FR-A2A-08).
                _taint.Apply(sessionId, reply.Taint);
                _taint.Propagate(sessionId, callerSessionId);

                var answer = Truncate(reply.Answer, _options.MaxConsultAnswerChars);
                await _audit.AppendAsync(new AuditRecord("a2a.consult", "completed", caller, callerSessionId, FleetTools.AskAgent, Detail: $"→ {target}", TraceId: child.TraceId), CancellationToken.None);
                return new A2AResult(true, null, answer, _taint.Of(sessionId), sessionId, remainder);
            }
            finally
            {
                _fleetSessions.Release();
            }
        }
        finally
        {
            perAgent.Release();
        }
    }

    /// <summary>
    /// <c>send_message</c>: asynchronous; enqueues into the target's main-session inbox and wakes it (FR-A2A-03).
    /// The message's budget is carved out of the caller's envelope.
    /// </summary>
    public async Task<A2AResult> SendMessageAsync(
        AgentId caller,
        string callerSessionId,
        Envelope parent,
        AgentId target,
        string body,
        CancellationToken cancellationToken = default)
    {
        var auth = Authorize(caller, callerSessionId, parent, target, A2AOperation.Message, _options.DefaultConsultBudget);
        if (auth.IsRejected)
        {
            return await RejectAsync(caller, target, A2AOperation.Message, parent, auth.Error);
        }

        var child = auth.Child;

        var message = new FleetMessage
        {
            To = target,
            From = caller,
            Origin = MessageOrigin.Agent,
            Body = body,
            Envelope = child,
            Taint = child.Taint,
            CreatedAt = _time.GetUtcNow(),
        };

        await PersistTaskEnvelopeAsync(caller, callerSessionId, auth.ParentRemainder, cancellationToken);
        await _inbox.EnqueueAsync(message, cancellationToken);
        Record(caller, target, A2AOperation.Message, child, Truncate(body, 200));
        return new A2AResult(true, null, Reference: message.Id, ParentRemainder: auth.ParentRemainder);
    }

    /// <summary>
    /// <c>delegate_task</c>: creates a durable task owned by the target and run in its own task session; the result is
    /// an artifact by reference (FR-A2A-04). Questions and the result return to <paramref name="callerSessionId"/>.
    /// </summary>
    public async Task<A2AResult> DelegateTaskAsync(
        AgentId caller,
        string callerSessionId,
        Envelope parent,
        AgentId target,
        string objective,
        string? outputSchema,
        IReadOnlyList<string> boundaries,
        Budget budget,
        CancellationToken cancellationToken = default)
    {
        var auth = Authorize(caller, callerSessionId, parent, target, A2AOperation.Task, budget);
        if (auth.IsRejected)
        {
            return await RejectAsync(caller, target, A2AOperation.Task, parent, auth.Error);
        }

        var child = auth.Child;

        var now = _time.GetUtcNow();
        var task = new FleetTask
        {
            Owner = target,
            RequestedBy = caller,
            RequesterSessionId = ReplySession(caller, callerSessionId),
            Objective = objective,
            OutputSchema = outputSchema,
            Boundaries = boundaries,
            Envelope = child,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await PersistTaskEnvelopeAsync(caller, callerSessionId, auth.ParentRemainder, cancellationToken);
        await _tasks.SaveAsync(task, cancellationToken);
        await _inbox.EnqueueAsync(
            new FleetMessage
            {
                To = target,
                From = caller,
                Origin = MessageOrigin.Agent,
                Body = $"[task {task.Id}] {objective}",
                Envelope = child,
                Taint = child.Taint,
                CreatedAt = now,
                TaskId = task.Id,
                SessionId = SessionIds.Task(target, task.Id),
            },
            cancellationToken);

        Record(caller, target, A2AOperation.Task, child, Truncate(objective, 200));
        return new A2AResult(true, null, Reference: task.Id, ParentRemainder: auth.ParentRemainder);
    }

    /// <summary>The task, if <paramref name="owner"/> owns it; <see langword="null"/> otherwise.</summary>
    public async Task<FleetTask?> GetOwnedTaskAsync(AgentId owner, string taskId, CancellationToken cancellationToken = default)
    {
        var task = await LoadTaskAsync(taskId, cancellationToken);
        return task?.Owner == owner ? task : null;
    }

    /// <summary>The owner has started work on a submitted task. Returns the current task, or null if not owned.</summary>
    public async Task<FleetTask?> MarkWorkingAsync(AgentId owner, string taskId, CancellationToken cancellationToken = default)
    {
        var task = await GetOwnedTaskAsync(owner, taskId, cancellationToken);
        if (task is null || task.State != FleetTaskState.Submitted)
        {
            return task;
        }

        var working = task with { State = FleetTaskState.Working, UpdatedAt = _time.GetUtcNow() };
        await _tasks.SaveAsync(working, cancellationToken);
        return working;
    }

    /// <summary>
    /// A sub-agent needs input: the task moves to <c>INPUT_REQUIRED</c> and the question bubbles up the hop path to the
    /// requester's session (FR-A2A-07). Only the root, user-facing agent may escalate to the human.
    /// </summary>
    public Task<A2AResult> RequestInputAsync(AgentId owner, string taskId, string question, CancellationToken cancellationToken = default) =>
        RequestInputAsync(owner, SessionIds.Task(owner, taskId), taskId, question, cancellationToken);

    /// <summary>
    /// A sub-agent needs input (FR-A2A-07). The question is the owner's content — data, never instructions or consent
    /// (FR-A2A-08) — and carries the taint of <paramref name="ownerSessionId"/>.
    /// </summary>
    public async Task<A2AResult> RequestInputAsync(AgentId owner, string ownerSessionId, string taskId, string question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        var task = await LoadTaskAsync(taskId, cancellationToken);
        if (task is null || task.Owner != owner)
        {
            return A2AResult.Rejected("only the task owner can request input.");
        }

        if (task.State.IsTerminal())
        {
            return A2AResult.Rejected($"task is already {task.State}.");
        }

        if (_killSwitch.IsBlocked(owner) || _killSwitch.IsBlocked(task.RequestedBy))
        {
            return A2AResult.Rejected("agent is paused.");
        }

        var now = _time.GetUtcNow();
        var taint = Combine(task.Envelope.Taint, _taint.Of(ownerSessionId));
        await _tasks.SaveAsync(task with { State = FleetTaskState.InputRequired, PendingQuestion = question, UpdatedAt = now }, cancellationToken);
        await _inbox.EnqueueAsync(
            new FleetMessage
            {
                To = task.RequestedBy,
                From = owner,
                Origin = MessageOrigin.Agent,
                Body = $"[task {task.Id} needs input] {question}",
                Envelope = task.Envelope,
                Taint = taint,
                CreatedAt = now,
                TaskId = task.Id,
                SessionId = task.RequesterSessionId,
            },
            cancellationToken);
        _thread.Append(new FleetThreadEntry(now, owner, task.RequestedBy, A2AOperation.Task, $"needs input: {Truncate(question, 200)}", task.Envelope.TraceId, task.Envelope.Depth, taint));
        await _audit.AppendAsync(new AuditRecord("a2a.task", "input_required", owner, ownerSessionId, FleetTools.RequestInput, Detail: $"task {task.Id} → {task.RequestedBy}", TraceId: task.Envelope.TraceId), CancellationToken.None);
        return new A2AResult(true, null, Reference: task.Id);
    }

    /// <summary>
    /// The requester answers an <c>INPUT_REQUIRED</c> task: the task goes back to <c>WORKING</c> and the answer is
    /// delivered to the owner's task session (FR-A2A-07). The answer is agent content: data, never consent (FR-A2A-08).
    /// </summary>
    public async Task<A2AResult> ProvideInputAsync(AgentId requester, string requesterSessionId, string taskId, string answer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var task = await LoadTaskAsync(taskId, cancellationToken);
        if (task is null || task.RequestedBy != requester)
        {
            return A2AResult.Rejected("only the agent that delegated the task can answer it.");
        }

        if (task.State != FleetTaskState.InputRequired)
        {
            return A2AResult.Rejected($"task is {task.State}, not waiting for input.");
        }

        if (_killSwitch.IsBlocked(requester) || _killSwitch.IsBlocked(task.Owner))
        {
            return A2AResult.Rejected("agent is paused.");
        }

        var now = _time.GetUtcNow();
        var taint = Combine(task.Envelope.Taint, _taint.Of(requesterSessionId));
        await _tasks.SaveAsync(task with { State = FleetTaskState.Working, PendingQuestion = null, UpdatedAt = now }, cancellationToken);
        await _inbox.EnqueueAsync(
            new FleetMessage
            {
                To = task.Owner,
                From = requester,
                Origin = MessageOrigin.Agent,
                Body = $"[task {task.Id} answer] {answer}",
                Envelope = task.Envelope,
                Taint = taint,
                CreatedAt = now,
                TaskId = task.Id,
                SessionId = SessionIds.Task(task.Owner, task.Id),
            },
            cancellationToken);
        _thread.Append(new FleetThreadEntry(now, requester, task.Owner, A2AOperation.Task, $"answer: {Truncate(answer, 200)}", task.Envelope.TraceId, task.Envelope.Depth, taint));
        await _audit.AppendAsync(new AuditRecord("a2a.task", "input_provided", requester, requesterSessionId, FleetTools.AnswerTask, Detail: $"task {task.Id} → {task.Owner}", TraceId: task.Envelope.TraceId), CancellationToken.None);
        return new A2AResult(true, null, Reference: task.Id);
    }

    /// <summary>
    /// Single termination owner (FR-A2A-09): only the owner can complete, and only after verifying against the objective.
    /// </summary>
    public Task<A2AResult> CompleteTaskAsync(
        AgentId owner,
        string taskId,
        bool verifiedAgainstObjective,
        IReadOnlyList<Artifact> artifacts,
        IReadOnlyList<string> unverified,
        CancellationToken cancellationToken = default) =>
        CompleteTaskAsync(owner, taskId, verifiedAgainstObjective, artifacts, unverified, Taint.Clean, cancellationToken);

    /// <summary>
    /// Single termination owner (FR-A2A-09). The result goes back to the requester's session carrying
    /// <paramref name="resultTaint"/>, the taint of the owner session that produced it (FR-A2A-08).
    /// </summary>
    public async Task<A2AResult> CompleteTaskAsync(
        AgentId owner,
        string taskId,
        bool verifiedAgainstObjective,
        IReadOnlyList<Artifact> artifacts,
        IReadOnlyList<string> unverified,
        Taint resultTaint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(unverified);
        var task = await LoadTaskAsync(taskId, cancellationToken);
        if (task is null || task.Owner != owner)
        {
            return A2AResult.Rejected("only the task owner can complete a task.");
        }

        if (task.State.IsTerminal())
        {
            return A2AResult.Rejected($"task is already {task.State}.");
        }

        if (!verifiedAgainstObjective)
        {
            return A2AResult.Rejected("a verification step against the objective and output schema is required before completion.");
        }

        if (_killSwitch.IsBlocked(owner))
        {
            return A2AResult.Rejected("agent is paused.");
        }

        var now = _time.GetUtcNow();
        var taint = Combine(task.Envelope.Taint, resultTaint);
        var done = task with { State = FleetTaskState.Completed, Artifacts = artifacts, Unverified = unverified, PendingQuestion = null, UpdatedAt = now };
        await _tasks.SaveAsync(done, cancellationToken);
        await _inbox.EnqueueAsync(
            new FleetMessage
            {
                To = task.RequestedBy,
                From = owner,
                Origin = MessageOrigin.Agent,
                Body = $"[task {task.Id} completed] {artifacts.Count} artifact(s); {unverified.Count} unverified item(s).",
                Attachments = artifacts,
                Envelope = task.Envelope,
                Taint = taint,
                CreatedAt = now,
                TaskId = task.Id,
                SessionId = task.RequesterSessionId,
            },
            cancellationToken);
        _thread.Append(new FleetThreadEntry(now, owner, task.RequestedBy, A2AOperation.Task, $"completed: {artifacts.Count} artifact(s)", task.Envelope.TraceId, task.Envelope.Depth, taint));
        await _audit.AppendAsync(new AuditRecord("a2a.task", "completed", owner, SessionIds.Task(owner, task.Id), FleetTools.CompleteTask, Detail: $"task {task.Id} → {task.RequestedBy}", TraceId: task.Envelope.TraceId), CancellationToken.None);
        return new A2AResult(true, null, Reference: task.Id);
    }

    public void Dispose()
    {
        _fleetSessions.Dispose();
        foreach (var semaphore in _consultsPerAgent.Values)
        {
            semaphore.Dispose();
        }
    }

    private Authorization Authorize(
        AgentId caller,
        string callerSessionId,
        Envelope parent,
        AgentId target,
        A2AOperation operation,
        Budget requested)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var callerAgent = _registry.Find(caller);
        var targetAgent = _registry.Find(target);
        if (callerAgent is null || targetAgent is null)
        {
            return Authorization.Reject("unknown agent.");
        }

        if (_killSwitch.IsBlocked(caller) || _killSwitch.IsBlocked(target) || targetAgent.Paused)
        {
            return Authorization.Reject("agent is paused.");
        }

        if (!callerAgent.Peers.Allows(operation, target))
        {
            return Authorization.Reject($"{caller} is not allowed to {operation.ToString().ToLowerInvariant()} {target}.");
        }

        var accepts = operation switch
        {
            A2AOperation.Consult => targetAgent.AcceptsConsults,
            A2AOperation.Message => targetAgent.AcceptsMessages,
            A2AOperation.Task => targetAgent.AcceptsTasks,
            _ => false,
        };
        if (!accepts)
        {
            return Authorization.Reject($"{target} does not accept {operation.ToString().ToLowerInvariant()}s.");
        }

        var (first, second) = string.CompareOrdinal(caller.Value, target.Value) < 0 ? (caller, target) : (target, caller);
        var pair = $"{parent.RootTaskId}:{first}|{second}";
        if (_exchanges.AddOrUpdate(pair, 1, (_, n) => n + 1) > _options.MaxExchangesPerPairPerTask)
        {
            return Authorization.Reject($"exchange limit between {caller} and {target} reached for this task; involve the user.");
        }

        var result = _envelopes.CreateChild(parent, target, requested, _taint.Of(callerSessionId), OwnTaskId(caller, callerSessionId));
        return result.IsSuccess ? new Authorization(targetAgent, result.Child, result.ParentRemainder, null) : Authorization.Reject(result.Error!);
    }

    /// <summary>The delegated task <paramref name="sessionId"/> works on, when it is one of <paramref name="agent"/>'s task sessions.</summary>
    private static string? OwnTaskId(AgentId agent, string sessionId) =>
        SessionIds.TryParseTask(sessionId, out var owner, out var key) && owner == agent && FileTaskStore.IsTaskId(key) ? key : null;

    /// <summary>Replies go to the delegating session, unless it is an ephemeral consult; then to the main session.</summary>
    private static string? ReplySession(AgentId caller, string callerSessionId) =>
        SessionIds.TryGetAgent(callerSessionId, out var agent) && agent == caller && !callerSessionId.Contains(":consult:", StringComparison.Ordinal)
            ? callerSessionId
            : null;

    /// <summary>
    /// A task session's envelope survives across its turns in the task record, so the debit a call made is persisted
    /// there (FR-A2A-05). Main sessions get a fresh root envelope per turn and need nothing stored.
    /// </summary>
    private async ValueTask PersistTaskEnvelopeAsync(AgentId caller, string callerSessionId, Envelope remainder, CancellationToken cancellationToken)
    {
        if (OwnTaskId(caller, callerSessionId) is not { } taskId
            || await LoadTaskAsync(taskId, cancellationToken) is not { } task
            || task.Owner != caller
            || task.State.IsTerminal())
        {
            return;
        }

        await _tasks.SaveAsync(task with { Envelope = task.Envelope with { Budget = remainder.Budget }, UpdatedAt = _time.GetUtcNow() }, cancellationToken);
    }

    /// <summary>Model-supplied ids that are not task ids address nothing.</summary>
    private async ValueTask<FleetTask?> LoadTaskAsync(string taskId, CancellationToken cancellationToken) =>
        FileTaskStore.IsTaskId(taskId) ? await _tasks.GetAsync(taskId, cancellationToken) : null;

    private static Taint Combine(Taint a, Taint b) => a == Taint.Tainted || b == Taint.Tainted ? Taint.Tainted : Taint.Clean;

    private async Task<A2AResult> RejectAsync(AgentId caller, AgentId target, A2AOperation operation, Envelope parent, string error)
    {
        _thread.Append(new FleetThreadEntry(_time.GetUtcNow(), caller, target, operation, error, parent.TraceId, parent.Depth + 1, parent.Taint, Rejected: true));
        await _audit.AppendAsync(new AuditRecord($"a2a.{operation.ToString().ToLowerInvariant()}", "rejected", caller, Detail: $"→ {target}: {error}", TraceId: parent.TraceId), CancellationToken.None);
        return A2AResult.Rejected(error);
    }

    private void Record(AgentId caller, AgentId target, A2AOperation operation, Envelope child, string summary) =>
        _thread.Append(new FleetThreadEntry(_time.GetUtcNow(), caller, target, operation, summary, child.TraceId, child.Depth, child.Taint));

    private static string Truncate(string value, int max) => value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");

    private sealed record Authorization(AgentDefinition? Target, Envelope? Child, Envelope? ParentRemainder, string? Error)
    {
        [MemberNotNullWhen(true, nameof(Error))]
        [MemberNotNullWhen(false, nameof(Target), nameof(Child), nameof(ParentRemainder))]
        public bool IsRejected => Error is not null;

        public static Authorization Reject(string error) => new(null, null, null, error);
    }
}
