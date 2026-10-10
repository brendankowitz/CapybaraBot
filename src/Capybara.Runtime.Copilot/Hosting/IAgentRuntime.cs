using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Orchestration.Coordination;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>
/// One agent's runtime as the inbox pump, routine executor and A2A coordinator use it (FR-AGT-01, FR-AGT-05). The
/// Copilot implementation is <see cref="AgentRuntime"/>; the abstraction exists so those callers can be exercised
/// without a CLI process.
/// </summary>
/// <remarks>
/// Implementations run at most one turn at a time per session (FR-AGT-05) and fail closed: a blocked or over-budget
/// agent gets a <see cref="TurnStatus.Blocked"/> / <see cref="TurnStatus.OverBudget"/> result, not a run.
/// </remarks>
public interface IAgentRuntime : IAsyncDisposable
{
    AgentId Agent { get; }

    /// <summary>
    /// Runs one turn and waits until it is persisted. Failures of the turn itself are returned as
    /// <see cref="TurnStatus.Failed"/>; only cancellation of <paramref name="cancellationToken"/> throws.
    /// </summary>
    Task<TurnResult> RunTurnAsync(AgentTurnRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Interjects <paramref name="prompt"/> into the main-session turn in flight using <c>immediate</c> mode, so a user
    /// DM pre-empts background work, including routine heartbeats (FR-AGT-05). Waits for that turn to finish and returns
    /// its result, so the caller can acknowledge the DM only once it is persisted. Returns <see langword="null"/> when no
    /// main turn is in flight, or the one in flight is a dry run (a user's message must never run under dry-run rules).
    /// </summary>
    Task<TurnResult?> SteerAsync(string prompt, MessageOrigin origin, CancellationToken cancellationToken);

    /// <summary>Changes the agent's model for its main session and later turns (FR-AGT-08).</summary>
    Task SetModelAsync(string model, CancellationToken cancellationToken);

    /// <summary>Deletes a finished task, isolated or incognito session and forgets its taint. Never the main session.</summary>
    Task EndSessionAsync(TurnTarget target, CancellationToken cancellationToken);

    /// <summary>Answers one <c>ask_agent</c> question in a fresh, read-only, ephemeral session (FR-A2A-02).</summary>
    Task<ConsultReply> ConsultAsync(string sessionId, string question, Envelope envelope, CancellationToken cancellationToken);
}

public static class AgentRuntimeExtensions
{
    /// <summary>A main-session turn.</summary>
    public static Task<TurnResult> RunTurnAsync(this IAgentRuntime runtime, string prompt, MessageOrigin origin, string principal, string? routineId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.RunTurnAsync(new AgentTurnRequest { Prompt = prompt, Origin = origin, Principal = principal, RoutineId = routineId }, cancellationToken);
    }
}
