using Capybara.Contracts.Routines;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>
/// An isolated routine run waiting for an approval (FR-RTN-01, FR-SAF-04). Persisted under the owner's host state, so
/// the turn that resumes the session after the decision still runs as the routine — attributed to it, on its model,
/// within what is left of its per-run budget — and its output reaches the routine's delivery target.
/// </summary>
public sealed record RoutineContinuation(RoutineSpec Routine, string RunId, string Model, decimal RemainingCredits, string SessionKey);
