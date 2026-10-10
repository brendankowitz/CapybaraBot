using Capybara.Contracts.Agents;
using Capybara.Runtime.Copilot.Configuration;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>
/// The complete environment of an agent's CLI process (FR-SAF-08, FR-HST-03). The SDK's child-process
/// <c>Environment</c> replaces the inherited one, so nothing from capybarad's own environment reaches the CLI or the
/// shell, tool and MCP processes it spawns unless it is listed here. In particular systemd's
/// <c>CREDENTIALS_DIRECTORY</c> and <c>Capybara__*</c> configuration overrides never do.
/// </summary>
/// <remarks>
/// <para>Always set: <c>PATH</c>, <c>LANG</c>, <c>HOME</c> = the agent's workspace (the BaseDirectory is the CLI's
/// private state; tools that write to <c>~</c> must not land there), and an agent-scoped temp directory as
/// <c>TMPDIR</c> (<c>TEMP</c>/<c>TMP</c> on Windows).</para>
/// <para>Passed through when present on the host: <see cref="CopilotRuntimeOptions.CliEnvironmentPassthrough"/> (proxy
/// and CA settings by default) and, on Windows only, the variables the OS loader needs. A passthrough name that looks
/// like a credential is always dropped, as is a value of a <c>*_PROXY</c> variable (any case) containing '@', which is how a
/// user name or password is embedded, with or without a scheme. Other variables, such as a CA path, may contain '@'.</para>
/// </remarks>
public static class CliEnvironment
{
    /// <summary>Variables Windows processes need to start and resolve system paths. None carries a secret.</summary>
    private static readonly string[] WindowsEssentials =
        ["SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "OS"];

    private static readonly string[] CredentialMarkers = ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL"];

    public static IReadOnlyDictionary<string, string> Build(AgentId agent, CopilotRuntimeOptions options) =>
        Build(agent, options, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());

    /// <param name="agent">The agent whose CLI is being launched.</param>
    /// <param name="options">Runtime options: agent directories and the passthrough list.</param>
    /// <param name="host">Reads a variable from capybarad's own environment.</param>
    /// <param name="windows">Whether the CLI runs on Windows.</param>
    public static IReadOnlyDictionary<string, string> Build(AgentId agent, CopilotRuntimeOptions options, Func<string, string?> host, bool windows)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);

        var home = options.AgentWorkingDirectory(agent);
        var temp = options.AgentTempDirectory(agent);
        var env = new Dictionary<string, string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["PATH"] = host("PATH") is { Length: > 0 } path ? path : "/usr/local/bin:/usr/bin:/bin",
            ["LANG"] = host("LANG") is { Length: > 0 } lang ? lang : "C.UTF-8",
            ["HOME"] = home,
        };

        if (windows)
        {
            env["USERPROFILE"] = home;
            env["TEMP"] = temp;
            env["TMP"] = temp;
            foreach (var name in WindowsEssentials)
            {
                CopyFromHost(env, host, name);
            }
        }
        else
        {
            env["TMPDIR"] = temp;
        }

        foreach (var name in options.CliEnvironmentPassthrough.Where(n => !IsDenied(n)))
        {
            if (host(name) is { Length: > 0 } value && !(IsProxyName(name) && HasUserInfo(value)))
            {
                env.TryAdd(name, value);
            }
        }

        return env;
    }

    /// <summary>Names that can never be passed through, whatever the configuration says.</summary>
    public static bool IsDenied(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Equals("CREDENTIALS_DIRECTORY", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Capybara__", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("CAPYBARA_", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
            || CredentialMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A proxy value with userinfo is a credential, not a setting. Any '@' drops it, scheme-less forms included (<see cref="Uri"/> reads the user part of such a value as a scheme).</summary>
    private static bool HasUserInfo(string value) => value.Contains('@');

    private static bool IsProxyName(string name) => name.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase);

    private static void CopyFromHost(Dictionary<string, string> env, Func<string, string?> host, string name)
    {
        if (host(name) is { Length: > 0 } value)
        {
            env[name] = value;
        }
    }
}
