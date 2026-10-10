using System.Diagnostics;
using Capybara.Contracts.Agents;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using GitHub.Copilot;
using static Capybara.Runtime.Copilot.Tests.SessionConfigCompletenessTests;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// FR-SAF-08 / FR-SKL-04: credentials must not reach child processes, and stdio MCP servers run only pinned code.
/// The SDK merges a stdio server's <c>Env</c> into the CLI's environment, so isolation is an <c>env -i</c> launch line.
/// </summary>
public sealed class CredentialIsolationTests
{
    private const string Token = "github_pat_0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>Deliberately not token-shaped, so only registration with the redactor can make it a known secret.</summary>
    private const string Rotated = "rotated-opaque-credential-42";

    private static GitHubTokenProviderArgs TokenRequest(GitHubTokenRequestReason reason) => new() { Host = "github.com", Reason = reason };

    private static CapybaraSession SessionWith(RuntimeFixture f, McpServerSpec server) =>
        f.Session(TestAgents.Create("ea", a => a with { Tools = ["mcp:local-search"], McpServers = [server] }));

    private static McpServerSpec Server(string command, string? scriptPin = null, bool selfContained = false, params string[] args) => new()
    {
        Name = "local",
        Type = "stdio",
        Command = command,
        Args = args,
        Tools = ["search"],
        Sha256 = Sha256(command),
        ScriptSha256 = scriptPin,
        SelfContained = selfContained,
    };

    [Fact]
    public void Stdio_servers_are_launched_through_env_dash_i_with_only_path_home_and_lang()
    {
        using var f = new RuntimeFixture();
        var exe = WriteExecutable(f, "server.bin");

        var stdio = Assert.IsType<McpStdioServerConfig>(
            f.Configs.CreateConfig(SessionWith(f, Server(exe, selfContained: true, args: ["--port=0", "serve"]))).McpServers!["local"]);

        Assert.Equal(f.Options.EnvExecutable, stdio.Command);
        Assert.Equal("-i", stdio.Args![0]);
        Assert.StartsWith("PATH=", stdio.Args[1], StringComparison.Ordinal);
        Assert.Equal("HOME=" + f.Options.AgentWorkingDirectory("ea"), stdio.Args[2]);
        Assert.StartsWith("LANG=", stdio.Args[3], StringComparison.Ordinal);
        Assert.Equal([exe, "--port=0", "serve"], stdio.Args.Skip(4));
        Assert.Null(stdio.Env);
    }

    [Fact]
    public void Stdio_servers_are_refused_where_the_environment_cannot_be_cleared()
    {
        using var f = new RuntimeFixture();
        f.Options.StdioMcpIsolation = StdioMcpIsolation.Refuse;
        var exe = WriteExecutable(f, "server.bin");

        var ex = Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(SessionWith(f, Server(exe, selfContained: true))));

        Assert.Contains("env -i", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_env_launcher_is_verified_against_its_pin()
    {
        using var f = new RuntimeFixture();
        var exe = WriteExecutable(f, "server.bin");
        var session = SessionWith(f, Server(exe, selfContained: true));

        f.Options.EnvExecutableSha256 = Sha256(f.Options.EnvExecutable);
        Assert.Single(f.Configs.CreateConfig(session).McpServers!);

        File.AppendAllText(f.Options.EnvExecutable, "swapped");
        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(session));

        f.Options.EnvExecutable = "env";
        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(session));
    }

    [Theory]
    [InlineData("nodejs", "/opt/mcp/server.js")]
    [InlineData("bash", "/opt/mcp/run.sh")]
    [InlineData("npx", "-y")]
    public void Unpinned_launchers_are_refused_whatever_they_are_called(string launcher, string firstArg)
    {
        using var f = new RuntimeFixture();
        var command = WriteExecutable(f, launcher);

        var ex = Assert.Throws<InvalidOperationException>(
            () => f.Configs.CreateConfig(SessionWith(f, Server(command, args: [firstArg, "@modelcontextprotocol/server-everything"]))));

        Assert.Contains("scriptSha256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_pins_are_enforced_for_every_non_self_contained_server()
    {
        using var f = new RuntimeFixture();
        var node = WriteExecutable(f, "node");
        var script = WriteExecutable(f, "server.js");
        var pin = Sha256(script);

        Assert.Single(f.Configs.CreateConfig(SessionWith(f, Server(node, pin, args: ["--no-warnings", script]))).McpServers!);

        // npx with a pin still fails: its "script" is a package name, not a pinned file.
        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(SessionWith(f, Server(node, pin, args: ["-y", "some-package"]))));
        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(SessionWith(f, Server(node, pin, args: ["server.js"]))));
        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(SessionWith(f, Server(node, pin, args: []))));

        File.AppendAllText(script, "// trojan");
        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(SessionWith(f, Server(node, pin, args: [script]))));
    }

    [Fact]
    public void Launch_line_really_starts_the_server_with_an_empty_environment()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "env -i isolation is Unix-only; stdio MCP servers are refused on Windows.");
        const string printenv = "/usr/bin/printenv";
        Assert.SkipUnless(File.Exists("/usr/bin/env") && File.Exists(printenv), "env/printenv not installed.");

        using var f = new RuntimeFixture();
        f.Options.EnvExecutable = "/usr/bin/env";
        var stdio = Assert.IsType<McpStdioServerConfig>(
            f.Configs.CreateConfig(SessionWith(f, Server(printenv, selfContained: true))).McpServers!["local"]);

        // Stand in for the CLI: a parent process whose environment carries the credentials.
        var start = new ProcessStartInfo(stdio.Command!) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in stdio.Args!)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["GH_TOKEN"] = Token;
        start.Environment["COPILOT_SDK_AUTH_TOKEN"] = Token;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.DoesNotContain(Token, output, StringComparison.Ordinal);
        Assert.DoesNotContain("GH_TOKEN", output, StringComparison.Ordinal);
        Assert.Contains("HOME=" + f.Options.AgentWorkingDirectory("ea"), output, StringComparison.Ordinal);
        Assert.Equal(["HOME", "LANG", "PATH"], output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l[..l.IndexOf('=', StringComparison.Ordinal)]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GitHub_token_goes_to_sessions_over_rpc_not_into_the_cli_environment()
    {
        using var f = new RuntimeFixture();
        f.Options.GitHubToken = Token;
        var agent = TestAgents.Create("ea");

        Assert.Null(CopilotClientOptionsFactory.Create(agent, f.Options).GitHubToken);

        foreach (SessionConfigBase config in new SessionConfigBase[] { f.Configs.CreateConfig(f.Session(agent)), f.Configs.ResumeConfig(f.Session(agent)) })
        {
            Assert.Null(config.GitHubToken);
            var result = await config.GitHubTokenProvider!(TokenRequest(GitHubTokenRequestReason.Initial));
            Assert.Equal(Token, result.Token?.AccessToken);
            Assert.True(result.Token?.ExpiresIn > 0);
        }

        // A token rotated after the session was built is served on the next request and registered with the redactor,
        // so it can never come back out through tool output either.
        var built = f.Configs.CreateConfig(f.Session(agent));
        f.Options.GitHubToken = Rotated;
        Assert.False(f.Redactor.ContainsSecret(Rotated));
        var rotated = await built.GitHubTokenProvider!(TokenRequest(GitHubTokenRequestReason.Refresh));
        Assert.Equal(Rotated, rotated.Token?.AccessToken);
        Assert.True(f.Redactor.ContainsSecret($"leaked {Rotated} here"));
    }

    [Fact]
    public async Task Session_token_lifetime_is_long_enough_not_to_churn()
    {
        using var f = new RuntimeFixture();
        f.Options.GitHubToken = Token;

        var result = await f.Configs.CreateConfig(f.Session(TestAgents.Create("ea"))).GitHubTokenProvider!(TokenRequest(GitHubTokenRequestReason.Initial));

        Assert.True(result.Token!.ExpiresIn > 3600, $"ExpiresIn was {result.Token.ExpiresIn}s");
    }

    [Fact]
    public void A_configured_script_pin_is_enforced_even_on_a_self_contained_server()
    {
        using var f = new RuntimeFixture();
        var exe = WriteExecutable(f, "server.bin");
        var script = WriteExecutable(f, "plugin.js");
        var pin = Sha256(script);
        var server = Server(exe, pin, selfContained: true, args: [script]);

        Assert.Single(f.Configs.CreateConfig(SessionWith(f, server)).McpServers!);

        File.AppendAllText(script, "// trojan");
        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(SessionWith(f, server)));
    }
    [Fact]
    public void Process_level_token_is_only_a_rollback_switch()
    {
        using var f = new RuntimeFixture();
        f.Options.GitHubToken = Token;
        f.Options.SessionScopedGitHubToken = false;
        var agent = TestAgents.Create("ea");

        Assert.Equal(Token, CopilotClientOptionsFactory.Create(agent, f.Options).GitHubToken);
        Assert.Null(f.Configs.CreateConfig(f.Session(agent)).GitHubTokenProvider);
    }
}
