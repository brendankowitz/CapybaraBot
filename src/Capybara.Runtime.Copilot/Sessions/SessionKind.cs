namespace Capybara.Runtime.Copilot.Sessions;

public enum SessionKind
{
    /// <summary>The agent's long-lived, resumable session with infinite-session compaction.</summary>
    Main,

    /// <summary>Ephemeral, read-only session that answers one <c>ask_agent</c> question.</summary>
    Consult,

    /// <summary>
    /// A session working a delegated task (FR-A2A-04), or an isolated routine run (FR-RTN-01). Resumable across turns
    /// until the work ends.
    /// </summary>
    Task,

    /// <summary>A conversation that reads no memory and writes none (FR-MEM-12).</summary>
    Incognito,
}
