namespace Fleet.Policy.Rules;

/// <summary>
/// A deterministic policy entry matched on agent, tool and (optionally) one argument value. Patterns are globs where
/// <c>*</c> matches any run of characters.
/// </summary>
public sealed record PolicyRule
{
    public required RuleEffect Effect { get; init; }

    public string Agent { get; init; } = "*";

    public required string Tool { get; init; }

    /// <summary>Name of the argument to match, e.g. <c>command</c>, <c>url</c> or <c>to</c>.</summary>
    public string? Argument { get; init; }

    /// <summary>Glob applied to <see cref="Argument"/>'s string value.</summary>
    public string? ArgumentPattern { get; init; }

    /// <summary>Destinations (argument values) that bypass the taint check for <see cref="RuleEffect.AllowUnlessTainted"/>.</summary>
    public IReadOnlyList<string> DestinationAllowlist { get; init; } = [];

    public string? Reason { get; init; }
}
