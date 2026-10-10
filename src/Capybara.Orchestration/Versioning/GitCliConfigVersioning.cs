using System.Diagnostics;

namespace Capybara.Orchestration.Versioning;

/// <summary>Commits the config root with the <c>git</c> CLI. Arguments are passed as a list, never through a shell.</summary>
public sealed class GitCliConfigVersioning(string root) : IConfigVersioning, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async ValueTask CommitAsync(string message, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(Path.Combine(root, ".git")))
            {
                await RunAsync(cancellationToken, "init", "--quiet");
            }

            await RunAsync(cancellationToken, "add", "--all");
            await RunAsync(cancellationToken, "-c", "user.name=capybara-config", "-c", "user.email=capybara@localhost", "commit", "--quiet", "--allow-empty", "-m", message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start git.");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args[0]} failed: {await stderr}");
        }
    }
}
