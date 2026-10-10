using System.Collections.Concurrent;
using System.Globalization;
using Capybara.Contracts.Routines;
using Capybara.Scheduling.Planning;
using Capybara.Scheduling.Validation;

namespace Capybara.Scheduling.Lifecycle;

/// <summary>
/// Effective state for a routine (FR-RTN-04, FR-RTN-06). <paramref name="AcknowledgedMonthlyCredits"/> is the projected
/// cost the user accepted when they last enabled it from a client (FR-RTN-09), or <see langword="null"/> when its
/// enabled state comes from the config file.
/// </summary>
public sealed record RoutineState(bool Enabled, bool Paused, bool AutoPaused, string? Reason, decimal? AcknowledgedMonthlyCredits = null)
{
    public bool Runnable => Enabled && !Paused && !AutoPaused;
}

/// <summary>
/// Human-controlled registry for routine state (FR-RTN-04, FR-RTN-09). It is also the gatekeeper for the FR-RTN-03 limits:
/// whatever the caller, a routine that fires more often than every five minutes, or that would be an agent's 51st, never
/// gets in.
/// </summary>
/// <remarks>
/// <para>
/// The user's enable and disable decisions are overrides of the config file's <c>enabled</c> flag. With a SQLite path
/// they survive restarts: when the config load adds a routine, its override is re-applied, unless the file's flag has
/// been edited since the decision (the file then wins and the override is dropped), or the routine now projects more
/// than the cost the user acknowledged when enabling it (it then starts disabled until re-enabled).
/// </para>
/// <para>
/// The same cost rule applies to a running routine whose definition is replaced: one enabled on an acknowledgement
/// stops when its new definition projects more, so an approved edit cannot raise what it bills unseen.
/// </para>
/// </remarks>
public sealed class RoutineRegistry
{
    /// <summary>Tolerance when comparing projected costs, matching the acknowledgement check.</summary>
    private const decimal CostTolerance = 0.01m;

    private readonly ConcurrentDictionary<string, RoutineSpec> _routines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RoutineState> _states = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly RoutineOverrideStore? _store;
    private readonly Dictionary<string, RoutineOverride> _overrides;

    /// <param name="timeZone">Zone cron expressions are evaluated in (<see cref="Dispatch.SchedulingOptions.TimeZone"/>); UTC when omitted.</param>
    /// <param name="sqlitePath">Where the user's enable/disable decisions are kept; in memory only when omitted.</param>
    public RoutineRegistry(TimeZoneInfo? timeZone = null, string? sqlitePath = null)
    {
        TimeZone = timeZone ?? TimeZoneInfo.Utc;
        _store = sqlitePath is null ? null : new RoutineOverrideStore(sqlitePath);
        _overrides = _store?.Load() ?? new Dictionary<string, RoutineOverride>(StringComparer.Ordinal);
    }

    /// <summary>Raised after any change to the set of routines or their lifecycle state, so the scheduler can resynchronize (FR-RTN-05).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised, before the state changes, when a human enables or resumes a routine. That is a user interaction, so the
    /// auto-pause clock restarts (FR-RTN-06).
    /// </summary>
    public event EventHandler<string>? UserInteraction;

    public TimeZoneInfo TimeZone { get; }

    public IReadOnlyList<RoutineSpec> Routines => [.. _routines.Values];

    /// <summary>Projected monthly credits for <paramref name="routine"/> in this registry's time zone (FR-RTN-09).</summary>
    public decimal ProjectedMonthlyCredits(RoutineSpec routine) => CostProjection.MonthlyCredits(routine, TimeZone);

    /// <summary>
    /// Adds a routine, or replaces its definition. A new routine starts in the state its spec declares, or the user's
    /// persisted decision for it (see remarks); for an existing one, the lifecycle state is kept unless the spec's
    /// <see cref="RoutineSpec.Enabled"/> flag itself changed, which is the user editing the config file, or the new
    /// definition projects more than the cost the user acknowledged.
    /// </summary>
    /// <exception cref="RoutineValidationException">The routine is invalid, a cron fires more often than every five minutes, or its owner would exceed 50 routines.</exception>
    public void AddOrUpdate(RoutineSpec routine, RoutinePrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(routine);
        principal.RequireHuman();
        lock (_gate)
        {
            var errors = RoutineValidator.ValidateRoutine(routine, TimeZone).ToList();
            var owned = _routines.Values.Count(r => r.Owner == routine.Owner && !string.Equals(r.Id, routine.Id, StringComparison.Ordinal));
            if (owned >= RoutineLimits.MaxRoutinesPerAgent)
            {
                errors.Add($"{routine.Owner} already has {owned} routines; the limit is {RoutineLimits.MaxRoutinesPerAgent}.");
            }

            if (errors.Count > 0)
            {
                throw new RoutineValidationException(errors);
            }

            var previous = _routines.TryGetValue(routine.Id, out var existing) ? existing : null;
            _routines[routine.Id] = routine;
            if (previous is null || !_states.TryGetValue(routine.Id, out var state))
            {
                _states[routine.Id] = InitialState(routine);
            }
            else if (previous.Enabled != routine.Enabled)
            {
                // The config file's flag was edited: it wins over the user's earlier decision.
                DeleteOverride(routine.Id);
                _states[routine.Id] = state with { Enabled = routine.Enabled, Reason = routine.Enabled ? null : "disabled", AcknowledgedMonthlyCredits = null };
            }
            else if (state.Enabled && ExceedsAcknowledgement(routine, state.AcknowledgedMonthlyCredits) is { } reason)
            {
                SaveOverride(routine.Id, new RoutineOverride(Enabled: false, null, routine.Enabled));
                _states[routine.Id] = state with { Enabled = false, Reason = reason, AcknowledgedMonthlyCredits = null };
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a routine (for example one deleted from the config file), its state and the user's decision for it.</summary>
    public void Remove(string routineId, RoutinePrincipal principal)
    {
        principal.RequireHuman();
        bool removed;
        lock (_gate)
        {
            removed = _routines.TryRemove(routineId, out _);
            if (removed)
            {
                _states.TryRemove(routineId, out _);
                DeleteOverride(routineId);
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public RoutineSpec? Find(string routineId) => _routines.GetValueOrDefault(routineId);

    public RoutineState GetState(string routineId) => _states.GetValueOrDefault(routineId) ?? new RoutineState(false, false, false, "unknown routine");

    public bool IsRunnable(RoutineSpec routine) => GetState(routine.Id).Runnable;

    /// <summary>Enables a routine once the user acknowledged its projected monthly cost (FR-RTN-09); survives restarts.</summary>
    public void Enable(string routineId, decimal acknowledgedMonthlyCost, RoutinePrincipal principal)
    {
        principal.RequireHuman();
        var routine = RequireRoutine(routineId);
        var projected = ProjectedMonthlyCredits(routine);
        if (Math.Abs(acknowledgedMonthlyCost - projected) > CostTolerance)
        {
            throw new InvalidOperationException($"Projected monthly cost {projected:0.##} credits must be acknowledged before enabling.");
        }

        UserInteraction?.Invoke(this, routineId);
        Decide(routineId, state => state with { Enabled = true, Paused = false, AutoPaused = false, Reason = null, AcknowledgedMonthlyCredits = acknowledgedMonthlyCost });
    }

    /// <summary>Disables a routine; survives restarts until the user enables it or edits its <c>enabled</c> flag.</summary>
    public void Disable(string routineId, RoutinePrincipal principal)
    {
        principal.RequireHuman();
        Decide(routineId, state => state with { Enabled = false, Reason = "disabled", AcknowledgedMonthlyCredits = null });
    }

    public void Pause(string routineId, string reason) =>
        SetState(routineId, GetState(routineId) with { Paused = true, Reason = reason });

    public void AutoPause(string routineId, string reason) =>
        SetState(routineId, GetState(routineId) with { AutoPaused = true, Reason = reason });

    public void Resume(string routineId, RoutinePrincipal principal)
    {
        principal.RequireHuman();
        _ = RequireRoutine(routineId);
        UserInteraction?.Invoke(this, routineId);
        SetState(routineId, GetState(routineId) with { Paused = false, AutoPaused = false, Reason = null });
    }

    /// <summary>A routine the config load adds: the file's flag, or the user's persisted decision if it still holds.</summary>
    private RoutineState InitialState(RoutineSpec routine)
    {
        var fromSpec = new RoutineState(routine.Enabled, Paused: false, AutoPaused: false, routine.Enabled ? null : "disabled");
        if (!_overrides.TryGetValue(routine.Id, out var decision))
        {
            return fromSpec;
        }

        if (decision.SpecEnabled != routine.Enabled)
        {
            DeleteOverride(routine.Id);
            return fromSpec;
        }

        if (!decision.Enabled)
        {
            return fromSpec with { Enabled = false, Reason = "disabled" };
        }

        // An enable is only as good as the cost it acknowledged; a definition that grew needs a new acknowledgement.
        return ExceedsAcknowledgement(routine, decision.AcknowledgedMonthlyCredits) is { } reason
            ? fromSpec with { Enabled = false, Reason = reason }
            : fromSpec with { Enabled = true, Reason = null, AcknowledgedMonthlyCredits = decision.AcknowledgedMonthlyCredits };
    }

    private string? ExceedsAcknowledgement(RoutineSpec routine, decimal? acknowledged)
    {
        if (acknowledged is not { } ack)
        {
            return null;
        }

        var projected = ProjectedMonthlyCredits(routine);
        return projected > ack + CostTolerance
            ? string.Create(CultureInfo.InvariantCulture, $"projected monthly cost rose from {ack:0.##} to {projected:0.##} credits; re-enable to acknowledge it")
            : null;
    }

    /// <summary>A human enable or disable: the new state and its persisted override change together.</summary>
    private void Decide(string routineId, Func<RoutineState, RoutineState> change)
    {
        lock (_gate)
        {
            var routine = RequireRoutine(routineId);
            var state = change(GetState(routineId));
            SaveOverride(routineId, new RoutineOverride(state.Enabled, state.AcknowledgedMonthlyCredits, routine.Enabled));
            _states[routineId] = state;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(string routineId, RoutineState state)
    {
        _ = RequireRoutine(routineId);
        _states[routineId] = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SaveOverride(string routineId, RoutineOverride value)
    {
        _store?.Save(routineId, value);
        _overrides[routineId] = value;
    }

    private void DeleteOverride(string routineId)
    {
        if (_overrides.Remove(routineId))
        {
            _store?.Delete(routineId);
        }
    }

    private RoutineSpec RequireRoutine(string routineId) =>
        Find(routineId) ?? throw new KeyNotFoundException($"Routine '{routineId}' is not registered.");
}
