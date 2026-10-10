using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Contracts.Tasks;

/// <summary>A durable unit of delegated work owned by exactly one agent (FR-A2A-04, FR-A2A-09).</summary>
public sealed record FleetTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required AgentId Owner { get; init; }

    public required AgentId RequestedBy { get; init; }

    /// <summary>
    /// The requester's session that delegated the task; questions and the result go back to it (FR-A2A-07).
    /// <see langword="null"/> means the requester's main session.
    /// </summary>
    public string? RequesterSessionId { get; init; }

    public required string Objective { get; init; }

    public string? OutputSchema { get; init; }

    public IReadOnlyList<string> Boundaries { get; init; } = [];

    public required Envelope Envelope { get; init; }

    public FleetTaskState State { get; init; } = FleetTaskState.Submitted;

    /// <summary>The clarification question when <see cref="State"/> is <see cref="FleetTaskState.InputRequired"/>.</summary>
    public string? PendingQuestion { get; init; }

    public IReadOnlyList<Artifact> Artifacts { get; init; } = [];

    /// <summary>
    /// Agent-to-agent exchanges between owner and requester under this task's root (the delegation, each question and
    /// each answer), so the per-pair limit survives restarts (FR-A2A-11).
    /// </summary>
    public int Exchanges { get; init; }

    /// <summary>Why the task failed, when <see cref="State"/> is <see cref="FleetTaskState.Failed"/>.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Items the owner could not verify against the objective (FR-A2A-09).</summary>
    public IReadOnlyList<string> Unverified { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
