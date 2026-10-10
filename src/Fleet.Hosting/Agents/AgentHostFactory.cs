namespace Fleet.Hosting.Agents;

/// <summary>Selects the configured host implementation per agent so placement changes are configuration-only (NFR-09).</summary>
public sealed class AgentHostFactory(IReadOnlyDictionary<AgentHostKind, IAgentHost> hosts, IReadOnlyDictionary<string, AgentHostKind> agentHosts, AgentHostKind defaultKind = AgentHostKind.Systemd)
{
    private readonly IReadOnlyDictionary<AgentHostKind, IAgentHost> _hosts = hosts;
    private readonly IReadOnlyDictionary<string, AgentHostKind> _agentHosts = agentHosts;
    private readonly AgentHostKind _defaultKind = defaultKind;

    public IAgentHost GetHost(string agentId)
    {
        AgentIdValidator.Validate(agentId);
        var kind = _agentHosts.GetValueOrDefault(agentId, _defaultKind);
        return _hosts.TryGetValue(kind, out var host) ? host : throw new InvalidOperationException($"No host registered for {kind}.");
    }
}
