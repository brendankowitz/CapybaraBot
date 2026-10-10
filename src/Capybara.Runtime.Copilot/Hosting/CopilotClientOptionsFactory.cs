using Capybara.Contracts.Agents;
using Capybara.Contracts.Diagnostics;
using Capybara.Runtime.Copilot.Configuration;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>One isolated CLI process per agent (FR-AGT-01).</summary>
public static class CopilotClientOptionsFactory
{
    /// <summary>Span source name for the CLI's own OpenTelemetry spans, under the fleet prefix.</summary>
    public const string TelemetrySourceName = CapybaraTelemetry.Prefix + ".Copilot";

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

            // Mission Control access is opt-in per agent; the runtime only offers it inside a GitHub repo (FR-CLI-08).
            EnableRemoteSessions = agent.EnableRemoteSessions,
            Telemetry = TelemetryFor(options),
            Logger = logger,
        };

        if (options.CliPath is { Length: > 0 } cli)
        {
            // Typically the sandbox wrapper, which in turn launches the CLI (FR-AGT-07).
            clientOptions.Connection = RuntimeConnection.ForStdio(cli, [.. options.CliArgs]);
        }

        return clientOptions;
    }

    /// <summary>
    /// LLM and tool spans from the CLI go to the same OTLP collector as the host's spans (FR-OBS-01). Content capture is
    /// always off: prompts, tool arguments and results never leave the box through telemetry (FR-OBS-04).
    /// </summary>
    private static TelemetryConfig? TelemetryFor(CopilotRuntimeOptions options) =>
        options.OtlpEndpoint is { Length: > 0 } endpoint
            ? new TelemetryConfig
            {
                ExporterType = "otlp-http",
                OtlpEndpoint = endpoint,
                OtlpProtocol = options.OtlpProtocol,
                SourceName = TelemetrySourceName,
                CaptureContent = false,
            }
            : null;
}
