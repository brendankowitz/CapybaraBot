using Capybara.Contracts.Agents;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Testing;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>FR-AGT-06: an approved config edit reaches a running agent, restarting its CLI only when it has to.</summary>
public sealed class DefinitionUpdateTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea", a => a with { EgressDomains = ["api.example.com"], Model = "gpt-5" });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void An_identical_definition_is_no_change_even_as_a_different_instance()
    {
        var copy = Ea with { Tools = [.. Ea.Tools], EgressDomains = [.. Ea.EgressDomains] };

        Assert.Equal(DefinitionChange.None, DefinitionChanges.Classify(Ea, copy));
    }

    [Theory]
    [InlineData("soul")]
    [InlineData("rules")]
    [InlineData("tools")]
    [InlineData("model")]
    [InlineData("approval")]
    public void Session_level_edits_only_rebuild_the_main_session(string field)
    {
        var next = field switch
        {
            "soul" => Ea with { Soul = "Warmer." },
            "rules" => Ea with { OperatingRules = "Never send mail." },
            "tools" => Ea with { Tools = [.. Ea.Tools.Where(t => t != "remember")] },
            "model" => Ea with { Model = "claude-sonnet" },
            _ => Ea with { ApprovalRequiredTools = ["send_message"] },
        };

        Assert.Equal(DefinitionChange.Sessions, DefinitionChanges.Classify(Ea, next));
    }

    [Theory]
    [InlineData("egress")]
    [InlineData("mcp")]
    [InlineData("provider")]
    public void Edits_fixed_at_process_start_restart_the_cli(string field)
    {
        var next = field switch
        {
            "egress" => Ea with { EgressDomains = [] },
            "mcp" => Ea with { McpServers = [new McpServerSpec { Name = "docs", Type = "http", Url = "https://docs.example.com/mcp" }] },
            _ => Ea with { Provider = new ModelProviderSpec { Type = "openai", BaseUrl = "https://api.openai.com/v1" } },
        };

        Assert.Equal(DefinitionChange.Process, DefinitionChanges.Classify(Ea, next));
    }

    [Fact]
    public void Another_agents_definition_is_refused()
    {
        Assert.Throws<ArgumentException>(() => DefinitionChanges.Classify(Ea, TestAgents.Create("code")));
    }

    [Fact]
    public async Task The_runtime_builds_from_the_edited_definition_and_refuses_another_agents()
    {
        using var f = new RuntimeFixture();
        await using var runtime = f.Runtime(Ea);
        var edited = Ea with { Soul = "Warmer.", Model = "claude-sonnet" };

        await runtime.UpdateDefinitionAsync(edited, Ct);

        Assert.Same(edited, runtime.Definition);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.UpdateDefinitionAsync(TestAgents.Create("code"), Ct));
        Assert.Same(edited, runtime.Definition);
    }
}
