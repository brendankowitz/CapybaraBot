using System.Collections.Frozen;
using Capybara.Contracts.Agents;
using Capybara.Policy.Safety;

namespace Capybara.Orchestration.Registry;

/// <summary>
/// Directory of declared agents. Agents cannot add or edit entries (FR-AGT-06); only a reload of user-owned config
/// after a human action (<see cref="Replace"/>) changes it.
/// </summary>
public sealed class AgentRegistry(IEnumerable<AgentDefinition> agents, KillSwitch killSwitch)
{
    private volatile FrozenDictionary<string, AgentDefinition> _agents =
        agents.ToFrozenDictionary(a => a.Id.Value, StringComparer.Ordinal);

    /// <summary>Raised after <see cref="Replace"/> with the previous and new definitions.</summary>
    public event EventHandler<AgentRegistryChangedEventArgs>? Changed;

    public IReadOnlyCollection<AgentDefinition> All => _agents.Values;

    /// <summary>Swaps in a freshly loaded and validated configuration.</summary>
    public void Replace(IEnumerable<AgentDefinition> agents)
    {
        var previous = _agents;
        _agents = agents.ToFrozenDictionary(a => a.Id.Value, StringComparer.Ordinal);
        Changed?.Invoke(this, new AgentRegistryChangedEventArgs([.. previous.Values], [.. _agents.Values]));
    }

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
            .Where(a => a.Id != caller && self.Peers.AllowsAny(a.Id) && !a.Paused && !a.Hidden && !killSwitch.IsBlocked(a.Id))
            .OrderBy(a => a.Id.Value, StringComparer.Ordinal)
            .Select(a => a.ToCard())];
    }
}

public sealed class AgentRegistryChangedEventArgs(IReadOnlyList<AgentDefinition> previous, IReadOnlyList<AgentDefinition> current) : EventArgs
{
    public IReadOnlyList<AgentDefinition> Previous { get; } = previous;

    public IReadOnlyList<AgentDefinition> Current { get; } = current;

    public IEnumerable<AgentId> Added => Current.Select(a => a.Id).Except(Previous.Select(a => a.Id));

    public IEnumerable<AgentId> Removed => Previous.Select(a => a.Id).Except(Current.Select(a => a.Id));
}
