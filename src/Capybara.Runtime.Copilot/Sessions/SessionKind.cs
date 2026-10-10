namespace Capybara.Runtime.Copilot.Sessions;

public enum SessionKind
{
    /// <summary>The agent's long-lived, resumable session with infinite-session compaction.</summary>
    Main,

    /// <summary>Ephemeral, read-only session that answers one <c>ask_agent</c> question.</summary>
    Consult,

    /// <summary>A session working a delegated task.</summary>
    Task,
}
