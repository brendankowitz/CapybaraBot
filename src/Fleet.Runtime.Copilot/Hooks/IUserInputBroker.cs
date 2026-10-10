using Fleet.Runtime.Copilot.Sessions;

namespace Fleet.Runtime.Copilot.Hooks;

/// <summary>
/// Routes <c>ask_user</c> to the right human surface. Main sessions reach the user via the PWA/ntfy; child sessions
/// bubble the question up through the task's <c>input-required</c> state (FR-A2A-07).
/// </summary>
public interface IUserInputBroker
{
    Task<string?> AskAsync(FleetSession session, string question, IReadOnlyList<string> choices, CancellationToken cancellationToken);
}

/// <summary>Default broker for unattended runs: nobody is there, so the agent must proceed without an answer.</summary>
public sealed class UnattendedUserInputBroker : IUserInputBroker
{
    public Task<string?> AskAsync(FleetSession session, string question, IReadOnlyList<string> choices, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
