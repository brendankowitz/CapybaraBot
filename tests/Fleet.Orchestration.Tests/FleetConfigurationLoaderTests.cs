using Fleet.Contracts.Agents;
using Fleet.Orchestration.Configuration;
using Fleet.Testing;

namespace Fleet.Orchestration.Tests;

public sealed class FleetConfigurationLoaderTests
{
    [Fact]
    public void Sample_config_is_valid()
    {
        var config = FleetConfigurationLoader.Load(RepositoryPaths.SampleConfig);

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

        var ex = Assert.Throws<FleetConfigurationException>(() => FleetConfigurationLoader.Load(dir.Path));

        Assert.True(ex.Errors.Count >= 3, string.Join('\n', ex.Errors));
    }
}
