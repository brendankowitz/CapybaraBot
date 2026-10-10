using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Testing;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Live check against the Copilot CLI bundled with the SDK (FR-SAF-08). Opt-in with <c>CAPYBARA_LIVE_CLI=1</c>: it
/// spawns real processes and mutates this process's environment. A stdio "MCP server" that only dumps its environment
/// shows what every child of the CLI inherits. No session token is supplied: the CLI validates tokens against GitHub,
/// and the environment check does not need an authenticated session.
/// </summary>
public sealed class LiveCliEnvironmentTests
{
    private const string Marker = "CAPYBARA_PROBE_MARKER";

    private static readonly Dictionary<string, string> HostEnvironment = new(StringComparer.Ordinal)
    {
        [Marker] = "host-marker-value",
        ["CREDENTIALS_DIRECTORY"] = "/run/credentials/capybarad.service",
        ["GH_TOKEN"] = "gh-token-from-host",
        ["Capybara__Runtime__GitHubToken"] = "override-from-host",
        ["PROBE_PASSTHROUGH_SETTING"] = "passed-through",
    };

    [Fact]
    public async Task Cli_children_inherit_only_the_allowlisted_environment()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CAPYBARA_LIVE_CLI") == "1", "Set CAPYBARA_LIVE_CLI=1 to run against the bundled Copilot CLI.");
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDirectory();
        var options = new CopilotRuntimeOptions
        {
            StateRoot = Path.Combine(dir.Path, "state"),
            ConfigRoot = Path.Combine(dir.Path, "config"),
            OtlpEndpoint = "http://127.0.0.1:4318",
        };

        // A configured passthrough reaches the CLI; a credential-like name is stripped even when configured.
        options.CliEnvironmentPassthrough.Add("PROBE_PASSTHROUGH_SETTING");
        options.CliEnvironmentPassthrough.Add("GH_TOKEN");
        var agent = TestAgents.Create("ea");
        Directory.CreateDirectory(options.AgentBaseDirectory(agent.Id));
        Directory.CreateDirectory(options.AgentWorkingDirectory(agent.Id));
        var dump = Path.Combine(dir.Path, "child-env.txt");

        var previous = HostEnvironment.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (name, value) in HostEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            await using var client = new CopilotClient(CopilotClientOptionsFactory.Create(agent, options));
            await client.StartAsync(ct);
            await client.PingAsync("probe", ct);

            var events = new System.Collections.Concurrent.ConcurrentBag<string>();
            await using var session = await client.CreateSessionAsync(
                new SessionConfig
                {
                    SessionId = "env-probe",
                    AvailableTools = ["mcp:dump-x"],
                    OnEvent = e => events.Add(e.Type.StartsWith("session.mcp", StringComparison.Ordinal) || e.Type == "session.info" ? e.ToJson() : e.Type),
                    OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("probe")),
                    McpServers = new Dictionary<string, McpServerConfig> { ["dump"] = DumpServer(dump) },
                },
                ct);

            // MCP servers start lazily; asking for the tool list (as the self-test does) brings them up.
            _ = await session.Rpc.Tools.GetCurrentMetadataAsync(ct);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(dump) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, ct);
            }

            TestContext.Current.TestOutputHelper?.WriteLine("events: " + string.Join(", ", events));
            var text = await File.ReadAllTextAsync(dump, ct);
            TestContext.Current.TestOutputHelper?.WriteLine(text);

            Assert.DoesNotContain(Marker, text, StringComparison.Ordinal);
            Assert.DoesNotContain("CREDENTIALS_DIRECTORY", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gh-token-from-host", text, StringComparison.Ordinal);
            Assert.DoesNotContain("override-from-host", text, StringComparison.Ordinal);
            Assert.Contains("PROBE_PASSTHROUGH_SETTING=passed-through", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HOME=" + options.AgentWorkingDirectory(agent.Id), text, StringComparison.OrdinalIgnoreCase);

            // The SDK adds TelemetryConfig's variables after the replacement, so OTel export still works.
            Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    // The temp path has no spaces, so no quoting is needed (the CLI re-quotes arguments that contain them).
    private static McpStdioServerConfig DumpServer(string dump) => OperatingSystem.IsWindows()
        ? new() { Command = Path.Combine(Environment.SystemDirectory, "cmd.exe"), Args = ["/d", "/c", $"set>{dump}"], Tools = ["x"] }
        : new() { Command = "/bin/sh", Args = ["-c", $"env > '{dump}'"], Tools = ["x"] };
}
