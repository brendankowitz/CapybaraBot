using System.Collections.Immutable;
using System.Text.Json;

namespace Fleet.Policy.Rules;

/// <summary>
/// Deterministic rules engine (FR-SAF-01 layer 2). The strongest matching effect wins, so "ask first" always beats
/// "allow". Rules added through "Always allow" approvals are appended at runtime.
/// </summary>
public sealed class RulesEngine(IEnumerable<PolicyRule> rules)
{
    private ImmutableList<PolicyRule> _rules = [.. rules];

    public IReadOnlyList<PolicyRule> Rules => _rules;

    public void Add(PolicyRule rule) => ImmutableInterlocked.Update(ref _rules, static (list, r) => list.Add(r), rule);

    public RuleMatch? Evaluate(string agent, string tool, JsonElement? args)
    {
        RuleMatch? best = null;
        foreach (var rule in _rules)
        {
            if (!Glob.IsMatch(rule.Agent, agent) || !Glob.IsMatch(rule.Tool, tool))
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
