using Fleet.Contracts.Agents;
using Fleet.Contracts.Messaging;

namespace Fleet.Runtime.Copilot.Sessions;

/// <summary>
/// Everything the host knows about one session. Tool handlers and hooks close over this — never over model output —
/// so identity, depth and envelope are always host-assigned (FR-A2A-04).
/// </summary>
public sealed class FleetSession
{
    public FleetSession(AgentDefinition agent, string sessionId, SessionKind kind, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(envelope);
        Agent = agent;
        SessionId = sessionId;
        Kind = kind;
        Envelope = envelope;
    }

    public AgentDefinition Agent { get; }

    public string SessionId { get; }

    public SessionKind Kind { get; }

    /// <summary>The envelope of the turn currently running. Main sessions get a fresh root envelope per turn.</summary>
    public Envelope Envelope { get; set; }

    /// <summary>Who triggered the current turn. Only <see cref="MessageOrigin.User"/> can be consent.</summary>
    public MessageOrigin TurnOrigin { get; set; } = MessageOrigin.System;

    /// <summary>Routine that triggered the turn, for cost attribution.</summary>
    public string? RoutineId { get; set; }

    public bool IsReadOnly => Kind == SessionKind.Consult;

    public int Depth => Envelope.Depth;
}
