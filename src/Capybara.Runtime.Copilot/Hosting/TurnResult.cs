namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>Outcome of one turn on an agent's main session.</summary>
public sealed record TurnResult(bool Success, string? Content, string? Error)
{
    public static TurnResult Failed(string error) => new(false, null, error);
}

/// <summary>Result of the post-start self-test (FR-AGT-09).</summary>
public sealed record SelfTestResult(bool Passed, IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected);
