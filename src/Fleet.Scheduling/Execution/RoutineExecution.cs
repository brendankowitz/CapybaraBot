using Fleet.Contracts.Messaging;
using Fleet.Contracts.Routines;

namespace Fleet.Scheduling.Execution;

/// <summary>Target session for a routine invocation (FR-RTN-01).</summary>
public enum SessionTarget
{
    Main,
    IsolatedTask,
}

/// <summary>Runtime-visible routine trigger (FR-RTN-01, FR-RTN-07).</summary>
public enum RoutineTriggerKind
{
    Heartbeat,
    Cron,
    Event,
}

/// <summary>Status returned by the runtime after executing a routine (FR-RTN-05, FR-OBS-06).</summary>
public enum RoutineExecutionStatus
{
    Succeeded,
    NothingToReport,
    Skipped,
    Refused,
    OverBudget,
    Failed,
}

/// <summary>Size-capped event payload that is always tainted before it reaches the runtime (FR-RTN-07).</summary>
public sealed record RoutineEventPayload(string Source, string EventType, string Json, Taint Taint = Taint.Tainted);

/// <summary>Evidence kept with the last 20 routine runs (FR-OBS-06).</summary>
public sealed record RoutineEvidence(
    IReadOnlyList<string>? Sources = null,
    IReadOnlyList<string>? ActionLog = null,
    IReadOnlyList<string>? UnverifiableItems = null);

/// <summary>Invocation contract implemented by the runtime project (FR-RTN-01, FR-RTN-08).</summary>
public sealed record RoutineInvocation(
    RoutineSpec Routine,
    RoutineTriggerKind TriggerKind,
    DateTimeOffset FireTime,
    string RunId,
    bool DryRun,
    RoutineEventPayload? EventPayload,
    string ModelTier,
    SessionTarget SessionTarget);

/// <summary>Result contract returned by routine executors (FR-RTN-05, FR-OBS-06).</summary>
public sealed record RoutineExecutionResult(
    RoutineExecutionStatus Status,
    string? Summary = null,
    RoutineEvidence? Evidence = null,
    decimal CreditsUsed = 0,
    string? Model = null);

/// <summary>Executes one routine invocation inside the agent runtime (FR-RTN-01).</summary>
public interface IRoutineExecutor
{
    Task<RoutineExecutionResult> ExecuteAsync(RoutineInvocation invocation, CancellationToken cancellationToken);
}
