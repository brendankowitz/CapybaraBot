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

    /// <remarks>
    /// Creates the agent's temp directory, because the CLI's <c>TMPDIR</c> points at it (<see cref="CliEnvironment"/>).
    /// </remarks>
    public static CopilotClientOptions Create(AgentDefinition agent, CopilotRuntimeOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(options.AgentTempDirectory(agent.Id));

        // Always an explicit child-process connection: its Environment replaces capybarad's, so host secrets such as
        // CREDENTIALS_DIRECTORY never reach the CLI or the shell and MCP processes it spawns (FR-SAF-08).
        // A null path launches the CLI bundled with the SDK; a configured path is typically the sandbox wrapper (FR-AGT-07).
        var connection = RuntimeConnection.ForStdio(options.CliPath is { Length: > 0 } cli ? cli : null, [.. options.CliArgs]);
        connection.Environment = CliEnvironment.Build(agent.Id, options);

        return new CopilotClientOptions
        {
            // Empty mode: nothing ambient. No default tools, no discovered config, no shared ~/.copilot.
            Mode = CopilotClientMode.Empty,
            Connection = connection,
            BaseDirectory = options.AgentBaseDirectory(agent.Id),
            WorkingDirectory = options.AgentWorkingDirectory(agent.Id),

            // Session-scoped by default: a process-level token is put in the CLI's environment, which every child inherits.
            GitHubToken = options.SessionScopedGitHubToken ? null : options.GitHubToken,
            UseLoggedInUser = false,

            // Mission Control access is opt-in per agent; the runtime only offers it inside a GitHub repo (FR-CLI-08).
            EnableRemoteSessions = agent.EnableRemoteSessions,
            Telemetry = TelemetryFor(options),
            Logger = logger,
        };
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
