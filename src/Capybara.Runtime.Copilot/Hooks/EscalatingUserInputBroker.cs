using Capybara.Channels;
using Capybara.Orchestration.Coordination;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Sessions;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Hooks;

/// <summary>
/// <c>ask_user</c> routing (FR-A2A-07): only the root, user-facing agent of a piece of work talks to the human. A
/// sub-agent working a delegated task instead sets the task to <c>INPUT_REQUIRED</c>; the question bubbles up the hop
/// path to the requester, whose answer comes back later as a message in the task session (agent content, so data and
/// never consent, FR-A2A-08). A root session escalates to the user's phone and waits up to
/// <see cref="CopilotRuntimeOptions.UserEscalationTimeout"/>. Consults and dry runs never ask anyone.
/// </summary>
public sealed partial class EscalatingUserInputBroker(
    Lazy<A2ACoordinator> coordinator,
    IUserEscalation escalation,
    CopilotRuntimeOptions options,
    ILogger<EscalatingUserInputBroker> logger) : IUserInputBroker
{
    /// <summary>What a sub-agent is told after its question was passed up.</summary>
    public const string QuestionPassedUp =
        "Your question was sent to the agent that delegated this task, and the task now waits for input. End this turn "
        + "now without completing the task; the answer will arrive as a new message in this session.";

    public async Task<string?> AskAsync(CapybaraSession session, string question, IReadOnlyList<string> choices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(choices);
        if (session.DryRun || session.Kind == SessionKind.Consult)
        {
            return null;
        }

        var text = choices.Count == 0 ? question : $"{question}\nOptions: {string.Join(" | ", choices)}";
        if (session.TaskId is { } taskId)
        {
            var result = await coordinator.Value.RequestInputAsync(session.Agent.Id, session.SessionId, taskId, text, cancellationToken);
            return result.Success ? QuestionPassedUp : null;
        }

        if (session.Depth > 0)
        {
            return null;
        }

        try
        {
            return await escalation.AskAsync(Guid.NewGuid().ToString("N"), session.Agent.Id, $"{session.Agent.Name} has a question", text, options.UserEscalationTimeout, cancellationToken);
        }
#pragma warning disable CA1031 // Nobody reachable is the same as nobody answering.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogEscalationFailed(logger, ex, session.Agent.Id.Value);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not escalate a question from {Agent} to the user")]
    private static partial void LogEscalationFailed(ILogger logger, Exception exception, string agent);
}
