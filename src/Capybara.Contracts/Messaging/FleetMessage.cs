using Capybara.Contracts.Agents;

namespace Capybara.Contracts.Messaging;

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

    /// <summary>The delegated task this message concerns (assignment, input request, answer or completion).</summary>
    public string? TaskId { get; init; }

    /// <summary>
    /// The recipient's session that must process this message (for example the task session it belongs to, or the
    /// session that delegated the task). <see langword="null"/> means the recipient's main session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>Conversational channel a user DM arrived on (e.g. <c>telegram</c>), so the reply can go back there.</summary>
    public string? Channel { get; init; }

    /// <summary>
    /// For a host notice resuming a turn a routine ran (an approval outcome): that routine, so the resumed turn stays
    /// attributed to it (FR-OBS-03). Honoured only on <see cref="MessageOrigin.System"/> messages.
    /// </summary>
    public string? RoutineId { get; init; }

    /// <summary>
    /// With <see cref="RoutineId"/>: the routine's approval boundary, which the resumed turn keeps (FR-RTN-02). It can only
    /// add approval requirements, never remove one.
    /// </summary>
    public IReadOnlyList<string> ApprovalBoundary { get; init; } = [];
}
