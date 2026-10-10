using Capybara.Contracts.Agents;

namespace Capybara.Contracts.Messaging;

/// <summary>
/// Metadata carried on every agent-to-agent call (FR-A2A-05). The W3C <see cref="TraceParent"/> makes a multi-agent
/// task a single trace (FR-OBS-01).
/// </summary>
public sealed record Envelope
{
    public const int MaxDepth = 2;

    public required string TraceId { get; init; }

    public string? TraceParent { get; init; }

    public required string RootTaskId { get; init; }

    public string? ParentTaskId { get; init; }

    public int Depth { get; init; }

    public IReadOnlyList<AgentId> HopPath { get; init; } = [];

    public required Budget Budget { get; init; }

    public required DateTimeOffset Deadline { get; init; }

    /// <summary>The human the chain ultimately acts for. Never an agent.</summary>
    public required string OriginPrincipal { get; init; }

    public Taint Taint { get; init; }

    public bool IsAtMaxDepth => Depth >= MaxDepth;
}
