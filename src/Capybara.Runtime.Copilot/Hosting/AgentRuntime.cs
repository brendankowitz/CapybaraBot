using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Envelopes;
using Capybara.Policy.Provenance;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hooks;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Runtime.Copilot.Supervision;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>
/// One agent = one <see cref="CopilotClient"/> = one CLI process with its own <c>BaseDirectory</c> (FR-AGT-01).
/// Owns the agent's resumable main session and runs task, isolated, incognito and consult sessions.
/// </summary>
/// <remarks>
/// <para>
/// Session configurations are always built with the async builders, so BYOK keys, http MCP tokens and broker tools are
/// fetched fresh on every create and resume (FR-AGT-03). One turn at a time runs per session (FR-AGT-05): the main
/// session has its own lock, every other session one of its own. Turn failures come back as
/// <see cref="TurnStatus.Failed"/> results; only cancellation by the caller throws.
/// </para>
/// <para>
/// A user DM steered into a turn in flight (<see cref="SteerAsync"/>) uses <c>immediate</c> mode and shares that turn's
/// outcome; dry-run turns are never steered into. If the turn finishes at the same instant, the CLI queues the message
/// as the next run of the same session, so turns still never overlap (that run's reply is in the session history but
/// is not returned to the caller).
/// </para>
/// </remarks>
public sealed partial class AgentRuntime : IAgentRuntime, ISupervisedRuntime, ISessionEventHistory
{
    /// <summary>Alert raised when a turn overspends its own credit limit and is stopped.</summary>
    public const string LimitAlertKind = "runtime.turn_over_budget";

    private const int MainTurnToolCalls = 200;
    private const string ImmediateMode = "immediate";

    private readonly AgentDefinition _agent;
    private readonly CopilotRuntimeOptions _options;
    private readonly SessionConfigFactory _configs;
    private readonly EnvelopeFactory _envelopes;
    private readonly CostLedger _ledger;
    private readonly KillSwitch _killSwitch;
    private readonly TaintTracker _taint;
    private readonly PermissionAllowances _allowances;
    private readonly TurnMemory _memory;
    private readonly IAlertSink _alerts;
    private readonly SessionLimiter _sessions;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentRuntime> _logger;
    private readonly SemaphoreSlim _mainLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly KeyedLocks _sessionLocks = new();

    private readonly Lock _turnGate = new();

    private CopilotClient? _client;
    private CopilotSession? _main;
    private CapybaraSession? _mainSession;
    private string _model;
    private InFlightTurn? _mainTurn;
    private bool _mainToolsAtMaxDepth;

    public AgentRuntime(
        AgentDefinition agent,
        CopilotRuntimeOptions options,
        SessionConfigFactory configs,
        EnvelopeFactory envelopes,
        CostLedger ledger,
        KillSwitch killSwitch,
        TaintTracker taint,
        PermissionAllowances allowances,
        TurnMemory memory,
        IAlertSink alerts,
        SessionLimiter sessions,
        TimeProvider time,
        ILogger<AgentRuntime> logger)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _agent = agent;
        _options = options;
        _configs = configs;
        _envelopes = envelopes;
        _ledger = ledger;
        _killSwitch = killSwitch;
        _taint = taint;
        _allowances = allowances;
        _memory = memory;
        _alerts = alerts;
        _sessions = sessions;
        _time = time;
        _logger = logger;
        _model = agent.Model;
    }

    public AgentId Agent => _agent.Id;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            return false;
        }

        try
        {
            await client.PingAsync("fleet", cancellationToken);
            return true;
        }
#pragma warning disable CA1031 // Health probe.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogPingFailed(_logger, ex, _agent.Id.Value);
            return false;
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
            await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<TurnResult> RunTurnAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        if (_killSwitch.IsBlocked(_agent.Id))
        {
            return TurnResult.Blocked("agent is paused or the fleet kill switch is engaged");
        }

        if (!_ledger.CanSpend(_agent.Id))
        {
            return TurnResult.OverBudget("budget exhausted (hard stop)");
        }

        try
        {
            return request.Target.Kind == SessionKind.Main
                ? await RunMainTurnAsync(request, cancellationToken)
                : await RunSessionTurnAsync(request, cancellationToken);
        }
#pragma warning disable CA1031 // A failed turn is a result; the caller decides whether to retry.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            LogTurnFailed(_logger, ex, _agent.Id.Value, request.Target.Kind);
            return TurnResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<TurnResult?> SteerAsync(string prompt, MessageOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        InFlightTurn? turn;
        lock (_turnGate)
        {
            turn = _mainTurn;
        }

        // The run decides admission under its own lock: steering closes at its idle, so a message can never be counted
        // as joined to a run that already ended without the turn also waiting for what it started.
        if (turn is null || !await turn.Run.SteerAsync(new MessageOptions { Prompt = prompt, Source = SourceFor(origin, null), Mode = ImmediateMode }, cancellationToken))
        {
            return null;
        }

        LogSteered(_logger, _agent.Id.Value);
        return await turn.Done.Task.WaitAsync(cancellationToken);
    }

    public async Task SetModelAsync(string model, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        await _mainLock.WaitAsync(cancellationToken);
        try
        {
            if (_main is { } session && _mainSession is { } context && context.Model != model)
            {
                await session.SetModelAsync(model, cancellationToken);
                context.Model = model;
            }

            _model = model;
        }
        finally
        {
            _mainLock.Release();
        }
    }

    public async Task EndSessionAsync(TurnTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == SessionKind.Main)
        {
            throw new ArgumentException("The main session is never ended.", nameof(target));
        }

        var sessionId = target.SessionIdFor(_agent.Id);
        using (await _sessionLocks.AcquireAsync(sessionId, cancellationToken))
        {
            if (_client is { } client)
            {
                try
                {
                    await client.DeleteSessionAsync(sessionId, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    // Never created, or already gone.
                    LogDeleteSkipped(_logger, sessionId, ex.Message);
                }
            }

            _taint.Forget(sessionId);
            _allowances.Clear(sessionId);
        }
    }

    /// <summary>Answers one <c>ask_agent</c> question in a fresh, read-only, ephemeral session.</summary>
    public async Task<ConsultReply> ConsultAsync(string sessionId, string question, Envelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var client = _client ?? throw new InvalidOperationException($"Runtime for {_agent.Id} is not running.");
        var context = new CapybaraSession(_agent, sessionId, SessionKind.Consult, envelope) { TurnOrigin = MessageOrigin.Agent, Model = _model };
        _taint.Apply(sessionId, envelope.Taint);

        var caller = envelope.HopPath.Count >= 2 ? envelope.HopPath[^2].Value : "fleet";
        var session = await client.CreateSessionAsync(await _configs.CreateConfigAsync(context, cancellationToken), cancellationToken);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _killSwitch.FleetToken);
            var reply = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = question, Source = MessageSource.Agent(caller) },
                _options.ConsultTimeout,
                linked.Token);
            return new ConsultReply(reply?.Data?.Content ?? string.Empty, _taint.Of(sessionId), []);
        }
        finally
        {
            await session.DisposeAsync();
            await client.DeleteSessionAsync(sessionId, CancellationToken.None);
            _taint.Forget(sessionId);
            _allowances.Clear(sessionId);
        }
    }

    /// <summary>
    /// Asserts the tools the runtime actually exposes match the allowlist: nothing missing and nothing ambient
    /// leaking through (FR-AGT-09). The tool list is polled until the runtime reports one; if it never does within
    /// <see cref="CopilotRuntimeOptions.SelfTestToolsTimeout"/> this throws, which the guard treats as a failure.
    /// </summary>
    public async Task<SelfTestResult> SelfTestAsync(CancellationToken cancellationToken)
    {
        await _mainLock.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureMainSessionAsync(SystemEnvelope(), cancellationToken);
            List<(string Name, string Description)>? metadata = null;
            await ToolMetadataPoller.WaitForToolsAsync(
                async ct =>
                {
                    metadata = (await session.Rpc.Tools.GetCurrentMetadataAsync(ct)).Tools?.Select(t => (t.Name, t.Description)).ToList();
                    return metadata?.Select(t => t.Name).ToList();
                },
                _options.SelfTestToolsTimeout,
                _options.SelfTestPollInterval,
                _time,
                cancellationToken);
            var reported = metadata!.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Description, StringComparer.Ordinal);
            var result = Compare(SessionConfigFactory.AvailableToolsFor(_mainSession!), reported.Keys.ToHashSet(StringComparer.Ordinal));
            return WithImpostors(result, ImpostorTools(_configs.HostToolsFor(_mainSession!), reported));
        }
        finally
        {
            _mainLock.Release();
        }
    }

    /// <summary>The main session's persisted event log, for reconciling a turn a crash interrupted (FR-AGT-04).</summary>
    public async Task<IReadOnlyList<SessionEvent>> GetMainSessionEventsAsync(CancellationToken cancellationToken)
    {
        await _mainLock.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureMainSessionAsync(SystemEnvelope(), cancellationToken);
            return [.. await session.GetEventsAsync(cancellationToken)];
        }
        finally
        {
            _mainLock.Release();
        }
    }

    /// <summary>
    /// Host tools whose name the runtime reports with a different description: a CLI built-in of the same name (such as
    /// its own <c>list_agents</c>) standing in for a host tool that was not registered (FR-AGT-09).
    /// </summary>
    public static IReadOnlyList<string> ImpostorTools(IEnumerable<AIFunctionDeclaration> host, IReadOnlyDictionary<string, string> reported)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reported);
        return [.. host
            .Where(t => reported.TryGetValue(t.Name, out var description) && !string.Equals(description, t.Description, StringComparison.Ordinal))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)];
    }

    private static SelfTestResult WithImpostors(SelfTestResult result, IReadOnlyList<string> impostors) =>
        impostors.Count == 0
            ? result
            : new SelfTestResult(false, result.Missing, [.. result.Unexpected, .. impostors.Select(t => $"{t} (not the host tool)")]);

    public static SelfTestResult Compare(IReadOnlyCollection<string> expected, IReadOnlySet<string> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var missing = expected.Where(t => !actual.Contains(t)).Order(StringComparer.Ordinal).ToList();
        var unexpected = actual.Where(t => !expected.Contains(t)).Order(StringComparer.Ordinal).ToList();
        return new SelfTestResult(missing.Count == 0 && unexpected.Count == 0, missing, unexpected);
    }

    public async ValueTask DisposeAsync()
    {
        await StopCoreAsync();
        _mainLock.Dispose();
        _lifecycleLock.Dispose();
    }

    private async Task<TurnResult> RunMainTurnAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        await _mainLock.WaitAsync(cancellationToken);
        CapybaraSession? context = null;
        CopilotSession? session = null;
        try
        {
            var (envelope, creditLimit) = TurnEnvelope(request);
            session = await EnsureMainSessionAsync(envelope, cancellationToken);
            context = _mainSession!;
            context.Envelope = envelope;
            context.TurnOrigin = request.Origin;
            context.TurnFrom = request.From;
            context.RoutineId = request.RoutineId;
            context.DryRun = request.DryRun;
            var turn = context.BeginTurn(creditLimit);
            await MatchToolsToDepthAsync(session, context, cancellationToken);
            _taint.Apply(context.SessionId, request.Taint);

            if (request.Model is { } model && model != context.Model)
            {
                await session.SetModelAsync(model, cancellationToken);
                context.Model = model;
            }

            var prompt = await _memory.PrepareAsync(context, request.Prompt, cancellationToken);
            using var run = new TurnRun(new CopilotTurnSession(session), steerable: !request.DryRun, Timings);
            var inFlight = new InFlightTurn(run);
            lock (_turnGate)
            {
                _mainTurn = inFlight;
            }

            TurnResult? result = null;
            string? lateContent = null;
            try
            {
                result = await SendTurnAsync(run, turn, prompt, SourceFor(request.Origin, request.From), DropMainSessionAsync, cancellationToken);
            }
            finally
            {
                lock (_turnGate)
                {
                    _mainTurn = null;
                }

                // Nothing may still run once the lock is released: steering in flight, or a run a late steer started.
                try
                {
                    lateContent = await run.FinishAsync();
                }
                catch (TurnAbortFailedException ex)
                {
                    LogAbortFailed(_logger, ex.InnerException, _agent.Id.Value);
                    await DropMainSessionAsync();
                }

                // Messages steered into this turn share its outcome; a late steer gets the run it started.
                result = (result ?? TurnResult.Failed("the turn did not complete", turn.Snapshot()))
                    with { RoutineId = request.RoutineId, UserMessagesJoined = run.Joined };
                inFlight.Done.TrySetResult(lateContent is null ? result : result with { Content = lateContent });
            }

            if (result.Success)
            {
                await _memory.IngestAsync(context, request.Prompt, result.Content, cancellationToken);
            }

            return result;
        }
        finally
        {
            if (context is not null)
            {
                context.DryRun = false;
                context.RoutineId = null;
                await RestoreModelAsync(session, context);
            }

            _mainLock.Release();
        }
    }

    /// <summary>
    /// The envelope a turn runs under, and its credit limit, which the host enforces by aborting the run (the SDK's own
    /// session limit cannot go below 30 credits). Every turn is capped at the agent's <c>SessionMaxCredits</c>; a routine
    /// run at its per-run budget; a turn handling another agent's message continues that message's chain within its
    /// carved budget (FR-A2A-05).
    /// </summary>
    private (Envelope Envelope, decimal? CreditLimit) TurnEnvelope(AgentTurnRequest request)
    {
        var cap = request.Budget ?? new Budget(_agent.Budget.SessionMaxCredits, MainTurnToolCalls, _options.TurnTimeout);
        if (request.Envelope is { } incoming)
        {
            var continued = EnvelopeFactory.ContinueFrom(incoming, _agent.Id, cap);
            return (continued, continued.Budget.Credits);
        }

        return (_envelopes.CreateRoot(request.Principal, _agent.Id, cap, _options.TurnTimeout, rootOrigin: request.Origin), cap.Credits);
    }

    /// <summary>
    /// The main session's custom tools are fixed when it is built, but a turn continuing another agent's chain may run at
    /// maximum depth, where agent-to-agent tools must be absent (FR-A2A-06). The tool set is swapped whenever a turn's
    /// depth class differs from the one it was built for.
    /// </summary>
    private async Task MatchToolsToDepthAsync(CopilotSession session, CapybaraSession context, CancellationToken cancellationToken)
    {
        var atMax = context.Envelope.IsAtMaxDepth;
        if (atMax == _mainToolsAtMaxDepth)
        {
            return;
        }

#pragma warning disable GHCP001 // SetToolsAsync is evaluation-only; it is the only way to change a live session's custom tools (verified live).
        await session.SetToolsAsync([.. await _configs.BuildToolsAsync(context, cancellationToken)], cancellationToken);
#pragma warning restore GHCP001
        _mainToolsAtMaxDepth = atMax;
    }

    /// <summary>A per-turn model override (a routine's model) does not outlive its turn.</summary>
    private async Task RestoreModelAsync(CopilotSession? session, CapybaraSession context)
    {
        if (session is null || !ReferenceEquals(session, _main))
        {
            // Discarded: the rebuilt session is configured from the context, so it must carry the agent's model.
            context.Model = _model;
            return;
        }

        if (context.Model == _model)
        {
            return;
        }

        try
        {
            await session.SetModelAsync(_model, CancellationToken.None);
            context.Model = _model;
        }
#pragma warning disable CA1031 // The next turn retries via its own model check; dropping the session forces a rebuild.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogModelRestoreFailed(_logger, ex, _agent.Id.Value);
            await DropMainSessionAsync();
        }
    }

    private async Task<TurnResult> RunSessionTurnAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        var target = request.Target;
        var sessionId = target.SessionIdFor(_agent.Id);

        // FR-A2A-11: every running non-main session holds a fleet-wide slot.
        using var slot = _sessions.TryAcquire();
        if (slot is null)
        {
            return TurnResult.Throttled($"the fleet-wide limit of {_sessions.Capacity} concurrent non-main sessions is reached");
        }

        using (await _sessionLocks.AcquireAsync(sessionId, cancellationToken))
        {
            var (envelope, creditLimit) = target.Task is { } task
                ? (task.Envelope, Math.Min(task.Envelope.Budget.Credits, _agent.Budget.SessionMaxCredits))
                : TurnEnvelope(request);
            var context = new CapybaraSession(_agent, sessionId, target.Kind, envelope, target.Task?.Id)
            {
                TurnOrigin = request.Origin,
                TurnFrom = request.From,
                RoutineId = request.RoutineId,
                DryRun = request.DryRun,
                Model = request.Model ?? _model,
            };
            var turn = context.BeginTurn(creditLimit);
            _taint.Apply(sessionId, envelope.Taint);
            _taint.Apply(sessionId, request.Taint);

            var session = await OpenAsync(context, cancellationToken);
            try
            {
                var prompt = await _memory.PrepareAsync(context, request.Prompt, cancellationToken);
                using var run = new TurnRun(new CopilotTurnSession(session), steerable: false, Timings);

                // A session whose run could not be stopped is released, not deleted: deleting would lose its history.
                var result = await SendTurnAsync(run, turn, prompt, SourceFor(request.Origin, request.From), () => Task.CompletedTask, cancellationToken);
                if (result.Success)
                {
                    await _memory.IngestAsync(context, request.Prompt, result.Content, cancellationToken);
                }

                return result with { RoutineId = request.RoutineId };
            }
            finally
            {
                // Released, not deleted: the session resumes for its next turn until EndSessionAsync.
                await session.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Sends one turn, stopped by the caller, the fleet kill switch, the turn timeout or the turn's own credit limit.
    /// Whatever stops it, the run is stopped before this returns (<see cref="TurnRun"/>), so the caller's context and
    /// locks are only reset once nothing runs any more; if the abort fails, <paramref name="discardSession"/> throws the
    /// session away. Only cancellation by the caller propagates.
    /// </summary>
    private async Task<TurnResult> SendTurnAsync(TurnRun run, SessionTurnLog turn, string prompt, MessageSource source, Func<Task> discardSession, CancellationToken cancellationToken)
    {
        using var overLimit = new CancellationTokenSource();

        // Off the SDK's event thread: cancelling runs the send's cancellation callbacks.
        using var limitHook = turn.OnLimitExceeded(() => _ = Task.Run(() => TryCancel(overLimit)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _killSwitch.FleetToken, overLimit.Token);
        try
        {
            var reply = await run.SendAsync(new MessageOptions { Prompt = prompt, Source = source }, linked.Token);
            return TurnResult.Completed(reply, turn.Snapshot());
        }
        catch (TurnAbortFailedException ex)
        {
            LogAbortFailed(_logger, ex.AbortFailure, _agent.Id.Value);
            await discardSession();
            cancellationToken.ThrowIfCancellationRequested();
            return await InterruptedAsync(ex.InnerException, turn);
        }
#pragma warning disable CA1031 // The run is already aborted; the failure becomes a result.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return await InterruptedAsync(ex, turn);
        }
    }

    private async Task<TurnResult> InterruptedAsync(Exception? cause, SessionTurnLog turn)
    {
        var evidence = turn.Snapshot();
        if (turn.LimitExceeded)
        {
            var message = $"A turn of {_agent.Id} spent {evidence.Credits:0.##} credits, over its limit of {turn.CreditLimit:0.##}, and was stopped.";
            LogLimitExceeded(_logger, _agent.Id.Value, evidence.Credits, turn.CreditLimit);
            await PublishAsync(new Alert(LimitAlertKind, AlertSeverity.Warning, message, _time.GetUtcNow(), _agent.Id.Value));
            return TurnResult.LimitExceeded(message, evidence);
        }

        if (_killSwitch.IsBlocked(_agent.Id))
        {
            return TurnResult.Blocked("the kill switch stopped the turn", evidence);
        }

        LogTurnInterrupted(_logger, cause, _agent.Id.Value);
        return TurnResult.Failed(cause is null ? "the turn failed" : $"{cause.GetType().Name}: {cause.Message}", evidence);
    }

    private async Task PublishAsync(Alert alert)
    {
        try
        {
            await _alerts.PublishAsync(alert, CancellationToken.None);
        }
#pragma warning disable CA1031 // The turn is already stopped; a failed alert must not turn that into a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAlertFailed(_logger, ex, _agent.Id.Value);
        }
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn already ended.
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.AgentBaseDirectory(_agent.Id));
        Directory.CreateDirectory(_options.AgentWorkingDirectory(_agent.Id));
        _client = new CopilotClient(CopilotClientOptionsFactory.Create(_agent, _options, _logger));
        await _client.StartAsync(cancellationToken);
        LogStarted(_logger, _agent.Id.Value);
    }

    private async Task StopCoreAsync()
    {
        await DropMainSessionAsync();
        if (_client is { } client)
        {
            await client.DisposeAsync();
            _client = null;
        }
    }

    private async Task DropMainSessionAsync()
    {
        if (_main is { } main)
        {
            _main = null;
            await main.DisposeAsync();
        }
    }

    private TurnTimings Timings => new(_options.TurnTimeout, _options.AbortTimeout, _options.IdleGrace, _options.LateRunGrace, _time);

    private Envelope SystemEnvelope() =>
        _envelopes.CreateRoot("system", _agent.Id, new Budget(0.01m, 1, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(1), rootOrigin: MessageOrigin.System);

    /// <summary>
    /// Resumes (or first creates) the main session. A session built while the broker was unreachable lacks its broker
    /// tools, so it is rebuilt before the next turn.
    /// </summary>
    private async Task<CopilotSession> EnsureMainSessionAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (_main is { } existing && _mainSession is { BrokerToolsUnavailable: false })
        {
            return existing;
        }

        await DropMainSessionAsync();
        _mainSession ??= new CapybaraSession(_agent, SessionIds.Main(_agent.Id), SessionKind.Main, envelope) { Model = _model };
        _mainSession.Envelope = envelope;
        _main = await OpenAsync(_mainSession, cancellationToken);
        _mainToolsAtMaxDepth = envelope.IsAtMaxDepth;
        return _main;
    }

    /// <summary>Handlers are not persisted by the SDK, so resume re-registers the full configuration (FR-AGT-03).</summary>
    private async Task<CopilotSession> OpenAsync(CapybaraSession context, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException($"Runtime for {_agent.Id} is not running.");
        try
        {
            var resumed = await client.ResumeSessionAsync(context.SessionId, await _configs.ResumeConfigAsync(context, cancellationToken), cancellationToken);
            LogResumed(_logger, context.SessionId);
            return resumed;
        }
#pragma warning disable CA1031 // A missing session is expected on first use; fall back to create.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogResumeFallback(_logger, context.SessionId, ex.Message);
            return await client.CreateSessionAsync(await _configs.CreateConfigAsync(context, cancellationToken), cancellationToken);
        }
    }

    /// <summary>A main-session turn in flight; user DMs may be steered into it while its run is active (never a dry run).</summary>
    private sealed class InFlightTurn(TurnRun run)
    {
        public TurnRun Run { get; } = run;

        public TaskCompletionSource<TurnResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private MessageSource SourceFor(MessageOrigin origin, AgentId? from) => origin switch
    {
        MessageOrigin.User => MessageSource.User,
        MessageOrigin.Agent => MessageSource.Agent((from ?? _agent.Id).Value),
        _ => MessageSource.System,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Runtime for {Agent} started")]
    private static partial void LogStarted(ILogger logger, string agent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resumed session {SessionId}")]
    private static partial void LogResumed(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not resume {SessionId} ({Reason}); creating it")]
    private static partial void LogResumeFallback(ILogger logger, string sessionId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ping failed for runtime {Agent}")]
    private static partial void LogPingFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Error, Message = "A {Kind} turn of {Agent} failed")]
    private static partial void LogTurnFailed(ILogger logger, Exception exception, string agent, SessionKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Steered a user message into the turn in flight for {Agent}")]
    private static partial void LogSteered(ILogger logger, string agent);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId} was not deleted: {Reason}")]
    private static partial void LogDeleteSkipped(ILogger logger, string sessionId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "A stopped turn of {Agent} could not be aborted; its session is discarded")]
    private static partial void LogAbortFailed(ILogger logger, Exception? exception, string agent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A turn of {Agent} spent {Credits} credits, over its limit of {Limit}; stopped")]
    private static partial void LogLimitExceeded(ILogger logger, string agent, decimal credits, decimal? limit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A turn of {Agent} was interrupted and aborted")]
    private static partial void LogTurnInterrupted(ILogger logger, Exception? exception, string agent);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not publish an alert for {Agent}")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not restore the model of {Agent} after a per-turn override; the main session will be rebuilt")]
    private static partial void LogModelRestoreFailed(ILogger logger, Exception exception, string agent);
}
