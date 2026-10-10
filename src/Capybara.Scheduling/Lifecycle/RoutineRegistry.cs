using System.Collections.Concurrent;
using Capybara.Contracts.Routines;
using Capybara.Scheduling.Planning;

namespace Capybara.Scheduling.Lifecycle;

/// <summary>Origin of a routine lifecycle request (FR-RTN-04).</summary>
public sealed record RoutinePrincipal(bool IsHuman, string Name)
{
    public static RoutinePrincipal Human(string name) => new(true, name);

    public static RoutinePrincipal Agent(string id) => new(false, id);
}

/// <summary>Effective state for a routine (FR-RTN-04, FR-RTN-06).</summary>
public sealed record RoutineState(bool Enabled, bool Paused, bool AutoPaused, string? Reason)
{
    public bool Runnable => Enabled && !Paused && !AutoPaused;
}

/// <summary>Human-controlled registry for routine state (FR-RTN-04, FR-RTN-09).</summary>
public sealed class RoutineRegistry
{
    private readonly ConcurrentDictionary<string, RoutineSpec> _routines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RoutineState> _states = new(StringComparer.Ordinal);

    /// <summary>Raised after any change to the set of routines or their lifecycle state, so the scheduler can resynchronize (FR-RTN-05).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<RoutineSpec> Routines => [.. _routines.Values];

    /// <summary>
    /// Adds a routine, or replaces its definition. A new routine starts in the state its spec declares; for an existing
    /// one, the lifecycle state is kept unless the spec's <see cref="RoutineSpec.Enabled"/> flag itself changed, which is
    /// the user editing the config file.
    /// </summary>
    public void AddOrUpdate(RoutineSpec routine, RoutinePrincipal principal)
    {
        EnsureHuman(principal);
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

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a routine (for example one deleted from the config file) and its state.</summary>
    public void Remove(string routineId, RoutinePrincipal principal)
    {
        EnsureHuman(principal);
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
        EnsureHuman(principal);
        var routine = RequireRoutine(routineId);
        var projected = CostProjection.MonthlyCredits(routine);
        if (Math.Abs(acknowledgedMonthlyCost - projected) > 0.01m)
        {
            throw new InvalidOperationException($"Projected monthly cost {projected:0.##} credits must be acknowledged before enabling.");
        }

        SetState(routineId, GetState(routineId) with { Enabled = true, Paused = false, AutoPaused = false, Reason = null });
    }

    public void Disable(string routineId, RoutinePrincipal principal)
    {
        EnsureHuman(principal);
        SetState(routineId, GetState(routineId) with { Enabled = false, Reason = "disabled" });
    }

    public void Pause(string routineId, string reason) =>
        SetState(routineId, GetState(routineId) with { Paused = true, Reason = reason });

    public void AutoPause(string routineId, string reason) =>
        SetState(routineId, GetState(routineId) with { AutoPaused = true, Reason = reason });

    public void Resume(string routineId, RoutinePrincipal principal)
    {
        EnsureHuman(principal);
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

    private static void EnsureHuman(RoutinePrincipal principal)
    {
        if (!principal.IsHuman)
        {
            throw new InvalidOperationException("Only a human principal can change routine lifecycle state.");
        }
    }
}
