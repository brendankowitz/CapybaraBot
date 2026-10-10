using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Supervision;

/// <summary>
/// A runtime that can replay its main session's persisted event log (<c>GetEventsAsync</c>), so in-flight turns can
/// be reconciled after a crash (FR-AGT-04).
/// </summary>
public interface ISessionEventHistory
{
    /// <summary>All persisted events of the agent's main session, in chronological order.</summary>
    Task<IReadOnlyList<SessionEvent>> GetMainSessionEventsAsync(CancellationToken cancellationToken);
}
