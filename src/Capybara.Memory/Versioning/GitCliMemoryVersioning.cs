using System.Diagnostics;

namespace Capybara.Memory.Versioning;

/// <summary>Commits the memory root with the <c>git</c> CLI. Arguments are passed as a list, never through a shell.</summary>
public sealed class GitCliMemoryVersioning(string root) : IMemoryVersioning, IDisposable
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
            await RunAsync(cancellationToken, "-c", "user.name=capybara-memory", "-c", "user.email=capybara@localhost", "commit", "--quiet", "--allow-empty", "-m", message);
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
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"git {args[0]} failed: {error}");
        }
    }
}
