using Capybara.Contracts.Agents;

namespace Capybara.Policy.Trifecta;

/// <summary>
/// Config validator for the Rule of Two (FR-SAF-02). An agent may hold all three legs only if every external-action
/// tool is approval-required.
/// </summary>
public static class TrifectaValidator
{
    public static IReadOnlyList<string> Validate(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var errors = new List<string>();
        var profile = agent.Trifecta;

        if (agent.ExternalActionTools.Count > 0 && !profile.ExternalAction)
        {
            errors.Add($"{agent.Id}: declares external-action tools but its trifecta profile has no external-action leg.");
        }

        if (agent.Grants.Count > 0 && !profile.PrivateData)
        {
            errors.Add($"{agent.Id}: holds connection grants but its trifecta profile has no private-data leg.");
        }

        var notInAllowlist = agent.ExternalActionTools.Except(agent.Tools, StringComparer.Ordinal).ToArray();
        if (notInAllowlist.Length > 0)
        {
            errors.Add($"{agent.Id}: external-action tools not in the tool allowlist: {string.Join(", ", notInAllowlist)}.");
        }

        if (profile.HoldsAllThree)
        {
            var ungated = agent.ExternalActionTools.Except(agent.ApprovalRequiredTools, StringComparer.Ordinal).ToArray();
            if (ungated.Length > 0)
            {
                errors.Add($"{agent.Id}: holds all three trifecta legs, so these external-action tools must be approval-required: {string.Join(", ", ungated)}.");
            }
        }

        return errors;
    }
}
