using System.Collections.Immutable;
using System.Text.Json;

namespace Capybara.Policy.Rules;

/// <summary>
/// Deterministic rules engine (FR-SAF-01 layer 2). The strongest matching effect wins, so "ask first" always beats
/// "allow". Rules added through "Always allow" approvals are learned at runtime and survive configuration reloads.
/// </summary>
public sealed class RulesEngine(IEnumerable<PolicyRule> rules, IEnumerable<PolicyRule>? learned = null)
{
    private readonly Lock _gate = new();
    private ImmutableList<PolicyRule> _configured = [.. rules];
    private ImmutableList<PolicyRule> _learned = [.. learned ?? []];
    private ImmutableList<PolicyRule> _rules = [.. rules, .. learned ?? []];

    /// <summary>Raised after an "Always allow" rule is learned so it can be persisted.</summary>
    public event EventHandler<PolicyRule>? RuleLearned;

    public IReadOnlyList<PolicyRule> Rules => _rules;

    /// <summary>Rules learned from "Always allow" approvals.</summary>
    public IReadOnlyList<PolicyRule> LearnedRules => _learned;

    public void Add(PolicyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            _learned = _learned.Add(rule);
            _rules = _configured.AddRange(_learned);
        }

        RuleLearned?.Invoke(this, rule);
    }

    /// <summary>Swaps the configured rules after <c>rules.yaml</c> changes, keeping learned rules.</summary>
    public void ReplaceConfigured(IEnumerable<PolicyRule> configured)
    {
        lock (_gate)
        {
            _configured = [.. configured];
            _rules = _configured.AddRange(_learned);
        }
    }

    public RuleMatch? Evaluate(string agent, string tool, JsonElement? args)
    {
        RuleMatch? best = null;
        foreach (var rule in _rules)
        {
            if (!Glob.IsMatch(rule.Agent, agent) || !ToolPattern.IsMatch(rule.Tool, tool))
            {
                continue;
            }

            string? argumentValue = null;
            if (rule.Argument is not null)
            {
                argumentValue = ReadArgument(args, rule.Argument);
                if (argumentValue is null || (rule.ArgumentPattern is not null && !Glob.IsMatch(rule.ArgumentPattern, argumentValue)))
                {
                    continue;
                }
            }

            if (best is null || rule.Effect > best.Rule.Effect)
            {
                best = new RuleMatch(rule, argumentValue);
            }
        }

        return best;
    }

    internal static string? ReadArgument(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
}
