using Fleet.Observability.Alerts;

namespace Fleet.Scheduling.Lifecycle;

/// <summary>Auto-pauses unattended routines after a configurable absence (FR-RTN-06).</summary>
public sealed class RoutineAutoPauser(RoutineRegistry registry, IAlertSink alerts, TimeProvider time, int autoPauseDays = 14)
{
    private readonly Dictionary<string, DateTimeOffset> _lastInteraction = new(StringComparer.Ordinal);

    public void RecordUserInteraction(string routineId) => _lastInteraction[routineId] = time.GetUtcNow();

    public async ValueTask CheckAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(autoPauseDays);
        foreach (var routine in registry.Routines)
        {
            var last = _lastInteraction.GetValueOrDefault(routine.Id, routine.Enabled ? time.GetUtcNow() : DateTimeOffset.MaxValue);
            if (last <= cutoff && registry.GetState(routine.Id).Runnable)
            {
                registry.AutoPause(routine.Id, $"no user interaction since {last:O}");
                await alerts.PublishAsync(new Alert("routine.autopaused", AlertSeverity.Warning, $"Routine '{routine.Id}' auto-paused; confirm to resume.", time.GetUtcNow(), routine.Owner), cancellationToken);
            }
        }
    }
}
