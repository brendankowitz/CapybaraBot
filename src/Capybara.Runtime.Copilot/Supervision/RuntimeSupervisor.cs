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
/// The self-test guard and reconciler are optional only so existing composition keeps compiling; production wires both.
/// </remarks>
public sealed partial class RuntimeSupervisor(
    IEnumerable<ISupervisedRuntime> runtimes,
    IAlertSink alerts,
    SupervisorOptions options,
    TimeProvider time,
    ILogger<RuntimeSupervisor> logger,
    SelfTestGuard? selfTest = null,
    ISessionReconciler? reconciler = null)
{
    private readonly Dictionary<string, State> _states = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<RuntimeHealth> Health
    {
        get
        {
            lock (_gate)
            {
                return [.. runtimes.Select(r => Snapshot(r.Agent.Value, StateFor(r.Agent.Value)))];
            }
        }
    }

    /// <summary>Releases a quarantined runtime so the next check restarts it. Only reachable from the gateway.</summary>
    public void Unquarantine(string agent)
    {
        lock (_gate)
        {
            _states.Remove(agent);
        }
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in runtimes)
        {
            await CheckAsync(runtime, cancellationToken);
        }
    }

    private async Task CheckAsync(ISupervisedRuntime runtime, CancellationToken cancellationToken)
    {
        var agent = runtime.Agent.Value;
        var now = time.GetUtcNow();
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
            }

            return;
        }

        int crashes;
        bool quarantined;
        lock (_gate)
        {
            state.Crashes.Enqueue(now);
            while (state.Crashes.TryPeek(out var oldest) && now - oldest > options.CrashWindow)
            {
                state.Crashes.Dequeue();
            }

            crashes = state.Crashes.Count;
            if (crashes >= options.MaxCrashes)
            {
                state.Quarantined = true;
            }

            quarantined = state.Quarantined;
        }

        if (quarantined)
        {
            LogQuarantined(logger, agent, crashes);
            await alerts.PublishAsync(
                new Alert("runtime.quarantined", AlertSeverity.Critical, $"Runtime for {agent} crashed {crashes} times and was quarantined.", now, agent),
                cancellationToken);
            return;
        }

        LogRestarting(logger, agent, crashes);
        try
        {
            await runtime.RestartAsync(cancellationToken);
            lock (_gate)
            {
                state.ConsecutiveFailures = 0;
                state.NextAttemptAt = null;
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

            LogRestartFailed(logger, ex, agent);
            return;
        }

        await AfterRestartAsync(runtime, cancellationToken);
    }

    /// <summary>Reconcile first, so the user learns what the crash interrupted even if the self-test then pauses the agent.</summary>
    private async Task AfterRestartAsync(ISupervisedRuntime runtime, CancellationToken cancellationToken)
    {
        if (reconciler is not null)
        {
            try
            {
                await reconciler.ReconcileAsync(runtime, cancellationToken);
            }
#pragma warning disable CA1031 // Reconciliation is best effort; the restarted runtime stays supervised either way.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogReconcileFailed(logger, ex, runtime.Agent.Value);
            }
        }

        if (selfTest is not null)
        {
            await selfTest.VerifyAsync(runtime, cancellationToken);
        }
    }

    public TimeSpan Backoff(int consecutiveFailures)
    {
        var factor = Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(options.InitialBackoff.Ticks * factor, options.MaxBackoff.Ticks));
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciling runtime {Agent} after restart failed")]
    private static partial void LogReconcileFailed(ILogger logger, Exception exception, string agent);

    private sealed class State
    {
        public Queue<DateTimeOffset> Crashes { get; } = new();

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? NextAttemptAt { get; set; }

        public bool Quarantined { get; set; }
    }
}
