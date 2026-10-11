using Capybara.Contracts.Messaging;
using Capybara.Contracts.Routines;

namespace Capybara.Scheduling.Execution;

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

/// <summary>
/// Evidence kept with the last 20 routine runs (FR-OBS-06): what the run read (<c>Sources</c>), did (<c>ActionLog</c>)
/// and could not verify (<c>UnverifiableItems</c>), plus the tools that needed an approval and were auto-denied because
/// the run was a dry run (<c>DeniedApprovals</c>, FR-RTN-08), so the user can see what the routine would have asked for.
/// </summary>
public sealed record RoutineEvidence(
    IReadOnlyList<string>? Sources = null,
    IReadOnlyList<string>? ActionLog = null,
    IReadOnlyList<string>? UnverifiableItems = null,
    IReadOnlyList<string>? DeniedApprovals = null);

/// <summary>
/// Invocation contract implemented by the runtime project (FR-RTN-01, FR-RTN-08). A <c>DryRun</c> invocation is a test:
/// the executor MUST auto-deny every approval-required tool call without prompting anyone, must not deliver output to the
/// routine's delivery target, and should list what was denied in <see cref="RoutineEvidence.DeniedApprovals"/>.
/// <c>EventPayload</c> is present for event triggers and is always tainted.
/// </summary>
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
/// <remarks>
/// <c>CreditsLedgered</c> means the executor already wrote <c>CreditsUsed</c> to the cost ledger as it was spent (so the
/// hard stop saw it during the run); the dispatcher then keeps the credits for history only and must not record them
/// again. Otherwise the dispatcher records them once.
/// </remarks>
public sealed record RoutineExecutionResult(
    RoutineExecutionStatus Status,
    string? Summary = null,
    RoutineEvidence? Evidence = null,
    decimal CreditsUsed = 0,
    string? Model = null,
    bool CreditsLedgered = false);

/// <summary>
/// Executes one routine invocation inside the agent runtime (FR-RTN-01). Implemented by the runtime project (WP9).
/// <para>
/// Contract: the scheduler has already applied the kill switch, lifecycle state, concurrency cap, budget gate and
/// missing-input policy, and records history, evidence, credits and alerts from the returned result. The executor must
/// (1) run <see cref="SessionTarget.Main"/> invocations as a turn in the owner's main session and
/// <see cref="SessionTarget.IsolatedTask"/> invocations in a fresh task session, (2) treat
/// <see cref="RoutineInvocation.EventPayload"/> as untrusted, (3) honour <see cref="RoutineInvocation.DryRun"/>,
/// (4) return <see cref="RoutineExecutionStatus.Refused"/> or <see cref="RoutineExecutionStatus.OverBudget"/> instead of
/// throwing when it declines a run — a throw is recorded as <see cref="RoutineExecutionStatus.Failed"/> — and
/// (5) return <see cref="RoutineExecutionStatus.NothingToReport"/> for a silent heartbeat. Cancellation must be
/// propagated; it is the only exception the scheduler does not convert into a failed run.
/// </para>
/// </summary>
public interface IRoutineExecutor
{
    Task<RoutineExecutionResult> ExecuteAsync(RoutineInvocation invocation, CancellationToken cancellationToken);
}
