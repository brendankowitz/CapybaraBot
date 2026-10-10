using Fleet.Contracts.Routines;
using Fleet.Scheduling.Execution;

namespace Fleet.Scheduling.Dispatch;

/// <summary>A durable scheduler or event source firing to dispatch (FR-RTN-01, FR-RTN-05).</summary>
public sealed record RoutineFiring(
    RoutineSpec Routine,
    DateTimeOffset FireTime,
    string RunId,
    bool DryRun = false,
    RoutineEventPayload? EventPayload = null,
    bool IsMisfire = false);
