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

    /// <summary>
    /// Built-in skill tier shipped with the binary (FR-SKL-01). The shared tier is <c>{ConfigRoot}/skills</c> and the
    /// per-agent tier <c>{ConfigRoot}/agents/{id}/skills</c>; both must match their lock-file pins (FR-SKL-04).
    /// </summary>
    public string BuiltInSkillDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "skills");

    /// <summary>Root of user-owned config (agents, rules, routines).</summary>
    public string ConfigRoot { get; set; } = "/etc/capybara";

    /// <summary>
    /// Tool count above which MCP and external tools are deferred behind <c>tool_search_tool</c> (FR-SKL-02).
    /// <see langword="null"/> keeps the runtime default.
    /// </summary>
    public int? ToolSearchDeferThreshold { get; set; }

    /// <summary>
    /// OTLP endpoint the Copilot CLI exports its spans to (FR-OBS-01). <see langword="null"/> leaves SDK telemetry off.
    /// Message content is never captured (FR-OBS-04).
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>OTLP HTTP protocol: <c>http/protobuf</c> or <c>http/json</c>.</summary>
    public string OtlpProtocol { get; set; } = "http/protobuf";

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

    /// <summary>Host-owned per-agent state that is neither the CLI's home nor the agent's workspace.</summary>
    public string AgentStateDirectory(string agentId) => Path.Combine(StateRoot, "agents", agentId, "host");
}
