using System.Diagnostics;
using Capybara.Orchestration.Versioning;
using Capybara.Testing;

namespace Capybara.Orchestration.Tests;

public sealed class GitCliConfigVersioningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Staged_skill_proposals_are_never_committed()
    {
        SkipWithoutGit();
        using var dir = new GitTempDirectory();
        Write(dir.Path, "agents/alpha/agent.yaml", "id: alpha\n");
        Write(dir.Path, "agents/alpha/skills/triage/SKILL.md", "active\n");
        Write(dir.Path, "agents/alpha/skills/pending/evil/SKILL.md", "unreviewed\n");
        using var versioning = new GitCliConfigVersioning(dir.Path);

        await versioning.CommitAsync("first", Ct);

        var tracked = Git(dir.Path, "ls-files");
        Assert.Contains("agents/alpha/agent.yaml", tracked);
        Assert.Contains("agents/alpha/skills/triage/SKILL.md", tracked);
        Assert.DoesNotContain(tracked, f => f.Contains("/pending/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_previously_tracked_staged_skill_is_untracked_on_the_next_commit()
    {
        SkipWithoutGit();
        using var dir = new GitTempDirectory();
        Write(dir.Path, "agents/alpha/agent.yaml", "id: alpha\n");
        Git(dir.Path, "init", "--quiet");
        Write(dir.Path, "agents/alpha/skills/pending/evil/SKILL.md", "unreviewed\n");
        Git(dir.Path, "add", "--all");
        Git(dir.Path, "-c", "user.name=t", "-c", "user.email=t@localhost", "commit", "--quiet", "-m", "legacy");
        using var versioning = new GitCliConfigVersioning(dir.Path);

        await versioning.CommitAsync("second", Ct);

        Assert.DoesNotContain(Git(dir.Path, "ls-files"), f => f.Contains("/pending/", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(dir.Path, "agents", "alpha", "skills", "pending", "evil", "SKILL.md")), "the staged file itself stays on disk");
    }

    /// <summary>Git marks object files read-only, which <see cref="TempDirectory"/> cannot delete on Windows.</summary>
    private sealed class GitTempDirectory : IDisposable
    {
        private readonly TempDirectory _inner = new();

        public string Path => _inner.Path;

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            _inner.Dispose();
        }
    }

    private static void SkipWithoutGit()
    {
        try
        {
            Git(Path.GetTempPath(), "--version");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Assert.Skip("git is not installed.");
        }
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string[] Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : throw new InvalidOperationException($"git {args[0]} failed: {error}");
    }
}
