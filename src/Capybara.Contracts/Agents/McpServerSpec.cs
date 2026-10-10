namespace Capybara.Contracts.Agents;

/// <summary>
/// A vendored MCP server attached to an agent's sessions (FR-CON-03, FR-SKL-04). Tools are enabled individually,
/// stdio servers are pinned by the SHA-256 of their executable, and nothing is installed at runtime.
/// </summary>
public sealed record McpServerSpec
{
    public required string Name { get; init; }

    /// <summary><c>stdio</c> or <c>http</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Absolute path to the vendored executable (stdio).</summary>
    public string? Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Loopback URL (http), typically the connection broker's per-agent MCP endpoint.</summary>
    public string? Url { get; init; }

    /// <summary>Individually enabled tools. Empty means none.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Lower-case hex SHA-256 of <see cref="Command"/>. Required for stdio servers.</summary>
    public string? Sha256 { get; init; }

    /// <summary>
    /// Lower-case hex SHA-256 of the script the command runs, i.e. its first positional argument (after options and a
    /// leading <c>run</c>/<c>exec</c>). Required for every stdio server that is not <see cref="SelfContained"/> (FR-SKL-04).
    /// </summary>
    public string? ScriptSha256 { get; init; }

    /// <summary>
    /// Declares <see cref="Command"/> a self-contained binary whose own pin covers all the code it runs. Without it a
    /// stdio server must also pin its script, so an unlisted interpreter or launcher (<c>bash</c>, <c>npx</c>,
    /// <c>nodejs</c>) can never run unpinned code. Mutually exclusive with <see cref="ScriptSha256"/> in config; if both
    /// are set anyway, the script pin is still enforced.
    /// </summary>
    public bool SelfContained { get; init; }

    /// <summary>Results from this server are untrusted input and taint the session (FR-SAF-03).</summary>
    public bool Untrusted { get; init; } = true;

    /// <summary>Name of the secret (in the secret store) sent as a bearer token to an http server.</summary>
    public string? TokenSecret { get; init; }

    public int? TimeoutSeconds { get; init; }

    /// <summary>Source-qualified tool names as they appear in allowlists, e.g. <c>mcp:workiq-ask_work_iq</c>.</summary>
    public IEnumerable<string> QualifiedTools() => Tools.Select(t => $"mcp:{Name}-{t}");

    /// <summary>Bare tool names as reported by the runtime, e.g. <c>workiq-ask_work_iq</c>.</summary>
    public IEnumerable<string> RuntimeToolNames() => Tools.Select(t => $"{Name}-{t}");
}
