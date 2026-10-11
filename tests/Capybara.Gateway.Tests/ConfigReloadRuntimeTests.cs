using System.Collections.Concurrent;
using System.Net.Http.Json;
using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Proposals;
using Capybara.Orchestration.Configuration;
using Capybara.Orchestration.Proposals;
using Capybara.Orchestration.Registry;
using Capybara.Runtime.Copilot.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Capybara.Gateway.Tests;

/// <summary>
/// FR-AGT-06: an approved or hand-made config edit reaches the agent's running runtime: its sessions are rebuilt from the
/// new definition, the CLI restarts only for process-level fields, and the tool self-test runs again (FR-AGT-09).
/// </summary>
public sealed class ConfigReloadRuntimeTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_session_level_edit_swaps_the_definition_and_re_runs_the_self_test_without_a_restart()
    {
        using var factory = new FakeRuntimeFactory();
        _ = factory.CreateClient();
        var ea = factory.Runtime(Ea);
        var research = factory.Runtime(AgentId.Parse("research"));
        await Eventually.TrueAsync(() => ea.Starts == 1 && ea.SelfTests == 1, "the runtime started and passed its start-up self-test");

        await File.WriteAllTextAsync(Path.Combine(factory.ConfigRoot, "agents", "ea", "SOUL.md"), "You are warmer now.\n", Ct);
        factory.Services.GetRequiredService<CapybaraConfigurationHost>().Reload();

        await Eventually.TrueAsync(() => ea.Definition.Soul == "You are warmer now.\n" && ea.SelfTests == 2, "the edit reaches the runtime and is self-tested");
        Assert.Equal(0, ea.Restarts);
        Assert.Equal(0, research.DefinitionUpdates);
        Assert.Equal(1, research.SelfTests);
    }

    [Fact]
    public async Task An_egress_edit_restarts_the_cli_then_self_tests_it()
    {
        using var factory = new FakeRuntimeFactory();
        _ = factory.CreateClient();
        var ea = factory.Runtime(Ea);
        await Eventually.TrueAsync(() => ea.Starts == 1 && ea.SelfTests == 1, "the runtime started and passed its start-up self-test");
        var yaml = Path.Combine(factory.ConfigRoot, "agents", "ea", "agent.yaml");

        await File.WriteAllTextAsync(yaml, (await File.ReadAllTextAsync(yaml, Ct)).Replace("egress: []", "egress: [api.example.com]", StringComparison.Ordinal), Ct);
        factory.Services.GetRequiredService<CapybaraConfigurationHost>().Reload();

        await Eventually.TrueAsync(() => ea.Restarts == 1 && ea.SelfTests == 2, "the CLI restarts and is self-tested");
        Assert.Equal(["api.example.com"], ea.Definition.EgressDomains);
    }

    [Fact]
    public async Task The_decision_response_says_when_an_applied_proposal_takes_effect()
    {
        using var factory = new ApiFactory();
        var proposals = factory.Services.GetRequiredService<ProposalService>();
        var soul = await proposals.ProposeAsync(Ea, ProposalKind.Soul, null, "You are Ea, warmer.\n", "Tone.", Ct);
        var rejected = await proposals.ProposeAsync(Ea, ProposalKind.OperatingRules, null, "Send freely.\n", "Faster.", Ct);
        var desktop = factory.CreateDeviceClient(factory.DesktopToken);

        var applied = await (await desktop.PostAsJsonAsync(new Uri($"/api/proposals/{soul.Id}", UriKind.Relative), new ProposalDecisionDto(true), Ct)).Content.ReadFromJsonAsync<ProposalDto>(Ct);
        var refused = await (await desktop.PostAsJsonAsync(new Uri($"/api/proposals/{rejected.Id}", UriKind.Relative), new ProposalDecisionDto(false), Ct)).Content.ReadFromJsonAsync<ProposalDto>(Ct);

        Assert.Equal(nameof(ProposalStatus.Applied), applied!.Status);
        Assert.Contains("next turn", applied.Effect, StringComparison.Ordinal);
        Assert.Null(refused!.Effect);
    }

    private sealed class FakeRuntimeFactory : GatewayFactory
    {
        private readonly ConcurrentDictionary<AgentId, FakeSupervisedRuntime> _runtimes = new();

        public FakeSupervisedRuntime Runtime(AgentId agent) => _runtimes[agent];

        protected override IEnumerable<KeyValuePair<string, string?>> Settings() => [new("Capybara:Runtime:AutoStart", "true")];

        protected override void ConfigureServices(IServiceCollection services)
        {
            services.RemoveAll<AgentRuntimePool>();
            services.AddSingleton(sp => new AgentRuntimePool(sp.GetRequiredService<AgentRegistry>(), agent => _runtimes.GetOrAdd(agent.Id, _ => new FakeSupervisedRuntime(agent))));
        }
    }
}
