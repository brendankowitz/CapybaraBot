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

    public bool AcceptsConsults { get; init; }

    public bool AcceptsMessages { get; init; }

    public bool AcceptsTasks { get; init; }

    public bool Paused { get; init; }

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
    };
}
