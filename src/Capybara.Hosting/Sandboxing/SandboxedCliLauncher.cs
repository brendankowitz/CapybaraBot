namespace Capybara.Hosting.Sandboxing;

/// <summary>Produces command lines that run the Copilot CLI under <c>srt</c> or inside a Podman sandbox (FR-HST-03, spec §6 option b).</summary>
public sealed class SandboxedCliLauncher
{
    public static LaunchCommand ForSrt(string srtPath, string settingsPath, string copilotCliPath, IReadOnlyList<string> copilotArgs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(srtPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(copilotCliPath);
        return new LaunchCommand(srtPath, ["run", "--settings", settingsPath, "--", copilotCliPath, .. copilotArgs]);
    }

    public static LaunchCommand ForPodmanExec(string podmanPath, string containerName, string copilotCliPath, IReadOnlyList<string> copilotArgs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(podmanPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(copilotCliPath);
        return new LaunchCommand(podmanPath, ["exec", containerName, copilotCliPath, .. copilotArgs]);
    }
}

/// <summary>Executable path plus argument list for <c>RuntimeConnection.ForStdio</c> or <c>CopilotCliBinaryPath</c> (FR-HST-03).</summary>
public sealed record LaunchCommand(string Path, IReadOnlyList<string> Arguments);
