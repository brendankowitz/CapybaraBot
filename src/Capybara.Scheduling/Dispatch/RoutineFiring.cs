using Capybara.Contracts.Routines;
using Capybara.Scheduling.Execution;

namespace Capybara.Scheduling.Dispatch;

/// <summary>
/// A durable scheduler or event source firing to dispatch (FR-RTN-01, FR-RTN-05). <c>FireTime</c> is when it actually
/// happened. <c>IsMisfire</c> explicitly flags a missed run (recorded, never executed). <c>ScheduledFor</c> is when it was
/// supposed to happen: if that is more than <see cref="SchedulingOptions.MisfireTolerance"/> before <c>FireTime</c> the
/// dispatcher applies the <see cref="CatchUpPolicy"/>; it is null for event and manual firings.
/// </summary>
public sealed record RoutineFiring(
    RoutineSpec Routine,
    DateTimeOffset FireTime,
    string RunId,
    bool DryRun = false,
    RoutineEventPayload? EventPayload = null,
    bool IsMisfire = false,
    DateTimeOffset? ScheduledFor = null);
