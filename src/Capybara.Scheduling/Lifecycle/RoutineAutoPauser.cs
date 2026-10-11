using System.Collections.Concurrent;
using System.Globalization;
using Capybara.Contracts.Agents;
using Capybara.Observability.Alerts;
using Capybara.Scheduling.Durable;

namespace Capybara.Scheduling.Lifecycle;

/// <summary>
/// Auto-pauses unattended routines after a configurable absence of user interaction (FR-RTN-06). A paused routine
/// stays paused until a human confirms with <see cref="ConfirmResume"/>.
/// <para>
/// "Interaction" is recorded by the gateway, for a human principal only, whenever the user touches a routine or talks to
/// its owner agent (<see cref="RecordUserInteraction(string, RoutinePrincipal)"/>,
/// <see cref="RecordUserInteraction(AgentId, RoutinePrincipal)"/>); a human enabling or resuming a routine through the
/// registry counts as well. A routine nobody has interacted with yet starts its clock the first time it is seen. When
/// <c>sqlitePath</c> is given, the interaction times survive restarts, so frequent restarts cannot keep resetting the clock.
/// </para>
/// </summary>
public sealed class RoutineAutoPauser
{
    private readonly RoutineRegistry _registry;
    private readonly IAlertSink _alerts;
    private readonly TimeProvider _time;
    private readonly int _autoPauseDays;
    private readonly string? _sqlitePath;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastInteraction = new(StringComparer.Ordinal);

    public RoutineAutoPauser(RoutineRegistry registry, IAlertSink alerts, TimeProvider time, int autoPauseDays = 14, string? sqlitePath = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(autoPauseDays, 1);
        _registry = registry;
        _alerts = alerts;
        _time = time;
        _autoPauseDays = autoPauseDays;
        _sqlitePath = sqlitePath;
        if (sqlitePath is not null)
        {
            SchedulingDatabase.Initialize(sqlitePath);
            LoadPersisted(sqlitePath);
        }

        registry.UserInteraction += (_, routineId) => Touch(routineId, _time.GetUtcNow());
    }

    /// <summary>Records that the user interacted with one routine now. Only a human principal can; an agent cannot keep its own routines alive.</summary>
    public void RecordUserInteraction(string routineId, RoutinePrincipal principal)
    {
        principal.RequireHuman();
        Touch(routineId, _time.GetUtcNow());
    }

    /// <summary>Records that the user interacted with an agent, which counts for every routine that agent owns.</summary>
    public void RecordUserInteraction(AgentId owner, RoutinePrincipal principal)
    {
        principal.RequireHuman();
        var now = _time.GetUtcNow();
        foreach (var routine in _registry.Routines.Where(r => r.Owner == owner))
        {
            Touch(routine.Id, now);
        }
    }

    /// <summary>
    /// The confirmation step for resuming an auto-paused routine. The inactivity clock restarts as part of the resume,
    /// before the routine becomes runnable again, so a check cannot pause it straight back.
    /// </summary>
    public void ConfirmResume(string routineId, RoutinePrincipal principal) => _registry.Resume(routineId, principal);

    public async ValueTask CheckAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var cutoff = now - TimeSpan.FromDays(_autoPauseDays);
        foreach (var routine in _registry.Routines)
        {
            var state = _registry.GetState(routine.Id);
            if (!state.Runnable)
            {
                continue;
            }

            var last = _lastInteraction.TryGetValue(routine.Id, out var known) ? known : Touch(routine.Id, now);
            if (last <= cutoff)
            {
                _registry.AutoPause(routine.Id, $"no user interaction since {last:O}");
                await _alerts.PublishAsync(
                    new Alert("routine.autopaused", AlertSeverity.Warning, $"Routine '{routine.Id}' auto-paused after {_autoPauseDays} days without interaction; confirm to resume.", now, routine.Owner.Value),
                    cancellationToken);
            }
        }
    }

    private DateTimeOffset Touch(string routineId, DateTimeOffset at)
    {
        _lastInteraction[routineId] = at;
        if (_sqlitePath is not null)
        {
            using var connection = SchedulingDatabase.Open(_sqlitePath);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO capybara_routine_interactions (routine_id, at) VALUES ($id, $at) ON CONFLICT(routine_id) DO UPDATE SET at = excluded.at";
            command.Parameters.AddWithValue("$id", routineId);
            command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        return at;
    }

    private void LoadPersisted(string sqlitePath)
    {
        using var connection = SchedulingDatabase.Open(sqlitePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT routine_id, at FROM capybara_routine_interactions";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _lastInteraction[reader.GetString(0)] = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
    }
}
