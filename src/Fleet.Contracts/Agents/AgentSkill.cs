namespace Fleet.Contracts.Agents;

/// <summary>A skill advertised on an agent card (FR-A2A-01): name, description and example requests.</summary>
public sealed record AgentSkill(string Name, string Description, IReadOnlyList<string> Examples);
