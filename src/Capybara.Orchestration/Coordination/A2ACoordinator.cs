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

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _killSwitch.FleetToken);
                timeout.CancelAfter(child.Budget.Wallclock);
                var reply = await _executor.ConsultAsync(targetAgent, sessionId, question, child, timeout.Token);

                // The consult's taint flows back into the caller's session (FR-A2A-08).
                _taint.Apply(sessionId, reply.Taint);
                _taint.Propagate(sessionId, callerSessionId);

                var answer = Truncate(reply.Answer, _options.MaxConsultAnswerChars);
                await _audit.AppendAsync(new AuditRecord("a2a.consult", "completed", caller, callerSessionId, FleetTools.AskAgent, Detail: $"→ {target}", TraceId: child.TraceId), CancellationToken.None);
                return new A2AResult(true, null, answer, _taint.Of(sessionId), sessionId);
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

    /// <summary><c>send_message</c>: asynchronous; enqueues into the target's inbox and wakes it.</summary>
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

        await _inbox.EnqueueAsync(message, cancellationToken);
        Record(caller, target, A2AOperation.Message, child, Truncate(body, 200));
        return new A2AResult(true, null, Reference: message.Id);
    }

    /// <summary><c>delegate_task</c>: creates a durable task owned by the target; the result is an artifact by reference.</summary>
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
            Objective = objective,
            OutputSchema = outputSchema,
            Boundaries = boundaries,
            Envelope = child,
            CreatedAt = now,
            UpdatedAt = now,
        };

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
            },
            cancellationToken);

        Record(caller, target, A2AOperation.Task, child, Truncate(objective, 200));
        return new A2AResult(true, null, Reference: task.Id);
    }

    /// <summary>
    /// A sub-agent needs input: the task moves to <c>INPUT_REQUIRED</c> and the question bubbles to the requester
    /// (FR-A2A-07). Only the root, user-facing agent may escalate to the human.
    /// </summary>
    public async Task<A2AResult> RequestInputAsync(AgentId owner, string taskId, string question, CancellationToken cancellationToken = default)
    {
        var task = await _tasks.GetAsync(taskId, cancellationToken);
        if (task is null || task.Owner != owner)
        {
            return A2AResult.Rejected("only the task owner can request input.");
        }

        if (task.State.IsTerminal())
        {
            return A2AResult.Rejected($"task is already {task.State}.");
        }

        await _tasks.SaveAsync(task with { State = FleetTaskState.InputRequired, PendingQuestion = question, UpdatedAt = _time.GetUtcNow() }, cancellationToken);
        await _inbox.EnqueueAsync(
            new FleetMessage
            {
                To = task.RequestedBy,
                From = owner,
                Origin = MessageOrigin.Agent,
                Body = $"[task {task.Id} needs input] {question}",
                Envelope = task.Envelope,
                Taint = task.Envelope.Taint,
                CreatedAt = _time.GetUtcNow(),
            },
            cancellationToken);
        return new A2AResult(true, null, Reference: task.Id);
    }

    /// <summary>
    /// Single termination owner (FR-A2A-09): only the owner can complete, and only after verifying against the objective.
    /// </summary>
    public async Task<A2AResult> CompleteTaskAsync(
        AgentId owner,
        string taskId,
        bool verifiedAgainstObjective,
        IReadOnlyList<Artifact> artifacts,
        IReadOnlyList<string> unverified,
        CancellationToken cancellationToken = default)
    {
        var task = await _tasks.GetAsync(taskId, cancellationToken);
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

        var done = task with { State = FleetTaskState.Completed, Artifacts = artifacts, Unverified = unverified, PendingQuestion = null, UpdatedAt = _time.GetUtcNow() };
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
                Taint = task.Envelope.Taint,
                CreatedAt = _time.GetUtcNow(),
            },
            cancellationToken);
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

        var result = _envelopes.CreateChild(parent, target, requested, _taint.Of(callerSessionId));
        return result.IsSuccess ? new Authorization(targetAgent, result.Child, null) : Authorization.Reject(result.Error!);
    }

    private async Task<A2AResult> RejectAsync(AgentId caller, AgentId target, A2AOperation operation, Envelope parent, string error)
    {
        _thread.Append(new FleetThreadEntry(_time.GetUtcNow(), caller, target, operation, error, parent.TraceId, parent.Depth + 1, parent.Taint, Rejected: true));
        await _audit.AppendAsync(new AuditRecord($"a2a.{operation.ToString().ToLowerInvariant()}", "rejected", caller, Detail: $"→ {target}: {error}", TraceId: parent.TraceId), CancellationToken.None);
        return A2AResult.Rejected(error);
    }

    private void Record(AgentId caller, AgentId target, A2AOperation operation, Envelope child, string summary) =>
        _thread.Append(new FleetThreadEntry(_time.GetUtcNow(), caller, target, operation, summary, child.TraceId, child.Depth, child.Taint));

    private static string Truncate(string value, int max) => value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");

    private sealed record Authorization(AgentDefinition? Target, Envelope? Child, string? Error)
    {
        [MemberNotNullWhen(true, nameof(Error))]
        [MemberNotNullWhen(false, nameof(Target), nameof(Child))]
        public bool IsRejected => Error is not null;

        public static Authorization Reject(string error) => new(null, null, error);
    }
}
