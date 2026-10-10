using System.Collections.Frozen;
using Fleet.Contracts.Agents;
using Fleet.Policy.Safety;

namespace Fleet.Orchestration.Registry;

/// <summary>Read-only directory of declared agents. Agents cannot add or edit entries (FR-AGT-06).</summary>
public sealed class AgentRegistry(IEnumerable<AgentDefinition> agents, KillSwitch killSwitch)
{
    private readonly FrozenDictionary<string, AgentDefinition> _agents =
        agents.ToFrozenDictionary(a => a.Id.Value, StringComparer.Ordinal);

    public IReadOnlyCollection<AgentDefinition> All => _agents.Values;

    public AgentDefinition? Find(AgentId id) => _agents.GetValueOrDefault(id.Value);

    public AgentDefinition Get(AgentId id) =>
        Find(id) ?? throw new KeyNotFoundException($"Unknown agent '{id}'.");

    /// <summary>
    /// <c>list_agents</c> (FR-A2A-01): AgentCards for peers the caller may reach, excluding paused agents.
    /// </summary>
    public IReadOnlyList<AgentCard> ListFor(AgentId caller)
    {
        var self = Get(caller);
        return [.. _agents.Values
            .Where(a => a.Id != caller && self.Peers.AllowsAny(a.Id) && !a.Paused && !killSwitch.IsBlocked(a.Id))
            .OrderBy(a => a.Id.Value, StringComparer.Ordinal)
            .Select(a => a.ToCard())];
    }
}
