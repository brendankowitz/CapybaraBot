using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Capybara.Contracts.Agents;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>What a running agent needs for an edited definition to take effect (FR-AGT-06, FR-AGT-07).</summary>
public enum DefinitionChange
{
    None,

    /// <summary>Session configuration only (persona, rules, tools, model, skills, budget...): rebuild the main session.</summary>
    Sessions,

    /// <summary>
    /// Something fixed when the CLI process starts (its srt egress allowlist, stdio MCP servers, BYOK provider, remote
    /// sessions or host): restart the CLI as well.
    /// </summary>
    Process,
}

/// <summary>Classifies the difference between two definitions of the same agent by serialized value.</summary>
public static class DefinitionChanges
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static DefinitionChange Classify(AgentDefinition current, AgentDefinition next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        if (current.Id != next.Id)
        {
            throw new ArgumentException($"'{next.Id}' is not a new definition of '{current.Id}'.", nameof(next));
        }

        if (JsonNode.DeepEquals(Node(current), Node(next)))
        {
            return DefinitionChange.None;
        }

        return JsonNode.DeepEquals(Node(ProcessFields(current)), Node(ProcessFields(next))) ? DefinitionChange.Sessions : DefinitionChange.Process;
    }

    // Comparing every public property means a field added later is never silently treated as "no change"; the process
    // subset errs towards a restart, which only costs a few seconds and re-runs the tool self-test.
    private static object ProcessFields(AgentDefinition agent) =>
        new { agent.EgressDomains, agent.McpServers, agent.Provider, agent.EnableRemoteSessions, agent.Host };

    private static JsonNode? Node(object value) => JsonSerializer.SerializeToNode(value, value.GetType(), Json);
}
