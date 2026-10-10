using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>One turn for an agent runtime: what to say, on whose behalf, and where.</summary>
public sealed record AgentTurnRequest
{
    public required string Prompt { get; init; }

    /// <summary>Who produced the prompt. Only <see cref="MessageOrigin.User"/> can be consent (FR-A2A-08).</summary>
    public required MessageOrigin Origin { get; init; }

    /// <summary>The human the turn ultimately acts for; becomes the envelope's origin principal.</summary>
    public required string Principal { get; init; }

    public TurnTarget Target { get; init; } = TurnTarget.Main;

    /// <summary>The agent that produced the prompt, labelled as <c>MessageSource.Agent(id)</c> (FR-A2A-08).</summary>
    public AgentId? From { get; init; }

    /// <summary>Taint of the incoming content; it propagates to the receiving session (FR-A2A-08, FR-SAF-03).</summary>
    public Taint Taint { get; init; }

    /// <summary>The routine that triggered the turn (FR-OBS-03).</summary>
    public string? RoutineId { get; init; }

    /// <summary>Routine dry run: approval-required tools are auto-denied and nobody is prompted (FR-RTN-08).</summary>
    public bool DryRun { get; init; }

    /// <summary>Model for this turn only, e.g. a routine's model (FR-AGT-08). <see langword="null"/> keeps the agent's.</summary>
    public string? Model { get; init; }

    /// <summary>Budget for a session without a task envelope, e.g. a routine's per-run budget.</summary>
    public Budget? Budget { get; init; }

    /// <summary>
    /// The chain this turn continues: another agent's message, or a notice (approval outcome, failed sub-task) about a
    /// turn that ran in a chain. Depth, carved budget, deadline, root task, root origin and taint carry over
    /// (FR-A2A-05/06/07, FR-A2A-11) instead of a fresh root. Ignored for delegated tasks, which run under the task's own
    /// envelope.
    /// </summary>
    public Envelope? Envelope { get; init; }
}
