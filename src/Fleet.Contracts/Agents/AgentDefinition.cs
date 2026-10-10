using Fleet.Contracts.Connections;

namespace Fleet.Contracts.Agents;

/// <summary>
/// A persistent agent as declared in user-owned config (<c>agents/{id}/agent.yaml</c>, <c>SOUL.md</c>, <c>AGENTS.md</c>).
/// Agents can never write this (FR-AGT-06).
/// </summary>
public sealed record AgentDefinition
{
    public required AgentId Id { get; init; }

    public required string Name { get; init; }

    public required string Job { get; init; }

    /// <summary>Standing rules, e.g. "Never send external messages without approval".</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Model id, or <c>auto</c> (10% discount, FR-AGT-08).</summary>
    public string Model { get; init; } = "auto";

    /// <summary>Contents of <c>SOUL.md</c>: identity and tone.</summary>
    public string Soul { get; init; } = string.Empty;

    /// <summary>Contents of <c>AGENTS.md</c>: operating rules.</summary>
    public string OperatingRules { get; init; } = string.Empty;

    /// <summary>Source-qualified tool allowlist passed as <c>AvailableTools</c> (FR-AGT-02).</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Tools that change state or communicate externally: the third trifecta leg.</summary>
    public IReadOnlyList<string> ExternalActionTools { get; init; } = [];

    /// <summary>Tools that always require human approval.</summary>
    public IReadOnlyList<string> ApprovalRequiredTools { get; init; } = [];

    public TrifectaProfile Trifecta { get; init; } = new();

    public PeerAllowlist Peers { get; init; } = new();

    public IReadOnlyList<Grant> Grants { get; init; } = [];

    public IReadOnlyList<string> EgressDomains { get; init; } = [];

    public AgentBudget Budget { get; init; } = new();

    /// <summary>Whether the agent may read <c>/user/</c> memory. The research agent may not (no private-data leg).</summary>
    public bool ReadsUserMemory { get; init; }

    public bool AcceptsConsults { get; init; }

    public bool AcceptsMessages { get; init; }

    public bool AcceptsTasks { get; init; }

    public bool Paused { get; init; }

    /// <summary>Skills found in the agent's skill directory, advertised on its card.</summary>
    public IReadOnlyList<AgentSkill> Skills { get; init; } = [];

    /// <summary>Hidden agents keep running but are not listed in clients or <c>list_agents</c> (FR-AGT-07).</summary>
    public bool Hidden { get; init; }

    /// <summary>Which <c>IAgentHost</c> runs the agent: <c>systemd</c>, <c>podman</c>, <c>aca</c> or <c>remote</c> (FR-HST-01, NFR-09).</summary>
    public string Host { get; init; } = "systemd";

    /// <summary>Optional BYOK provider (FR-AGT-10).</summary>
    public ModelProviderSpec? Provider { get; init; }

    /// <summary>Vendored MCP servers attached to every session (FR-CON-03).</summary>
    public IReadOnlyList<McpServerSpec> McpServers { get; init; } = [];

    /// <summary>Allow GitHub remote sessions (Mission Control) when the working directory is a GitHub repo (FR-CLI-08).</summary>
    public bool EnableRemoteSessions { get; init; }

    /// <summary>Per-agent notification toggle (FR-CLI-07).</summary>
    public bool Notifications { get; init; } = true;

    public AgentCard ToCard() => new()
    {
        Id = Id,
        Name = Name,
        Job = Job,
        Description = Description,
        AcceptsConsults = AcceptsConsults,
        AcceptsMessages = AcceptsMessages,
        AcceptsTasks = AcceptsTasks,
        Model = Model,
        Trifecta = Trifecta,
        Skills = Skills,
    };
}
