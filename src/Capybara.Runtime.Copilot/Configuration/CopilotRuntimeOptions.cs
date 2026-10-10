namespace Capybara.Runtime.Copilot.Configuration;

/// <summary>Host-level settings for every agent runtime (FR-AGT-01, FR-HST-03).</summary>
public sealed class CopilotRuntimeOptions
{
    public const string SectionName = "Capybara:Runtime";

    /// <summary>State root; each agent gets <c>{StateRoot}/agents/{id}/copilot</c> as its <c>BaseDirectory</c>.</summary>
    public string StateRoot { get; set; } = "/var/lib/capybara";

    /// <summary>
    /// Path to the Copilot CLI, or to the sandbox wrapper (<c>srt</c>) that launches it (FR-AGT-07).
    /// <see langword="null"/> uses the CLI bundled with the SDK.
    /// </summary>
    public string? CliPath { get; set; }

    /// <summary>Arguments passed to <see cref="CliPath"/>, e.g. the wrapper's own arguments followed by the CLI path.</summary>
    public IList<string> CliArgs { get; } = [];

    /// <summary>
    /// Fine-grained PAT with the "Copilot Requests" permission. Never written to config files: supplied by
    /// <c>systemd-creds</c> through <c>$CREDENTIALS_DIRECTORY</c> (FR-HST-03).
    /// </summary>
    public string? GitHubToken { get; set; }

    /// <summary>Skill folders shared by all agents; per-agent skills live under <c>config/agents/{id}/skills</c>.</summary>
    public IList<string> SharedSkillDirectories { get; } = [];

    /// <summary>Root of user-owned config (agents, rules, routines).</summary>
    public string ConfigRoot { get; set; } = "/etc/capybara";

    /// <summary>Tools whose results are untrusted; using one taints the session (FR-SAF-03).</summary>
    public IList<string> UntrustedInputTools { get; } = ["web_fetch", "web_search", "email_read", "calendar_read"];

    /// <summary>Start runtimes and the supervisor with the host. Disabled in tests and for config validation.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Upper bound for one consult turn.</summary>
    public TimeSpan ConsultTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound for one main-session turn.</summary>
    public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromMinutes(20);

    public string AgentBaseDirectory(string agentId) => Path.Combine(StateRoot, "agents", agentId, "copilot");

    public string AgentWorkingDirectory(string agentId) => Path.Combine(StateRoot, "agents", agentId, "workspace");

    public string AgentSkillDirectory(string agentId) => Path.Combine(ConfigRoot, "agents", agentId, "skills");
}
