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

    public IReadOnlyList<RoutineSpec> Routines => [.. _routines.Values];

    public void AddOrUpdate(RoutineSpec routine, RoutinePrincipal principal)
    {
        EnsureHuman(principal);
        _routines[routine.Id] = routine;
        _states.TryAdd(routine.Id, new RoutineState(routine.Enabled, Paused: false, AutoPaused: false, null));
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

        _states[routineId] = GetState(routineId) with { Enabled = true, Paused = false, AutoPaused = false, Reason = null };
    }

    public void Disable(string routineId, RoutinePrincipal principal)
    {
        EnsureHuman(principal);
        _states[routineId] = GetState(routineId) with { Enabled = false, Reason = "disabled" };
    }

    public void Pause(string routineId, string reason)
    {
        _states[routineId] = GetState(routineId) with { Paused = true, Reason = reason };
    }

    public void AutoPause(string routineId, string reason)
    {
        _states[routineId] = GetState(routineId) with { AutoPaused = true, Reason = reason };
    }

    public void Resume(string routineId, RoutinePrincipal principal)
    {
        EnsureHuman(principal);
        _states[routineId] = GetState(routineId) with { Paused = false, AutoPaused = false, Reason = null };
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
