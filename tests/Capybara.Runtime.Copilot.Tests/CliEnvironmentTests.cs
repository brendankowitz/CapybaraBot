using Capybara.Contracts.Agents;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Testing;
using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>FR-SAF-08: the CLI's environment is an allowlist, never capybarad's own environment.</summary>
public sealed class CliEnvironmentTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static readonly Dictionary<string, string> Host = new(StringComparer.Ordinal)
    {
        ["PATH"] = "/usr/bin:/bin",
        ["LANG"] = "en_US.UTF-8",
        ["HOME"] = "/var/lib/capybara",
        ["CREDENTIALS_DIRECTORY"] = "/run/credentials/capybarad.service",
        ["GH_TOKEN"] = "host-token",
        ["Capybara__Runtime__GitHubToken"] = "host-override",
        ["OPENAI_API_KEY"] = "host-key",
        ["AZURE_CLIENT_SECRET"] = "host-secret",
        ["HTTPS_PROXY"] = "http://proxy.internal:3128",
        ["HTTP_PROXY"] = "http://svc:hunter2@proxy.internal:3128",
        ["SSL_CERT_FILE"] = "/etc/ssl/certs/ca.pem",
        ["SystemRoot"] = @"C:\Windows",
        ["UNRELATED"] = "host-marker",
    };

    private static CopilotRuntimeOptions Options() => new() { StateRoot = "/var/lib/capybara" };

    private static string? Lookup(string name) => Host.GetValueOrDefault(name);

    [Fact]
    public void Unix_environment_is_exactly_the_allowlist()
    {
        var options = Options();

        var env = CliEnvironment.Build(Ea, options, Lookup, windows: false);

        Assert.Equal(["HOME", "HTTPS_PROXY", "LANG", "PATH", "SSL_CERT_FILE", "TMPDIR"], env.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(options.AgentWorkingDirectory("ea"), env["HOME"]);
        Assert.Equal(options.AgentTempDirectory("ea"), env["TMPDIR"]);
        Assert.Equal("/usr/bin:/bin", env["PATH"]);
        Assert.Equal("en_US.UTF-8", env["LANG"]);
        Assert.DoesNotContain(env.Values, v => v.StartsWith("host-", StringComparison.Ordinal));
    }

    [Fact]
    public void Credential_like_names_are_stripped_even_when_configured_for_passthrough()
    {
        var options = Options();
        foreach (var name in new[] { "CREDENTIALS_DIRECTORY", "GH_TOKEN", "Capybara__Runtime__GitHubToken", "OPENAI_API_KEY", "AZURE_CLIENT_SECRET", "UNRELATED" })
        {
            options.CliEnvironmentPassthrough.Add(name);
        }

        var env = CliEnvironment.Build(Ea, options, Lookup, windows: false);

        Assert.Equal("host-marker", env["UNRELATED"]);
        Assert.Equal(["HOME", "HTTPS_PROXY", "LANG", "PATH", "SSL_CERT_FILE", "TMPDIR", "UNRELATED"], env.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("CREDENTIALS_DIRECTORY", true)]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("COPILOT_SDK_AUTH_TOKEN", true)]
    [InlineData("AWS_SECRET_ACCESS_KEY", true)]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("PGPASSWORD", true)]
    [InlineData("Capybara__Gateway__Devices__0__Sha256", true)]
    [InlineData("capybara_anything", true)]
    [InlineData("HTTPS_PROXY", false)]
    [InlineData("SSL_CERT_DIR", false)]
    [InlineData("KEYBOARD_LAYOUT", false)]
    public void Denylist_matches_credential_names(string name, bool denied) => Assert.Equal(denied, CliEnvironment.IsDenied(name));

    [Fact]
    public void Proxy_urls_with_credentials_are_dropped()
    {
        var env = CliEnvironment.Build(Ea, Options(), Lookup, windows: false);

        Assert.False(env.ContainsKey("HTTP_PROXY"));
        Assert.Equal("http://proxy.internal:3128", env["HTTPS_PROXY"]);
    }

    [Theory]
    [InlineData("user:pass@proxy.internal:3128")]
    [InlineData("user@proxy.internal:3128")]
    [InlineData("socks5://user:pass@proxy.internal:1080")]
    [InlineData("http://proxy.internal:3128/path@x")]
    public void Proxy_values_with_userinfo_are_dropped_with_or_without_a_scheme(string value)
    {
        var host = new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["HTTP_PROXY"] = value, ["ALL_PROXY"] = value };

        var env = CliEnvironment.Build(Ea, Options(), host.GetValueOrDefault, windows: false);

        Assert.DoesNotContain(env.Values, v => v.Contains('@', StringComparison.Ordinal));
        Assert.False(env.ContainsKey("HTTP_PROXY"));
    }

    [Fact]
    public void Windows_adds_its_loader_variables_and_agent_scoped_profile_and_temp()
    {
        var options = Options();

        var env = CliEnvironment.Build(Ea, options, Lookup, windows: true);

        Assert.Equal(@"C:\Windows", env["SystemRoot"]);
        Assert.Equal(options.AgentWorkingDirectory("ea"), env["USERPROFILE"]);
        Assert.Equal(options.AgentTempDirectory("ea"), env["TEMP"]);
        Assert.Equal(options.AgentTempDirectory("ea"), env["tmp"]);
        Assert.False(env.ContainsKey("TMPDIR"));
        Assert.False(env.ContainsKey("CREDENTIALS_DIRECTORY"));
    }

    [Fact]
    public void Client_always_uses_an_explicit_child_process_connection_with_the_allowlisted_environment()
    {
        using var dir = new TempDirectory();
        var options = new CopilotRuntimeOptions { StateRoot = dir.Path };
        var agent = TestAgents.Create("ea");

        var bundled = Assert.IsType<StdioRuntimeConnection>(CopilotClientOptionsFactory.Create(agent, options).Connection);

        Assert.Null(bundled.Path);
        Assert.Equal(CliEnvironment.Build(agent.Id, options), bundled.Environment);
        Assert.True(Directory.Exists(options.AgentTempDirectory("ea")));

        options.CliPath = "/usr/local/bin/srt";
        options.CliArgs.Add("--settings=/etc/capybara/srt/ea.json");
        var wrapped = Assert.IsType<StdioRuntimeConnection>(CopilotClientOptionsFactory.Create(agent, options).Connection);
        Assert.Equal("/usr/local/bin/srt", wrapped.Path);
        Assert.Equal(["--settings=/etc/capybara/srt/ea.json"], wrapped.Args);
        Assert.NotNull(wrapped.Environment);
    }
}
