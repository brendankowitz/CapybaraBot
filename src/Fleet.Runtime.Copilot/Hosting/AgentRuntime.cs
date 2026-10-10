using Fleet.Contracts.Agents;
using Fleet.Contracts.Messaging;
using Fleet.Observability.Cost;
using Fleet.Orchestration.Coordination;
using Fleet.Orchestration.Envelopes;
using Fleet.Policy.Provenance;
using Fleet.Policy.Safety;
using Fleet.Runtime.Copilot.Configuration;
using Fleet.Runtime.Copilot.Hooks;
using Fleet.Runtime.Copilot.Sessions;
using Fleet.Runtime.Copilot.Supervision;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;

namespace Fleet.Runtime.Copilot.Hosting;

/// <summary>
/// One agent = one <see cref="CopilotClient"/> = one CLI process with its own <c>BaseDirectory</c> (FR-AGT-01).
/// Owns the agent's resumable main session and runs ephemeral consult sessions.
/// </summary>
public sealed partial class AgentRuntime : ISupervisedRuntime, IAsyncDisposable
{
    private const int MainTurnToolCalls = 200;

    private readonly AgentDefinition _agent;
    private readonly CopilotRuntimeOptions _options;
    private readonly SessionConfigFactory _configs;
    private readonly EnvelopeFactory _envelopes;
    private readonly CostLedger _ledger;
    private readonly KillSwitch _killSwitch;
    private readonly TaintTracker _taint;
    private readonly PermissionAllowances _allowances;
    private readonly ILogger<AgentRuntime> _logger;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private CopilotClient? _client;
    private CopilotSession? _main;
    private FleetSession? _mainSession;

    public AgentRuntime(
        AgentDefinition agent,
        CopilotRuntimeOptions options,
        SessionConfigFactory configs,
        EnvelopeFactory envelopes,
        CostLedger ledger,
        KillSwitch killSwitch,
        TaintTracker taint,
        PermissionAllowances allowances,
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
        _logger = logger;
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

    /// <summary>Runs one turn on the main session, resuming it (and re-registering everything) if needed.</summary>
    public async Task<TurnResult> RunTurnAsync(string prompt, MessageOrigin origin, string principal, string? routineId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (_killSwitch.IsBlocked(_agent.Id))
        {
            return TurnResult.Failed("agent is paused or the fleet kill switch is engaged");
        }

        if (!_ledger.CanSpend(_agent.Id))
        {
            return TurnResult.Failed("budget exhausted (hard stop)");
        }

        await _turnLock.WaitAsync(cancellationToken);
        try
        {
            var envelope = _envelopes.CreateRoot(
                principal,
                _agent.Id,
                new Budget(_agent.Budget.SessionMaxCredits, MainTurnToolCalls, _options.TurnTimeout),
                _options.TurnTimeout);

            var session = await EnsureMainSessionAsync(envelope, cancellationToken);
            _mainSession!.Envelope = envelope;
            _mainSession.TurnOrigin = origin;
            _mainSession.RoutineId = routineId;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _killSwitch.FleetToken);
            var reply = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt, Source = SourceFor(origin) },
                _options.TurnTimeout,
                linked.Token);
            return new TurnResult(true, reply?.Data?.Content, null);
        }
        finally
        {
            _turnLock.Release();
        }
    }

    /// <summary>Answers one <c>ask_agent</c> question in a fresh, read-only, ephemeral session.</summary>
    public async Task<ConsultReply> ConsultAsync(string sessionId, string question, Envelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var client = _client ?? throw new InvalidOperationException($"Runtime for {_agent.Id} is not running.");
        var context = new FleetSession(_agent, sessionId, SessionKind.Consult, envelope) { TurnOrigin = MessageOrigin.Agent };
        _taint.Apply(sessionId, envelope.Taint);

        var caller = envelope.HopPath.Count >= 2 ? envelope.HopPath[^2].Value : "fleet";
        var session = await client.CreateSessionAsync(_configs.CreateConfig(context), cancellationToken);
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
    /// leaking through (FR-AGT-09). Run after every start and resume.
    /// </summary>
    public async Task<SelfTestResult> SelfTestAsync(CancellationToken cancellationToken)
    {
        var envelope = _envelopes.CreateRoot("system", _agent.Id, new Budget(0.01m, 1, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(1));
        var session = await EnsureMainSessionAsync(envelope, cancellationToken);
        var metadata = await session.Rpc.Tools.GetCurrentMetadataAsync(cancellationToken);
        var actual = (metadata.Tools ?? []).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return Compare(SessionConfigFactory.AvailableToolsFor(_mainSession!), actual);
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
        _turnLock.Dispose();
        _lifecycleLock.Dispose();
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
        if (_main is { } main)
        {
            await main.DisposeAsync();
            _main = null;
        }

        if (_client is { } client)
        {
            await client.DisposeAsync();
            _client = null;
        }
    }

    private async Task<CopilotSession> EnsureMainSessionAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (_main is { } existing)
        {
            return existing;
        }

        var client = _client ?? throw new InvalidOperationException($"Runtime for {_agent.Id} is not running.");
        var sessionId = SessionIds.Main(_agent.Id);
        _mainSession = new FleetSession(_agent, sessionId, SessionKind.Main, envelope);
        try
        {
            // Handlers are not persisted by the SDK, so resume re-registers the full configuration.
            _main = await client.ResumeSessionAsync(sessionId, _configs.ResumeConfig(_mainSession), cancellationToken);
            LogResumed(_logger, sessionId);
        }
#pragma warning disable CA1031 // A missing session is expected on first start; fall back to create.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogResumeFallback(_logger, sessionId, ex.Message);
            _main = await client.CreateSessionAsync(_configs.CreateConfig(_mainSession), cancellationToken);
        }

        return _main;
    }

    private MessageSource SourceFor(MessageOrigin origin) => origin switch
    {
        MessageOrigin.User => MessageSource.User,
        MessageOrigin.Agent => MessageSource.Agent(_agent.Id.Value),
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
}
