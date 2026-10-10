using Capybara.Observability.Alerts;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Supervision;

/// <summary>
/// Default <see cref="ISessionReconciler"/>: replays the main session's event log and alerts the user when the last
/// turn was interrupted, naming any tool call whose outcome is unknown. It never re-sends the turn itself, because a
/// half-finished external action must be checked before it is repeated.
/// </summary>
public sealed partial class EventLogReconciler(IAlertSink alerts, TimeProvider time, ILogger<EventLogReconciler> logger) : ISessionReconciler
{
    public const string AlertKind = "runtime.turn_interrupted";

    public async Task<TurnRecovery> ReconcileAsync(ISupervisedRuntime runtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var agent = runtime.Agent.Value;
        if (runtime is not ISessionEventHistory history)
        {
            LogUnavailable(logger, agent);
            return TurnRecovery.Unavailable;
        }

        var recovery = TurnRecovery.Analyze(await history.GetMainSessionEventsAsync(cancellationToken));
        LogReconciled(logger, agent, recovery.State);
        if (recovery.State == TurnRecoveryState.Interrupted)
        {
            // Tool names only: alert payloads stay content-free (FR-OBS-05).
            var tools = recovery.UnfinishedTools.Count == 0
                ? string.Empty
                : $" Unfinished tool calls: {string.Join(", ", recovery.UnfinishedTools)}; verify their effect before retrying.";
            await alerts.PublishAsync(
                new Alert(AlertKind, AlertSeverity.Warning, $"A turn of {agent} was interrupted by a runtime crash.{tools}", time.GetUtcNow(), agent),
                cancellationToken);
        }

        return recovery;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime {Agent} cannot replay its event log; in-flight turns were not reconciled")]
    private static partial void LogUnavailable(ILogger logger, string agent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciled runtime {Agent}: last turn {State}")]
    private static partial void LogReconciled(ILogger logger, string agent, TurnRecoveryState state);
}
