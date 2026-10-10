using System.Text.Json;
using Capybara.Observability.Alerts;
using Capybara.Runtime.Copilot.Configuration;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Supervision;

/// <summary>
/// Default <see cref="ISessionReconciler"/>: replays the main session's event log and alerts the user when the last
/// turn was interrupted or left tool calls unfinished (aborted turns included), naming those tools. It never re-sends the
/// turn itself, because a half-finished external action must be checked before it is repeated.
/// </summary>
/// <remarks>
/// Every start reconciles, so the same interrupted turn would otherwise be reported after each restart. Alerted turns
/// are remembered per agent in <c>{StateRoot}/agents/{id}/host/reconciled-turns.json</c> (the newest
/// <see cref="RememberedTurns"/> ids). If that file cannot be written, the ids are still kept in memory for the life of
/// the process and the failure is logged. Thread-safe.
/// </remarks>
public sealed partial class EventLogReconciler(
    IAlertSink alerts,
    TimeProvider time,
    CopilotRuntimeOptions options,
    ILogger<EventLogReconciler> logger) : ISessionReconciler
{
    public const string AlertKind = "runtime.turn_interrupted";

    public const string FileName = "reconciled-turns.json";

    internal const int RememberedTurns = 32;

    private readonly Dictionary<string, List<string>> _alerted = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

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
        if (!recovery.NeedsAttention || WasAlerted(agent, recovery.InteractionId!))
        {
            return recovery;
        }

        // Tool names only: alert payloads stay content-free (FR-OBS-05).
        var what = recovery.State == TurnRecoveryState.Interrupted
            ? $"A turn of {agent} was interrupted by a runtime crash."
            : $"A turn of {agent} ended ({recovery.State}) with tool calls that never reported completion.";
        var tools = recovery.UnfinishedTools.Count == 0
            ? string.Empty
            : $" Unfinished tool calls: {string.Join(", ", recovery.UnfinishedTools)}; verify their effect before retrying.";
        await alerts.PublishAsync(new Alert(AlertKind, AlertSeverity.Warning, what + tools, time.GetUtcNow(), agent), cancellationToken);

        // Recorded only after publishing: a failed publish is retried on the next start rather than lost.
        Remember(agent, recovery.InteractionId!);
        return recovery;
    }

    private bool WasAlerted(string agent, string interactionId)
    {
        lock (_gate)
        {
            return IdsFor(agent).Contains(interactionId, StringComparer.Ordinal);
        }
    }

    private void Remember(string agent, string interactionId)
    {
        lock (_gate)
        {
            var ids = IdsFor(agent);
            if (ids.Contains(interactionId, StringComparer.Ordinal))
            {
                return;
            }

            ids.Add(interactionId);
            if (ids.Count > RememberedTurns)
            {
                ids.RemoveRange(0, ids.Count - RememberedTurns);
            }

            Save(agent, ids);
        }
    }

    private List<string> IdsFor(string agent)
    {
        if (!_alerted.TryGetValue(agent, out var ids))
        {
            ids = Load(agent);
            _alerted[agent] = ids;
        }

        return ids;
    }
    private string PathFor(string agent) => Path.Combine(options.AgentStateDirectory(agent), FileName);

    private List<string> Load(string agent)
    {
        var path = PathFor(agent);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable history only risks a repeated alert, never a missed one.
            LogStateUnavailable(logger, ex, path);
            return [];
        }
    }

    private void Save(string agent, List<string> ids)
    {
        var path = PathFor(agent);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(ids));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStateUnavailable(logger, ex, path);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime {Agent} cannot replay its event log; in-flight turns were not reconciled")]
    private static partial void LogUnavailable(ILogger logger, string agent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciled runtime {Agent}: last turn {State}")]
    private static partial void LogReconciled(ILogger logger, string agent, TurnRecoveryState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciled-turn state at {Path} is unavailable; keeping it in memory")]
    private static partial void LogStateUnavailable(ILogger logger, Exception exception, string path);
}
