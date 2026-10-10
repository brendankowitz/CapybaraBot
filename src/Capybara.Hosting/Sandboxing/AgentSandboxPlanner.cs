using Capybara.Contracts.Agents;
using Capybara.Hosting.Agents;

namespace Capybara.Hosting.Sandboxing;

/// <summary>Where the sandbox tooling lives and where per-agent <c>srt</c> settings files are written (FR-HST-03).</summary>
/// <param name="SrtPath">Path to the <c>srt</c> executable.</param>
/// <param name="CopilotCliPath">Path to the Copilot CLI that runs inside the sandbox.</param>
/// <param name="SettingsDirectory">Directory receiving <c>{agentId}.srt.json</c>; created on demand.</param>
public sealed record AgentSandboxOptions(string SrtPath, string CopilotCliPath, string SettingsDirectory);

/// <summary>The generated settings file and the sandboxed command line that uses it.</summary>
public sealed record AgentSandboxPlan(string SettingsPath, LaunchCommand Launch);

/// <summary>
/// Turns an <see cref="AgentDefinition"/> into a runnable <c>srt</c> sandbox (FR-SAF-07, FR-HST-03): the egress allowlist is the
/// agent's <see cref="AgentDefinition.EgressDomains"/> (none means no network) and the write allowlist is exactly the agent's workspace plus its
/// BaseDirectory (COPILOT_HOME). The launch command is an executable plus an argument list — never a shell string — ready for
/// <c>RuntimeConnection.ForStdio</c> or <c>CopilotCliBinaryPath</c>.
/// </summary>
public sealed class AgentSandboxPlanner(AgentSandboxOptions options)
{
    /// <summary>Writes the agent's srt settings file (replacing any previous one atomically) and returns the sandboxed launch command.</summary>
    /// <param name="agent">The agent to sandbox.</param>
    /// <param name="baseDirectory">The agent's COPILOT_HOME (<c>CopilotRuntimeOptions.AgentBaseDirectory</c>).</param>
    /// <param name="workspaceDirectory">The agent's working directory.</param>
    /// <param name="copilotArgs">Arguments passed to the Copilot CLI after its path.</param>
    /// <param name="cancellationToken">Cancels writing the settings file.</param>
    /// <exception cref="ArgumentException">The agent id, a directory or an egress domain is invalid.</exception>
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

        var policy = new SrtPolicy(
            agentId,
            NormalizeDomains(agent.EgressDomains),
            [.. new[] { Path.GetFullPath(workspaceDirectory), Path.GetFullPath(baseDirectory) }.Distinct(StringComparer.Ordinal)]);

        Directory.CreateDirectory(options.SettingsDirectory);
        var settingsPath = Path.Combine(Path.GetFullPath(options.SettingsDirectory), $"{agentId}.srt.json");
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

            if (!result.Contains(domain, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(domain);
            }
        }

        return [.. result];
    }
}
