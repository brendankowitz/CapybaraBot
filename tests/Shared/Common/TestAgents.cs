using Capybara.Contracts.Agents;

namespace Capybara.Testing;

/// <summary>Builders for agent definitions used across test projects.</summary>
internal static class TestAgents
{
    public static AgentDefinition Create(string id, Func<AgentDefinition, AgentDefinition>? configure = null)
    {
        var agent = new AgentDefinition
        {
            Id = AgentId.Parse(id),
            Name = id,
            Job = $"{id} job",
            Tools = ["list_agents", "ask_agent", "send_message", "delegate_task", "memory_read", "remember"],
            AcceptsConsults = true,
            AcceptsMessages = true,
            AcceptsTasks = true,
        };

        return configure is null ? agent : configure(agent);
    }

    public static AgentDefinition WithPeers(this AgentDefinition agent, params string[] peers) => agent with
    {
        Peers = new PeerAllowlist { Consult = peers, Message = peers, Task = peers },
    };
}
