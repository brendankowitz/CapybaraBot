using System.Text.RegularExpressions;
using Capybara.Contracts.Agents;
using Capybara.Hosting.Agents;
using Capybara.Hosting.DependencyInjection;

namespace Capybara.Hosting.Sandboxing;

/// <summary>Where the sandbox tooling lives, where per-agent <c>srt</c> settings are written, and what agents must not read (FR-HST-03, FR-SAF-07).</summary>
/// <param name="SrtPath">Path to the <c>srt</c> executable.</param>
/// <param name="CopilotCliPath">
/// Path to the Copilot CLI that runs inside the sandbox. It must be readable from inside the sandbox, so it must not sit under
/// <see cref="DenyRead"/> (install it outside the state root, e.g. under <c>/opt</c>).
/// </param>
/// <param name="SettingsDirectory">
/// Directory receiving <c>{agentId}.srt.json</c>; created on demand. It must not lie under an agent's workspace or BaseDirectory,
/// otherwise the agent could rewrite its own policy.
/// </param>
/// <param name="DenyRead">
/// Paths hidden from every sandboxed agent. srt allows reads everywhere unless denied, so this is what keeps an agent away from
/// other agents' COPILOT_HOMEs, the memory repo and state databases (all under the state root), the configuration and the decrypted
/// credentials. There is deliberately no default: a hard-coded state root would silently lose isolation when the state root is relocated.
/// Build it with <see cref="FromHosting"/> or <see cref="DefaultDenyRead"/>. An agent's own workspace and BaseDirectory are re-allowed per agent,
/// and <see cref="AgentSandboxPlanner.PlanAsync"/> refuses agents whose directories are not under a denied path.
/// </param>
public sealed record AgentSandboxOptions(string SrtPath, string CopilotCliPath, string SettingsDirectory, IReadOnlyList<string> DenyRead)
{
    /// <summary>
    /// Options whose read denials follow <paramref name="hosting"/>'s state root, so they stay correct when it is relocated.
    /// <paramref name="settingsDirectory"/> should sit under the state root but outside every agent directory (for example <c>{StateRoot}/srt</c>).
    /// </summary>
    public static AgentSandboxOptions FromHosting(HostingOptions hosting, string srtPath, string copilotCliPath, string settingsDirectory, string configRoot = "/etc/capybara", string? credentialsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(hosting);
        return new AgentSandboxOptions(srtPath, copilotCliPath, settingsDirectory, DefaultDenyRead(hosting.StateRoot, configRoot, credentialsDirectory));
    }

    /// <summary>
    /// <paramref name="stateRoot"/>, <paramref name="configRoot"/>, <c>/run/credentials</c> (systemd <c>LoadCredential</c> output) and
    /// <paramref name="credentialsDirectory"/> (default <c>$CREDENTIALS_DIRECTORY</c>) when set.
    /// </summary>
    public static IReadOnlyList<string> DefaultDenyRead(string stateRoot, string configRoot = "/etc/capybara", string? credentialsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
        var paths = new List<string> { stateRoot, configRoot, "/run/credentials" };
        var credentials = credentialsDirectory ?? Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        if (!string.IsNullOrEmpty(credentials))
        {
            paths.Add(credentials);
        }

        return [.. paths.Distinct(StringComparer.Ordinal)];
    }
}

/// <summary>The generated settings file and the sandboxed command line that uses it.</summary>
public sealed record AgentSandboxPlan(string SettingsPath, LaunchCommand Launch);

/// <summary>
/// Turns an <see cref="AgentDefinition"/> into a runnable <c>srt</c> sandbox (FR-SAF-07, FR-HST-03):
/// <list type="bullet">
/// <item>egress allowlist = the agent's <see cref="AgentDefinition.EgressDomains"/> (none means no network);</item>
/// <item>write allowlist = exactly the agent's workspace plus its BaseDirectory (COPILOT_HOME); the settings file is also write-denied;</item>
/// <item>read denials = <see cref="AgentSandboxOptions.DenyRead"/>, with the agent's own workspace and BaseDirectory re-allowed.</item>
/// </list>
/// The launch command is an executable plus an argument list — never a shell string — ready for
/// <c>RuntimeConnection.ForStdio</c> or <c>CopilotCliBinaryPath</c>.
/// </summary>
public sealed partial class AgentSandboxPlanner(AgentSandboxOptions options)
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Writes the agent's srt settings file (replacing any previous one atomically) and returns the sandboxed launch command.</summary>
    /// <param name="agent">The agent to sandbox.</param>
    /// <param name="baseDirectory">The agent's COPILOT_HOME (<c>CopilotRuntimeOptions.AgentBaseDirectory</c>).</param>
    /// <param name="workspaceDirectory">The agent's working directory.</param>
    /// <param name="copilotArgs">Arguments passed to the Copilot CLI after its path.</param>
    /// <param name="cancellationToken">Cancels writing the settings file.</param>
    /// <exception cref="ArgumentException">
    /// The agent id, a directory or an egress domain is invalid; an agent directory is not under any <see cref="AgentSandboxOptions.DenyRead"/> path;
    /// or the settings directory lies inside a path the agent can write.
    /// </exception>
    public async Task<AgentSandboxPlan> PlanAsync(
        AgentDefinition agent,
        string baseDirectory,
        string workspaceDirectory,
        IReadOnlyList<string>? copilotArgs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        var agentId = agent.Id.Value;
        AgentIdValidator.Validate(agentId);

        var settingsDirectory = Path.GetFullPath(options.SettingsDirectory);
        var denyRead = options.DenyRead.Select(Path.GetFullPath).ToArray();
        string[] own = [.. new[] { Path.GetFullPath(workspaceDirectory), Path.GetFullPath(baseDirectory) }.Distinct(StringComparer.Ordinal)];
        foreach (var directory in own)
        {
            // Fail closed: srt reads are allowed unless denied, so an agent directory outside every denied path means its neighbours are readable.
            if (!denyRead.Any(denied => IsSameOrInside(directory, denied)))
            {
                throw new ArgumentException($"Agent '{agentId}' directory '{directory}' is not under any read-denied path ({string.Join(", ", denyRead)}); the sandbox would not isolate it from other agents. Set AgentSandboxOptions.DenyRead from the state root (AgentSandboxOptions.FromHosting).");
            }

            if (IsSameOrInside(settingsDirectory, directory))
            {
                throw new ArgumentException($"Sandbox settings directory '{settingsDirectory}' lies inside '{directory}', which agent '{agentId}' can write; the agent could rewrite its own policy.");
            }
        }

        var settingsPath = Path.Combine(settingsDirectory, $"{agentId}.srt.json");
        var policy = new SrtPolicy(
            agentId,
            NormalizeDomains(agent.EgressDomains),
            WriteAllowlist: own,
            DenyRead: denyRead,
            AllowRead: own,
            DenyWrite: [settingsPath]);

        Directory.CreateDirectory(settingsDirectory);
        var temp = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, SrtPolicyBuilder.Build(policy), cancellationToken);
            File.Move(temp, settingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }

        return new AgentSandboxPlan(settingsPath, SandboxedCliLauncher.ForSrt(options.SrtPath, settingsPath, options.CopilotCliPath, copilotArgs ?? []));
    }

    private static bool IsSameOrInside(string path, string container)
    {
        var root = Path.TrimEndingDirectorySeparator(container) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, PathComparison) || string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(container), PathComparison);
    }

    private static string[] NormalizeDomains(IReadOnlyList<string> domains)
    {
        var result = new List<string>(domains.Count);
        foreach (var raw in domains)
        {
            var domain = raw?.Trim() ?? string.Empty;
            if (domain.Length == 0 || domain.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or ':'))
            {
                throw new ArgumentException($"Egress domain '{raw}' must be a bare host name or wildcard such as '*.example.com'.", nameof(domains));
            }

            if (domain == "*" || TopLevelWildcard().IsMatch(domain))
            {
                throw new ArgumentException($"Egress domain '{raw}' would allow an entire top-level domain or everything; list specific hosts or a wildcard under a registrable domain.", nameof(domains));
            }

            if (!result.Contains(domain, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(domain);
            }
        }

        return [.. result];
    }

    [GeneratedRegex(@"^\*\.?[^.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TopLevelWildcard();
}
