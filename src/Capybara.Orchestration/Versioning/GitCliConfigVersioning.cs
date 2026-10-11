using System.Diagnostics;

namespace Capybara.Orchestration.Versioning;

/// <summary>
/// Commits the config root with the <c>git</c> CLI. Arguments are passed as a list, never through a shell. Staged skill
/// proposals (<c>agents/*/skills/pending/</c>) are unreviewed agent output, so they are excluded through
/// <c>.git/info/exclude</c> and untracked if an earlier commit picked them up (FR-SKL-03).
/// </summary>
public sealed class GitCliConfigVersioning(string root) : IConfigVersioning, IDisposable
{
    /// <summary>gitignore pattern for staged skills; <c>*</c> does not cross <c>/</c>, so only direct agent folders match.</summary>
    private const string PendingSkillsPattern = "/agents/*/skills/pending/";

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

            EnsureExcluded();
            await RunAsync(cancellationToken, "rm", "-r", "--cached", "--quiet", "--ignore-unmatch", "--", ":(glob)agents/*/skills/pending/**");
            await RunAsync(cancellationToken, "add", "--all");
            await RunAsync(cancellationToken, "-c", "user.name=capybara-config", "-c", "user.email=capybara@localhost", "commit", "--quiet", "--allow-empty", "-m", message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private void EnsureExcluded()
    {
        var exclude = Path.Combine(root, ".git", "info", "exclude");
        var lines = File.Exists(exclude) ? File.ReadAllLines(exclude) : [];
        if (!lines.Contains(PendingSkillsPattern, StringComparer.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            File.AppendAllLines(exclude, ["# Unapproved skill proposals (FR-SKL-03); never committed.", PendingSkillsPattern]);
        }
    }

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