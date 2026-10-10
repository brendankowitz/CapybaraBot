using System.Text.RegularExpressions;

namespace Fleet.Hosting.Agents;

/// <summary>Strict identifier validation prevents unit, container, and URL injection (FR-HST-01).</summary>
public static partial class AgentIdValidator
{
    public static void Validate(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (!ValidAgentId().IsMatch(agentId))
        {
            throw new ArgumentException("Agent ids must be 1-64 lowercase letters, digits, dots, underscores, or hyphens.", nameof(agentId));
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidAgentId();
}
