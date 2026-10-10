using System.Collections.Concurrent;
using Capybara.Contracts.Routines;
using Capybara.Scheduling.Planning;
using Capybara.Scheduling.Validation;

namespace Capybara.Scheduling.Lifecycle;

/// <summary>Effective state for a routine (FR-RTN-04, FR-RTN-06).</summary>
public sealed record RoutineState(bool Enabled, bool Paused, bool AutoPaused, string? Reason)
{
    public bool Runnable => Enabled && !Paused && !AutoPaused;
}

/// <summary>
/// Human-controlled registry for routine state (FR-RTN-04, FR-RTN-09). It is also the gatekeeper for the FR-RTN-03 limits:
/// whatever the caller, a routine that fires more often than every five minutes, or that would be an agent's 51st, never
/// gets in.
/// </summary>
/// <param name="timeZone">Zone cron expressions are evaluated in (<see cref="Dispatch.SchedulingOptions.TimeZone"/>); UTC when omitted.</param>
public sealed class RoutineRegistry(TimeZoneInfo? timeZone = null)
{
    private readonly ConcurrentDictionary<string, RoutineSpec> _routines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RoutineState> _states = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Raised after any change to the set of routines or their lifecycle state, so the scheduler can resynchronize (FR-RTN-05).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised, before the state changes, when a human enables or resumes a routine. That is a user interaction, so the
    /// auto-pause clock restarts (FR-RTN-06).
    /// </summary>
    public event EventHandler<string>? UserInteraction;

    public TimeZoneInfo TimeZone { get; } = timeZone ?? TimeZoneInfo.Utc;

    public IReadOnlyList<RoutineSpec> Routines => [.. _routines.Values];

    /// <summary>Projected monthly credits for <paramref name="routine"/> in this registry's time zone (FR-RTN-09).</summary>
    public decimal ProjectedMonthlyCredits(RoutineSpec routine) => CostProjection.MonthlyCredits(routine, TimeZone);

    /// <summary>
    /// Adds a routine, or replaces its definition. A new routine starts in the state its spec declares; for an existing
    /// one, the lifecycle state is kept unless the spec's <see cref="RoutineSpec.Enabled"/> flag itself changed, which is
    /// the user editing the config file.
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
            if (previous is null || !_states.ContainsKey(routine.Id))
            {
                _states[routine.Id] = new RoutineState(routine.Enabled, Paused: false, AutoPaused: false, null);
            }
            else if (previous.Enabled != routine.Enabled)
            {
                _states[routine.Id] = GetState(routine.Id) with { Enabled = routine.Enabled, Reason = routine.Enabled ? null : "disabled" };
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a routine (for example one deleted from the config file) and its state.</summary>
    public void Remove(string routineId, RoutinePrincipal principal)
    {
        principal.RequireHuman();
        if (_routines.TryRemove(routineId, out _))
        {
            _states.TryRemove(routineId, out _);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public RoutineSpec? Find(string routineId) => _routines.GetValueOrDefault(routineId);

    public RoutineState GetState(string routineId) => _states.GetValueOrDefault(routineId) ?? new RoutineState(false, false, false, "unknown routine");

    public bool IsRunnable(RoutineSpec routine) => GetState(routine.Id).Runnable;

    public void Enable(string routineId, decimal acknowledgedMonthlyCost, RoutinePrincipal principal)
    {
        principal.RequireHuman();
        var routine = RequireRoutine(routineId);
        var projected = ProjectedMonthlyCredits(routine);
        if (Math.Abs(acknowledgedMonthlyCost - projected) > 0.01m)
        {
            throw new InvalidOperationException($"Projected monthly cost {projected:0.##} credits must be acknowledged before enabling.");
        }

        UserInteraction?.Invoke(this, routineId);
        SetState(routineId, GetState(routineId) with { Enabled = true, Paused = false, AutoPaused = false, Reason = null });
    }

    public void Disable(string routineId, RoutinePrincipal principal)
    {
        principal.RequireHuman();
        SetState(routineId, GetState(routineId) with { Enabled = false, Reason = "disabled" });
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

    private void SetState(string routineId, RoutineState state)
    {
        _ = RequireRoutine(routineId);
        _states[routineId] = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private RoutineSpec RequireRoutine(string routineId) =>
        Find(routineId) ?? throw new KeyNotFoundException($"Routine '{routineId}' is not registered.");
}
