using Fleet.Contracts.Agents;

namespace Fleet.Contracts.Messaging;

/// <summary>A message delivered to an agent inbox. Agent-originated content is data, never instructions or consent.</summary>
public sealed record FleetMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required AgentId To { get; init; }

    public AgentId? From { get; init; }

    public required MessageOrigin Origin { get; init; }

    public required string Body { get; init; }

    public IReadOnlyList<Artifact> Attachments { get; init; } = [];

    public Envelope? Envelope { get; init; }

    public Taint Taint { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
