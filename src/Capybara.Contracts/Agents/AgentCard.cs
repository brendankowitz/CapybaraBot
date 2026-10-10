namespace Capybara.Contracts.Agents;

/// <summary>A2A AgentCard-shaped directory entry returned by <c>list_agents</c> (FR-A2A-01).</summary>
public sealed record AgentCard
{
    public required AgentId Id { get; init; }

    public required string Name { get; init; }

    public required string Job { get; init; }

    public string Description { get; init; } = string.Empty;

    public bool AcceptsConsults { get; init; }

    public bool AcceptsMessages { get; init; }

    public bool AcceptsTasks { get; init; }

    /// <summary>Cost/latency hint: the model tier the agent runs on.</summary>
    public string Model { get; init; } = "auto";

    public required TrifectaProfile Trifecta { get; init; }

    public IReadOnlyList<AgentSkill> Skills { get; init; } = [];

    /// <summary>Rough cost/latency class derived from the model: <c>economy</c> for <c>auto</c>, otherwise <c>premium</c>.</summary>
    public string CostHint => string.Equals(Model, "auto", StringComparison.OrdinalIgnoreCase) ? "economy" : "premium";
}
