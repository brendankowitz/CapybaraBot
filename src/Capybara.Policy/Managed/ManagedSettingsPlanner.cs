using Capybara.Contracts.Agents;
using Capybara.Policy.Rules;

namespace Capybara.Policy.Managed;

/// <summary>
/// Derives the runtime's managed deny and ask lists from the rules that apply to an agent and its approval-required
/// tools. Shell command rules become <c>shell(pattern)</c> entries, as in <c>shell(rm*)</c>. Rules that need taint or
/// destination context stay host-only, because the runtime cannot evaluate them.
/// </summary>
public static class ManagedSettingsPlanner
{
    private static readonly string[] ShellTools = ["shell", "bash", "powershell"];

    public static ManagedPermissionPlan Plan(AgentDefinition agent, IEnumerable<PolicyRule> rules)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(rules);

        var deny = new SortedSet<string>(StringComparer.Ordinal);
        var ask = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules.Where(r => r.Effect is RuleEffect.Deny or RuleEffect.Ask && Glob.IsMatch(r.Agent, agent.Id.Value)))
        {
            var entry = ToEntry(rule);
            if (entry is not null)
            {
                (rule.Effect == RuleEffect.Deny ? deny : ask).Add(entry);
            }
        }

        foreach (var tool in agent.ApprovalRequiredTools)
        {
            ask.Add(ToolPattern.Unqualify(tool));
        }

        ask.ExceptWith(deny);
        return new ManagedPermissionPlan([.. deny], [.. ask]);
    }

    private static string? ToEntry(PolicyRule rule)
    {
        var tool = ToolPattern.Unqualify(rule.Tool);
        if (rule.Argument is null)
        {
            return tool;
        }

        // Only shell command patterns have a runtime rule syntax; anything else stays with the host gate.
        return rule.Argument == "command" && rule.ArgumentPattern is { Length: > 0 } pattern && ShellTools.Contains(tool, StringComparer.Ordinal)
            ? $"shell({pattern})"
            : null;
    }
}
