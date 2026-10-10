using System.Collections.Concurrent;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
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
/// A user DM steered into a turn in flight (<see cref="SteerAsync"/>) uses <c>immediate</c> mode. If the turn finishes
/// at the same instant, the CLI queues the message as the next run of the same session, so turns still never overlap.
/// </para>
/// </remarks>
public sealed partial class AgentRuntime : IAgentRuntime, ISupervisedRuntime, ISessionEventHistory
{
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
    private readonly TimeProvider _time;
    private readonly ILogger<AgentRuntime> _logger;
    private readonly SemaphoreSlim _mainLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new(StringComparer.Ordinal);

    private CopilotClient? _client;
    private CopilotSession? _main;
    private CapybaraSession? _mainSession;
    private string _model;
    private int _mainTurnInFlight;

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

    public async Task<bool> SteerAsync(string prompt, MessageOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (Volatile.Read(ref _mainTurnInFlight) == 0 || _main is not { } session)
        {
            return false;
        }

        await session.SendAsync(new MessageOptions { Prompt = prompt, Source = SourceFor(origin, null), Mode = ImmediateMode }, cancellationToken);
        LogSteered(_logger, _agent.Id.Value);
        return true;
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
        var gate = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
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
        finally
        {
            gate.Release();
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
            var actual = await ToolMetadataPoller.WaitForToolsAsync(
                async ct => (await session.Rpc.Tools.GetCurrentMetadataAsync(ct)).Tools?.Select(t => t.Name).ToList(),
                _options.SelfTestToolsTimeout,
                _options.SelfTestPollInterval,
                _time,
                cancellationToken);
            return Compare(SessionConfigFactory.AvailableToolsFor(_mainSession!), actual.ToHashSet(StringComparer.Ordinal));
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
        foreach (var gate in _sessionLocks.Values)
        {
            gate.Dispose();
        }
    }

    private async Task<TurnResult> RunMainTurnAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        await _mainLock.WaitAsync(cancellationToken);
        CapybaraSession? context = null;
        CopilotSession? session = null;
        try
        {
            var budget = request.Budget ?? new Budget(_agent.Budget.SessionMaxCredits, MainTurnToolCalls, _options.TurnTimeout);
            var envelope = _envelopes.CreateRoot(request.Principal, _agent.Id, budget, _options.TurnTimeout, request.RootTaskId);
            session = await EnsureMainSessionAsync(envelope, cancellationToken);
            context = _mainSession!;
            context.Envelope = envelope;
            context.TurnOrigin = request.Origin;
            context.RoutineId = request.RoutineId;
            context.DryRun = request.DryRun;
            var turn = context.BeginTurn();
            _taint.Apply(context.SessionId, request.Taint);

            if (request.Model is { } model && model != context.Model)
            {
                await session.SetModelAsync(model, cancellationToken);
                context.Model = model;
            }

            var prompt = await _memory.PrepareAsync(context, request.Prompt, cancellationToken);
            Volatile.Write(ref _mainTurnInFlight, 1);
            string? reply;
            try
            {
                reply = await SendAsync(session, prompt, SourceFor(request.Origin, request.From), cancellationToken);
            }
            finally
            {
                Volatile.Write(ref _mainTurnInFlight, 0);
            }

            await _memory.IngestAsync(context, request.Prompt, reply, cancellationToken);
            return TurnResult.Completed(reply, turn.Snapshot());
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

    /// <summary>A per-turn model override (a routine's model) does not outlive its turn.</summary>
    private async Task RestoreModelAsync(CopilotSession? session, CapybaraSession context)
    {
        if (session is null || context.Model == _model)
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
        var gate = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var envelope = target.Task?.Envelope
                ?? _envelopes.CreateRoot(request.Principal, _agent.Id, request.Budget ?? new Budget(_agent.Budget.SessionMaxCredits, MainTurnToolCalls, _options.TurnTimeout), _options.TurnTimeout, request.RootTaskId);
            var context = new CapybaraSession(_agent, sessionId, target.Kind, envelope, target.Task?.Id)
            {
                TurnOrigin = request.Origin,
                RoutineId = request.RoutineId,
                DryRun = request.DryRun,
                Model = request.Model ?? _model,
            };
            var turn = context.BeginTurn();
            _taint.Apply(sessionId, envelope.Taint);
            _taint.Apply(sessionId, request.Taint);

            var session = await OpenAsync(context, cancellationToken);
            try
            {
                var prompt = await _memory.PrepareAsync(context, request.Prompt, cancellationToken);
                var reply = await SendAsync(session, prompt, SourceFor(request.Origin, request.From), cancellationToken);
                await _memory.IngestAsync(context, request.Prompt, reply, cancellationToken);
                return TurnResult.Completed(reply, turn.Snapshot());
            }
            finally
            {
                // Released, not deleted: the session resumes for its next turn until EndSessionAsync.
                await session.DisposeAsync();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> SendAsync(CopilotSession session, string prompt, MessageSource source, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _killSwitch.FleetToken);
        var reply = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt, Source = source }, _options.TurnTimeout, linked.Token);
        return reply?.Data?.Content;
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

    private Envelope SystemEnvelope() =>
        _envelopes.CreateRoot("system", _agent.Id, new Budget(0.01m, 1, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(1));

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not restore the model of {Agent} after a per-turn override; the main session will be rebuilt")]
    private static partial void LogModelRestoreFailed(ILogger logger, Exception exception, string agent);
}
