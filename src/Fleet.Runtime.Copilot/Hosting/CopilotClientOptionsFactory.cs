using Fleet.Contracts.Agents;
using Fleet.Runtime.Copilot.Configuration;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;

namespace Fleet.Runtime.Copilot.Hosting;

/// <summary>One isolated CLI process per agent (FR-AGT-01).</summary>
public static class CopilotClientOptionsFactory
{
    public static CopilotClientOptions Create(AgentDefinition agent, CopilotRuntimeOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(options);

        var clientOptions = new CopilotClientOptions
        {
            // Empty mode: nothing ambient. No default tools, no discovered config, no shared ~/.copilot.
            Mode = CopilotClientMode.Empty,
            BaseDirectory = options.AgentBaseDirectory(agent.Id),
            WorkingDirectory = options.AgentWorkingDirectory(agent.Id),
            GitHubToken = options.GitHubToken,
            UseLoggedInUser = false,
            EnableRemoteSessions = false,
            Logger = logger,
        };

        if (options.CliPath is { Length: > 0 } cli)
        {
            // Typically the sandbox wrapper, which in turn launches the CLI (FR-AGT-07).
            clientOptions.Connection = RuntimeConnection.ForStdio(cli, [.. options.CliArgs]);
        }

        return clientOptions;
    }
}
