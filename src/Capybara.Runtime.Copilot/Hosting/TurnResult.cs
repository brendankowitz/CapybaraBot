using Capybara.Runtime.Copilot.Sessions;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>How a turn ended.</summary>
public enum TurnStatus
{
    /// <summary>The turn ran and its events are persisted by the runtime.</summary>
    Completed,

    /// <summary>Not run: the agent is paused or the fleet kill switch is engaged. Retry once released.</summary>
    Blocked,

    /// <summary>Not run: the agent's hard budget stop is reached (FR-OBS-02).</summary>
    OverBudget,

    /// <summary>The turn was attempted and failed.</summary>
    Failed,
}

/// <summary>Outcome of one turn, with credits, model and evidence (FR-OBS-03, FR-OBS-06).</summary>
public sealed record TurnResult(TurnStatus Status, string? Content, string? Error, TurnEvidence Evidence)
{
    public bool Success => Status == TurnStatus.Completed;

    public static TurnResult Completed(string? content, TurnEvidence evidence) => new(TurnStatus.Completed, content, null, evidence);

    public static TurnResult Blocked(string error) => new(TurnStatus.Blocked, null, error, TurnEvidence.Empty);

    public static TurnResult OverBudget(string error) => new(TurnStatus.OverBudget, null, error, TurnEvidence.Empty);

    public static TurnResult Failed(string error, TurnEvidence? evidence = null) => new(TurnStatus.Failed, null, error, evidence ?? TurnEvidence.Empty);
}

/// <summary>Result of the post-start self-test (FR-AGT-09).</summary>
public sealed record SelfTestResult(bool Passed, IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected);
