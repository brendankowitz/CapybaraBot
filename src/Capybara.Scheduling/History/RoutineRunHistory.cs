using System.Collections.Concurrent;
using Capybara.Observability.Alerts;
using Capybara.Scheduling.Validation;

namespace Capybara.Scheduling.History;

/// <summary>
/// Keeps the last 20 runs per routine and raises a visible alert for every run that did not succeed —
/// nothing fails silently (FR-RTN-05).
/// </summary>
public sealed class RoutineRunHistory(IAlertSink alerts)
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RoutineRun>> _runs = new(StringComparer.Ordinal);

    public async ValueTask RecordAsync(RoutineRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var queue = _runs.GetOrAdd(run.RoutineId, static _ => new ConcurrentQueue<RoutineRun>());
        queue.Enqueue(run);
        while (queue.Count > RoutineLimits.RunHistoryDepth && queue.TryDequeue(out _))
        {
        }

        if (run.Outcome != RoutineRunOutcome.Succeeded)
        {
            await alerts.PublishAsync(
                new Alert(
                    $"routine.{run.Outcome.ToString().ToLowerInvariant()}",
                    run.Outcome == RoutineRunOutcome.Failed ? AlertSeverity.Critical : AlertSeverity.Warning,
                    $"Routine '{run.RoutineId}' {run.Outcome}: {run.Summary ?? "no detail"}",
                    run.StartedAt),
                cancellationToken);
        }
    }

    public IReadOnlyList<RoutineRun> Get(string routineId) =>
        _runs.TryGetValue(routineId, out var queue) ? [.. queue] : [];
}
