namespace Capybara.Policy.Rules;

/// <summary>The strongest rule that matched a call, or <see langword="null"/> when the rules leave it unresolved.</summary>
public sealed record RuleMatch(PolicyRule Rule, string? ArgumentValue);
