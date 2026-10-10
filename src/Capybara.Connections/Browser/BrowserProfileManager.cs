using Microsoft.Playwright;

namespace Capybara.Connections.Browser;

/// <summary>Per-agent persistent Playwright profile directories treated as secrets (FR-CON-09).</summary>
public sealed class BrowserProfileManager(string root)
{
    public string GetProfileDirectory(string agentId)
    {
        var path = Path.Combine(root, Sanitize(agentId));
        Directory.CreateDirectory(path);
        TrySetOwnerOnly(path);
        return path;
    }

    public BrowserTypeLaunchPersistentContextOptions CreateLaunchOptions(string agentId, Uri? noVncTakeoverUrl = null)
    {
        _ = GetProfileDirectory(agentId);
        return new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")),
            Args = noVncTakeoverUrl is null ? [] : [$"--no-vnc-takeover-url={noVncTakeoverUrl}"],
        };
    }

    private static string Sanitize(string agentId) => string.Concat(agentId.Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static void TrySetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
