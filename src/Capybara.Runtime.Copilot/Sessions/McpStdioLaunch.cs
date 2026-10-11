using Capybara.Contracts.Agents;
using Capybara.Runtime.Copilot.Configuration;
using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Sessions;

/// <summary>
/// Builds the launch line for a stdio MCP server so it starts with an empty environment (FR-SAF-08). The SDK's per-server
/// <c>Env</c> is merged into the CLI's environment (verified against CLI 1.0.95), so the host environment, and any
/// token in it, would otherwise reach third-party code. The server is therefore launched as
/// <c>env -i PATH=… HOME=… LANG=… &lt;verified command&gt; &lt;args&gt;</c>.
/// </summary>
internal static class McpStdioLaunch
{
    private const string DefaultPath = "/usr/local/bin:/usr/bin:/bin";

    /// <exception cref="InvalidOperationException">The server or launcher fails verification, or stdio is refused here.</exception>
    public static McpStdioServerConfig Build(AgentId agent, McpServerSpec server, CopilotRuntimeOptions options, int? timeout)
    {
        var name = $"MCP server '{server.Name}' of {agent}";
        if (options.StdioMcpIsolation != StdioMcpIsolation.ClearEnvironment)
        {
            throw new InvalidOperationException(
                $"{name} is refused: stdio MCP servers need an env -i launcher to hide the host environment, which this platform does not provide.");
        }

        var command = McpServerPins.Verify(agent, server);
        if (command.Contains('=', StringComparison.Ordinal))
        {
            // env would read it as another variable assignment instead of the command.
            throw new InvalidOperationException($"{name}: command path '{command}' must not contain '='.");
        }

        return new McpStdioServerConfig
        {
            Command = VerifyLauncher(options),
            Args =
            [
                "-i",
                $"PATH={(Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } path ? path : DefaultPath)}",
                $"HOME={options.AgentWorkingDirectory(agent)}",
                $"LANG={(Environment.GetEnvironmentVariable("LANG") is { Length: > 0 } lang ? lang : "C.UTF-8")}",
                command,
                .. server.Args,
            ],
            Tools = [.. server.Tools],
            Timeout = timeout,
        };
    }

    /// <summary>The launcher must be absolute, exist, not be writable by others, and match its pin when one is set.</summary>
    private static string VerifyLauncher(CopilotRuntimeOptions options)
    {
        var env = options.EnvExecutable;
        if (!Path.IsPathFullyQualified(env) || !File.Exists(env))
        {
            throw new InvalidOperationException($"The stdio MCP launcher '{env}' must be an existing absolute path.");
        }

        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(env) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InvalidOperationException($"The stdio MCP launcher '{env}' is group- or world-writable.");
        }

        if (options.EnvExecutableSha256 is { Length: > 0 } pinned)
        {
            McpServerPins.VerifyFile("The stdio MCP launcher", env, pinned);
        }

        return env;
    }
}
