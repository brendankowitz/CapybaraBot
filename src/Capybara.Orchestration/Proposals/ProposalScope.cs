using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Proposals;
using Capybara.Orchestration.Configuration;

namespace Capybara.Orchestration.Proposals;

/// <summary>
/// What an approved proposal may change, judged on the configuration the loader produces rather than on the text
/// (FR-AGT-06, FR-CON-07, FR-SAF-11). The proposing agent's definition must stay equal except for the fields its kind
/// allows; every other agent, every rule and every routine except the proposed one must be unchanged; and a proposed
/// routine must be owned by the proposing agent before and after.
/// </summary>
/// <remarks>
/// The comparison is by serialized value over every public property, so a field added to
/// <see cref="AgentDefinition"/> later is protected until someone adds it to an allowlist here.
/// </remarks>
internal static class ProposalScope
{
    /// <summary>
    /// <c>agent.yaml</c> fields an agent may propose. Everything else (grants, MCP servers, provider, trifecta,
    /// approval and external-action tools, budget, host, hidden, accepts, memory access, remote sessions) is changed
    /// only by the user.
    /// </summary>
    private static readonly string[] AgentConfigFields =
    [
        nameof(AgentDefinition.Name),
        nameof(AgentDefinition.Job),
        nameof(AgentDefinition.Description),
        nameof(AgentDefinition.Model),
        nameof(AgentDefinition.Tools),
        nameof(AgentDefinition.EgressDomains),
        nameof(AgentDefinition.Peers),
        nameof(AgentDefinition.Notifications),
    ];

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static IReadOnlyList<string> Violations(CapybaraConfiguration before, CapybaraConfiguration after, ChangeProposal proposal)
    {
        var errors = new List<string>();
        CompareAgents(before, after, proposal, errors);

        if (!JsonNode.DeepEquals(ToNode(before.Rules), ToNode(after.Rules)))
        {
            errors.Add("rules.yaml cannot be changed by a proposal.");
        }

        var oldRoutines = before.Routines.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var newRoutines = after.Routines.ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var id in oldRoutines.Keys.Union(newRoutines.Keys).Order(StringComparer.Ordinal))
        {
            var old = oldRoutines.GetValueOrDefault(id);
            var current = newRoutines.GetValueOrDefault(id);
            if (old is not null && current is not null && JsonNode.DeepEquals(ToNode(old), ToNode(current)))
            {
                continue;
            }

            if (proposal.Kind != ProposalKind.Routine || id != proposal.Name)
            {
                errors.Add($"routine '{id}' cannot be changed by this proposal.");
                continue;
            }

            if (old is not null && old.Owner != proposal.Agent)
            {
                errors.Add($"routine '{id}' belongs to {old.Owner}.");
            }

            if (current is null || current.Owner != proposal.Agent)
            {
                errors.Add($"routine '{id}' must be owned by {proposal.Agent}.");
            }
        }

        return errors;
    }

    private static void CompareAgents(CapybaraConfiguration before, CapybaraConfiguration after, ChangeProposal proposal, List<string> errors)
    {
        var oldAgents = before.Agents.ToDictionary(a => a.Id);
        var newAgents = after.Agents.ToDictionary(a => a.Id);
        foreach (var id in oldAgents.Keys.Union(newAgents.Keys).OrderBy(a => a.Value, StringComparer.Ordinal))
        {
            if (!oldAgents.TryGetValue(id, out var old) || !newAgents.TryGetValue(id, out var current))
            {
                errors.Add($"agent '{id}' cannot be {(old is null ? "added" : "removed")} by a proposal.");
                continue;
            }

            var allowed = id == proposal.Agent ? AllowedFields(proposal.Kind) : [];
            var oldFields = ToNode(old).AsObject();
            var newFields = ToNode(current).AsObject();
            foreach (var field in oldFields.Select(f => f.Key).Union(newFields.Select(f => f.Key)).Except(allowed, StringComparer.Ordinal))
            {
                if (!JsonNode.DeepEquals(oldFields[field], newFields[field]))
                {
                    errors.Add(id == proposal.Agent
                        ? $"{id}: '{field}' cannot be changed by a proposal; only the user changes it."
                        : $"{id}: a proposal from {proposal.Agent} cannot change another agent's '{field}'.");
                }
            }
        }
    }

    private static string[] AllowedFields(ProposalKind kind) => kind switch
    {
        ProposalKind.Soul => [nameof(AgentDefinition.Soul)],
        ProposalKind.OperatingRules => [nameof(AgentDefinition.OperatingRules)],
        ProposalKind.AgentConfig => AgentConfigFields,
        ProposalKind.Skill => [nameof(AgentDefinition.Skills)],
        _ => [],
    };

    private static JsonNode ToNode<T>(T value) where T : notnull => JsonSerializer.SerializeToNode(value, Json)!;
}
