using Capybara.Observability.Alerts;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Supervision;

/// <summary>
/// Health-checks every runtime with <c>PingAsync</c>, restarts failures with exponential backoff, and quarantines a
/// runtime that keeps crashing, raising an alert instead of looping (FR-AGT-01, FR-AGT-04, FR-OBS-05). After a successful
/// restart it reconciles the interrupted turn from the event log and reruns the tool self-test, which pauses the agent
/// on failure (FR-AGT-09).
/// </summary>
/// <remarks>
/// The self-test guard and reconciler are optional only so composition that predates them keeps compiling. Running
/// without either is a degraded, not fail-closed, configuration, so it is logged as Critical at construction.
/// </remarks>
public sealed partial class RuntimeSupervisor : IDisposable
{
    private readonly IEnumerable<ISupervisedRuntime> _runtimes;
    private readonly IAlertSink _alerts;
    private readonly SupervisorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RuntimeSupervisor> _logger;
    private readonly SelfTestGuard? _selfTest;
    private readonly ISessionReconciler? _reconciler;

    public RuntimeSupervisor(
        IEnumerable<ISupervisedRuntime> runtimes,
        IAlertSink alerts,
        SupervisorOptions options,
        TimeProvider time,
        ILogger<RuntimeSupervisor> logger,
        SelfTestGuard? selfTest = null,
        ISessionReconciler? reconciler = null)
    {
        _runtimes = runtimes;
        _alerts = alerts;
        _options = options;
        _time = time;
        _logger = logger;
        _selfTest = selfTest;
        _reconciler = reconciler;

        if (selfTest is null)
        {
            LogDegraded(logger, "self-test rerun after restart is disabled; a restarted runtime with the wrong tools keeps running");
        }

        if (reconciler is null)
        {
            LogDegraded(logger, "reconciliation after restart is disabled; turns interrupted by a crash go unreported");
        }
    }
    private readonly Dictionary<string, State> _states = [];
    private readonly Lock _gate = new();

    // Serializes health checks with suspension, so a restart already under way can never outlive a suspend.
    private readonly SemaphoreSlim _checks = new(1, 1);
    private bool _suspended;

    /// <summary>Whether <see cref="SuspendAsync"/> stopped the runtimes and checks are off until <see cref="Resume"/>.</summary>
    public bool IsSuspended
    {
        get
        {
            lock (_gate)
            {
                return _suspended;
            }
        }
    }

    public IReadOnlyList<RuntimeHealth> Health
    {
        get
        {
            lock (_gate)
            {
                return [.. _runtimes.Select(r => Snapshot(r.Agent.Value, StateFor(r.Agent.Value)))];
            }
        }
    }

    /// <summary>
    /// Releases a quarantined runtime so the next check restarts it. Only reachable from the gateway. A runtime the
    /// kill switch stopped stays marked as such, so its restart after release is still not counted as a crash.
    /// </summary>
    public void Unquarantine(string agent)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(agent, out var state))
            {
                state.Quarantined = false;
                state.Crashes.Clear();
                state.ConsecutiveFailures = 0;
                state.NextAttemptAt = null;
            }
        }
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        await _checks.WaitAsync(cancellationToken);
        try
        {
            foreach (var runtime in _runtimes)
            {
                if (IsSuspended)
                {
                    return;
                }

                await CheckAsync(runtime, cancellationToken);
            }
        }
        finally
        {
            _checks.Release();
        }
    }

    /// <summary>
    /// Turns health checks off at once without waiting or stopping anything, so a check in progress restarts nothing
    /// more. Call it synchronously when the kill switch engages; <see cref="SuspendAsync"/> then stops the runtimes.
    /// </summary>
    public void MarkSuspended()
    {
        lock (_gate)
        {
            _suspended = true;
        }
    }

    /// <summary>
    /// Stops every runtime's process and turns health checks off, so nothing restarts them (FR-SAF-06 kill switch).
    /// Checks are turned off at once, so a check in progress restarts nothing more; that check is then waited for, and
    /// every runtime (including one it may just have restarted) is stopped. A runtime that fails to stop is logged and
    /// the rest are still stopped. Idempotent.
    /// </summary>
    public async Task SuspendAsync(CancellationToken cancellationToken)
    {
        MarkSuspended();

        await _checks.WaitAsync(cancellationToken);
        try
        {
            foreach (var runtime in _runtimes)
            {
                lock (_gate)
                {
                    StateFor(runtime.Agent.Value).Stopped = true;
                }

                try
                {
                    await runtime.StopAsync(cancellationToken);
                }
#pragma warning disable CA1031 // Stop the rest regardless.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogStopFailed(_logger, ex, runtime.Agent.Value);
                }
            }
        }
        finally
        {
            _checks.Release();
        }
    }

    /// <summary>
    /// Turns health checks back on. The next check restarts each runtime <see cref="SuspendAsync"/> stopped, without
    /// counting it as a crash, then reconciles and self-tests it like any restart.
    /// </summary>
    public void Resume()
    {
        lock (_gate)
        {
            _suspended = false;
        }
    }

    private async Task CheckAsync(ISupervisedRuntime runtime, CancellationToken cancellationToken)
    {
        var agent = runtime.Agent.Value;
        var now = _time.GetUtcNow();
        State state;
        lock (_gate)
        {
            state = StateFor(agent);
            if (state.Quarantined || (state.NextAttemptAt is { } next && now < next))
            {
                return;
            }
        }

        bool healthy;
        try
        {
            healthy = await runtime.PingAsync(cancellationToken);
        }
#pragma warning disable CA1031 // Any ping failure is a health failure.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            healthy = false;
        }

        if (healthy)
        {
            lock (_gate)
            {
                state.ConsecutiveFailures = 0;
                state.NextAttemptAt = null;
                state.Stopped = false;
            }

            return;
        }

        // Suspended while this runtime was being pinged: restarting it now would relaunch a CLI the kill switch stopped.
        if (IsSuspended)
        {
            return;
        }

        int crashes;
        bool quarantined;
        bool deliberate;
        lock (_gate)
        {
            // A runtime stopped by SuspendAsync is down on purpose: bring it back without counting a crash.
            deliberate = state.Stopped;
            if (!deliberate)
            {
                state.Crashes.Enqueue(now);
            }

            while (state.Crashes.TryPeek(out var oldest) && now - oldest > _options.CrashWindow)
            {
                state.Crashes.Dequeue();
            }

            crashes = state.Crashes.Count;
            if (!deliberate && crashes >= _options.MaxCrashes)
            {
                state.Quarantined = true;
            }

            quarantined = state.Quarantined;
        }

        if (quarantined)
        {
            LogQuarantined(_logger, agent, crashes);
            await _alerts.PublishAsync(
                new Alert("runtime.quarantined", AlertSeverity.Critical, $"Runtime for {agent} crashed {crashes} times and was quarantined.", now, agent),
                cancellationToken);
            return;
        }

        LogRestarting(_logger, agent, crashes);
        try
        {
            await runtime.RestartAsync(cancellationToken);
            lock (_gate)
            {
                state.ConsecutiveFailures = 0;
                state.NextAttemptAt = null;
                state.Stopped = false;
            }
        }
#pragma warning disable CA1031 // A failed restart backs off; it must not stop supervision of other agents.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            lock (_gate)
            {
                state.ConsecutiveFailures++;
                state.NextAttemptAt = now + Backoff(state.ConsecutiveFailures);
            }

            LogRestartFailed(_logger, ex, agent);
            return;
        }

        // Suspended while it restarted: the suspension stops it again, so neither reconcile nor self-test it now.
        if (IsSuspended)
        {
            return;
        }

        await AfterRestartAsync(runtime, cancellationToken);
    }

    /// <summary>Reconcile first, so the user learns what the crash interrupted even if the self-test then pauses the agent.</summary>
    private async Task AfterRestartAsync(ISupervisedRuntime runtime, CancellationToken cancellationToken)
    {
        if (_reconciler is not null)
        {
            try
            {
                await _reconciler.ReconcileAsync(runtime, cancellationToken);
            }
#pragma warning disable CA1031 // Reconciliation is best effort; the restarted runtime stays supervised either way.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogReconcileFailed(_logger, ex, runtime.Agent.Value);
            }
        }

        if (_selfTest is not null)
        {
            await _selfTest.VerifyAsync(runtime, cancellationToken);
        }
    }

    /// <summary>
    /// Re-runs the tool self-test after an edited definition reached <paramref name="runtime"/> (FR-AGT-06, FR-AGT-09); the
    /// guard pauses the agent if its tools no longer match. The runtime replaced its own CLI if the edit needed that.
    /// Serialized with health checks. While suspended (kill switch) nothing runs: the release restarts and self-tests every
    /// runtime anyway. A runtime that is down is left to the next health check, which restarts and self-tests it.
    /// </summary>
    public async Task VerifyDefinitionChangeAsync(ISupervisedRuntime runtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        await _checks.WaitAsync(cancellationToken);
        try
        {
            if (IsSuspended || _selfTest is null || !await runtime.PingAsync(cancellationToken))
            {
                return;
            }

            await _selfTest.VerifyAsync(runtime, cancellationToken);
        }
        finally
        {
            _checks.Release();
        }
    }

    public void Dispose() => _checks.Dispose();

    public TimeSpan Backoff(int consecutiveFailures)
    {
        var factor = Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(_options.InitialBackoff.Ticks * factor, _options.MaxBackoff.Ticks));
        return delay;
    }

    private State StateFor(string agent)
    {
        if (!_states.TryGetValue(agent, out var state))
        {
            state = new State();
            _states[agent] = state;
        }

        return state;
    }

    private static RuntimeHealth Snapshot(string agent, State state) => new(
        agent,
        state.Quarantined ? RuntimeHealthState.Quarantined : state.NextAttemptAt is null ? RuntimeHealthState.Healthy : RuntimeHealthState.BackingOff,
        state.Crashes.Count,
        state.NextAttemptAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime {Agent} unhealthy ({Crashes} recent crashes); restarting")]
    private static partial void LogRestarting(ILogger logger, string agent, int crashes);

    [LoggerMessage(Level = LogLevel.Error, Message = "Restart of runtime {Agent} failed")]
    private static partial void LogRestartFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Runtime {Agent} quarantined after {Crashes} crashes")]
    private static partial void LogQuarantined(ILogger logger, string agent, int crashes);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Runtime supervisor is degraded: {Problem}")]
    private static partial void LogDegraded(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciling runtime {Agent} after restart failed")]
    private static partial void LogReconcileFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stopping runtime {Agent} for suspension failed")]
    private static partial void LogStopFailed(ILogger logger, Exception exception, string agent);

    private sealed class State
    {
        public Queue<DateTimeOffset> Crashes { get; } = new();

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? NextAttemptAt { get; set; }

        public bool Quarantined { get; set; }

        /// <summary>Stopped by <see cref="SuspendAsync"/> and not yet restarted.</summary>
        public bool Stopped { get; set; }
    }
}
