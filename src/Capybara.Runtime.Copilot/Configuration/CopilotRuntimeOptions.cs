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
    /// Supply <see cref="GitHubToken"/> to each session over RPC (<c>GitHubTokenProvider</c>) instead of to the CLI
    /// process, where the SDK puts it in the environment that every child process (shell, stdio MCP servers) inherits
    /// (FR-SAF-08). <see langword="false"/> is a rollback switch only, for a CLI that rejects client calls without a
    /// process-level token.
    /// </summary>
    public bool SessionScopedGitHubToken { get; set; } = true;

    /// <summary>
    /// Host variables copied into each agent CLI's environment when present (see <see cref="Hosting.CliEnvironment"/>).
    /// Everything else in capybarad's environment is withheld. Credential-like names (<c>*TOKEN*</c>, <c>*SECRET*</c>,
    /// <c>*_KEY</c>, <c>CREDENTIALS_DIRECTORY</c>, <c>Capybara__*</c>, ...) are dropped even if listed here, as are
    /// proxy URLs that embed a user name or password.
    /// </summary>
    public IList<string> CliEnvironmentPassthrough { get; } =
        ["HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "https_proxy", "http_proxy", "no_proxy", "SSL_CERT_FILE", "SSL_CERT_DIR"];

    /// <summary>
    /// How stdio MCP servers are launched (FR-SAF-08). The SDK's per-server <c>Env</c> is merged into the CLI's
    /// environment, so it cannot hide anything; isolation therefore wraps the command in <c>env -i</c>.
    /// Defaults to <see cref="StdioMcpIsolation.ClearEnvironment"/> on Unix and <see cref="StdioMcpIsolation.Refuse"/>
    /// on Windows. Windows stays refused even though the CLI's own environment is now an allowlist
    /// (<see cref="Hosting.CliEnvironment"/>): without an <c>env -i</c> equivalent, a server would still inherit the
    /// CLI's private variables (<c>COPILOT_HOME</c>, and the token itself under the
    /// <see cref="SessionScopedGitHubToken"/> rollback), and Windows is not a production host.
    /// </summary>
    public StdioMcpIsolation StdioMcpIsolation { get; set; } =
        OperatingSystem.IsWindows() ? StdioMcpIsolation.Refuse : StdioMcpIsolation.ClearEnvironment;

    /// <summary>Absolute path of the <c>env</c> binary used for <see cref="StdioMcpIsolation.ClearEnvironment"/>.</summary>
    public string EnvExecutable { get; set; } = "/usr/bin/env";

    /// <summary>
    /// Optional SHA-256 pin for <see cref="EnvExecutable"/>. Without it the launcher is still required to be an absolute,
    /// existing file that is not group- or world-writable.
    /// </summary>
    public string? EnvExecutableSha256 { get; set; }

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

    /// <summary>
    /// How long a stopped turn's abort may take to reach idle. Past it the session is discarded and rebuilt, so no run
    /// outlives its turn.
    /// </summary>
    public TimeSpan AbortTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a failed turn waits for its run's own <c>session.idle</c> before aborting it. After <c>session.error</c>
    /// the SDK throws and the idle follows within milliseconds; aborting an already idle session never signals idle.
    /// </summary>
    public TimeSpan IdleGrace { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a finished turn looks for a run started by a steering message that reached the CLI after the turn's run
    /// ended (FR-AGT-05). A steering message the CLI merged into the run shows no further activity.
    /// </summary>
    public TimeSpan LateRunGrace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the post-start self-test waits for the runtime to report its tools (they are null until tools
    /// initialize). Running out fails the self-test, which pauses the agent (fail closed, FR-AGT-09).
    /// </summary>
    public TimeSpan SelfTestToolsTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan SelfTestPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The agent that receives conversational-channel DMs (FR-CLI-04): the default front door.</summary>
    public string FrontDoorAgent { get; set; } = "ea";

    /// <summary>How long a delivered channel message id is remembered, so a redelivery is dropped (FR-CLI-04).</summary>
    public TimeSpan InboundDedupeRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long a root agent's <c>ask_user</c> waits for the human on the phone channel (FR-A2A-07).</summary>
    public TimeSpan UserEscalationTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Cheap model for the tool-less reviewer session (FR-SAF-05).</summary>
    public string ReviewerModel { get; set; } = "gpt-5-mini";

    /// <summary>Upper bound for one review; running out escalates the call to a human.</summary>
    public TimeSpan ReviewerTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Credit cap for one reviewer session, sent to the SDK raised to its 30-credit minimum. In practice a review is
    /// bounded by having no tools (one model reply) and by <see cref="ReviewerTimeout"/>.
    /// </summary>
    public decimal ReviewerMaxCredits { get; set; } = 0.5m;

    /// <summary>The reviewer's own CLI state, outside every agent's directories.</summary>
    public string ReviewerDirectory => Path.Combine(StateRoot, "reviewer");

    public string AgentBaseDirectory(string agentId) => Path.Combine(StateRoot, "agents", agentId, "copilot");

    public string AgentWorkingDirectory(string agentId) => Path.Combine(StateRoot, "agents", agentId, "workspace");

    /// <summary>Host-owned per-agent state that is neither the CLI's home nor the agent's workspace.</summary>
    public string AgentStateDirectory(string agentId) => Path.Combine(StateRoot, "agents", agentId, "host");

    /// <summary>The agent's private temp directory, exported to its CLI as <c>TMPDIR</c>.</summary>
    public string AgentTempDirectory(string agentId) => Path.Combine(StateRoot, "agents", agentId, "tmp");
}
