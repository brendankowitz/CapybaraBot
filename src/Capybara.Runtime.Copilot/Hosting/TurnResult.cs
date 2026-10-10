using Capybara.Runtime.Copilot.Sessions;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>How a turn ended.</summary>
public enum TurnStatus
{
    /// <summary>The turn ran and its events are persisted by the runtime.</summary>
    Completed,

    /// <summary>
    /// Not run, or stopped: the agent is paused or the fleet kill switch is engaged. The input was not handled; retry once
    /// released.
    /// </summary>
    Blocked,

    /// <summary>Not run: the agent's hard budget stop is reached (FR-OBS-02). Retry once budget is available.</summary>
    OverBudget,

    /// <summary>
    /// The turn ran, spent more than its own credit limit (a routine's per-run budget or a hop's carved budget,
    /// FR-A2A-05) and was aborted. The input was handled as far as the budget allowed; retrying would overspend again.
    /// </summary>
    LimitExceeded,

    /// <summary>Not run now: the fleet-wide limit on concurrent non-main sessions is reached (FR-A2A-11). Retry later.</summary>
    Throttled,

    /// <summary>The turn was attempted and failed (and, if it was still running, aborted).</summary>
    Failed,
}

/// <summary>Outcome of one turn, with credits, model and evidence (FR-OBS-03, FR-OBS-06).</summary>
public sealed record TurnResult(TurnStatus Status, string? Content, string? Error, TurnEvidence Evidence)
{
    public bool Success => Status == TurnStatus.Completed;

    /// <summary>The routine the turn ran for, if any (FR-OBS-03).</summary>
    public string? RoutineId { get; init; }

    /// <summary>User DMs that were steered into this turn while it ran (FR-AGT-05).</summary>
    public int UserMessagesJoined { get; init; }

    public static TurnResult Completed(string? content, TurnEvidence evidence) => new(TurnStatus.Completed, content, null, evidence);

    public static TurnResult Blocked(string error, TurnEvidence? evidence = null) => new(TurnStatus.Blocked, null, error, evidence ?? TurnEvidence.Empty);

    public static TurnResult OverBudget(string error) => new(TurnStatus.OverBudget, null, error, TurnEvidence.Empty);

    public static TurnResult LimitExceeded(string error, TurnEvidence evidence) => new(TurnStatus.LimitExceeded, null, error, evidence);

    public static TurnResult Throttled(string error) => new(TurnStatus.Throttled, null, error, TurnEvidence.Empty);

    public static TurnResult Failed(string error, TurnEvidence? evidence = null) => new(TurnStatus.Failed, null, error, evidence ?? TurnEvidence.Empty);
}

/// <summary>Result of the post-start self-test (FR-AGT-09).</summary>
public sealed record SelfTestResult(bool Passed, IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected);
