using System.Net;
using Capybara.Contracts.Agents;
using Capybara.Gateway.Wiring;
using Capybara.Orchestration.Configuration;
using Capybara.Policy.Audit;
using Capybara.Policy.Safety;
using Capybara.Scheduling.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace Capybara.Gateway.Tests;

/// <summary>
/// FR-SAF-06, FR-RTN-04: what the user pauses or disables from a client stays that way across a restart, and every such
/// action is audited (FR-SAF-09).
/// </summary>
public sealed class HumanControlPersistenceTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PausedJournal(GatewayFactory factory) => Path.Combine(factory.StateRoot, "policy", "paused-agents.json");

    private static string SchedulingDb(GatewayFactory factory) => Path.Combine(factory.StateRoot, "scheduling", "scheduling.sqlite");

    [Fact]
    public async Task Pausing_and_resuming_an_agent_is_journaled_and_audited()
    {
        using var factory = new GatewayFactory();
        var phone = factory.CreateDeviceClient(factory.PhoneToken);
        var killSwitch = factory.Services.GetRequiredService<KillSwitch>();

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync(new Uri("/api/agents/ea/pause", UriKind.Relative), null, Ct)).StatusCode);
        Assert.True(killSwitch.IsBlocked(Ea));
        Assert.Equal([Ea], new PausedAgentsJournal(PausedJournal(factory)).Load([]));

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync(new Uri("/api/agents/ea/resume", UriKind.Relative), null, Ct)).StatusCode);
        Assert.False(killSwitch.IsBlocked(Ea));
        Assert.Empty(new PausedAgentsJournal(PausedJournal(factory)).Load([]));

        var audit = await ReadAuditAsync(factory);
        Assert.Contains(audit, e => e is { Kind: "agent_control", Decision: "paused", Agent: "ea" });
        Assert.Contains(audit, e => e is { Kind: "agent_control", Decision: "resumed", Agent: "ea" });
    }

    [Fact]
    public void An_agent_paused_before_a_restart_is_still_paused_after_it()
    {
        using var factory = new GatewayFactory();
        new PausedAgentsJournal(PausedJournal(factory)).Add(Ea);

        _ = factory.CreateClient();

        Assert.True(factory.Services.GetRequiredService<KillSwitch>().IsBlocked(Ea));
        Assert.False(factory.Services.GetRequiredService<KillSwitch>().IsBlocked(AgentId.Parse("research")));
    }

    [Fact]
    public async Task An_unreadable_paused_agents_journal_pauses_every_agent_with_a_critical_alert()
    {
        using var factory = new GatewayFactory();
        Directory.CreateDirectory(Path.GetDirectoryName(PausedJournal(factory))!);
        await File.WriteAllTextAsync(PausedJournal(factory), "{ not json", Ct);

        _ = factory.CreateClient();

        var killSwitch = factory.Services.GetRequiredService<KillSwitch>();
        Assert.All(factory.Services.GetRequiredService<Orchestration.Registry.AgentRegistry>().All, a => Assert.True(killSwitch.IsBlocked(a.Id)));
        await Eventually.TrueAsync(
            () => factory.Services.GetRequiredService<Observability.Alerts.FileAlertSink>().Recent.Any(a => a.Kind == KillSwitchCoordinator.JournalAlertKind && a.Message.Contains("paused-agents", StringComparison.Ordinal)),
            "the unreadable journal is alerted");
    }

    [Fact]
    public async Task Disabling_a_routine_from_a_client_is_persisted_and_audited()
    {
        using var factory = new GatewayFactory();
        var phone = factory.CreateDeviceClient(factory.PhoneToken);

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync(new Uri("/api/routines/ea-heartbeat/disable", UriKind.Relative), null, Ct)).StatusCode);

        var heartbeat = CapybaraConfigurationLoader.Load(factory.ConfigRoot).Routines.Single(r => r.Id == "ea-heartbeat");
        var restarted = new RoutineRegistry(sqlitePath: SchedulingDb(factory));
        restarted.AddOrUpdate(heartbeat, RoutinePrincipal.Human("test"));
        Assert.False(restarted.GetState("ea-heartbeat").Enabled);
        Assert.Contains(await ReadAuditAsync(factory), e => e is { Kind: "routine_control", Decision: "disabled", Agent: "ea" } && e.Detail!.Contains("ea-heartbeat", StringComparison.Ordinal));
    }

    [Fact]
    public void A_routine_disabled_before_a_restart_is_still_disabled_after_it()
    {
        using var factory = new GatewayFactory();
        var heartbeat = CapybaraConfigurationLoader.Load(factory.ConfigRoot).Routines.Single(r => r.Id == "ea-heartbeat");
        var before = new RoutineRegistry(sqlitePath: SchedulingDb(factory));
        before.AddOrUpdate(heartbeat, RoutinePrincipal.Human("test"));
        before.Disable("ea-heartbeat", RoutinePrincipal.Human("test"));

        _ = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<RoutineRegistry>();
        Assert.NotNull(registry.Find("ea-heartbeat"));
        Assert.False(registry.GetState("ea-heartbeat").Enabled);
    }

    private static async Task<List<AuditEntry>> ReadAuditAsync(GatewayFactory factory)
    {
        var entries = new List<AuditEntry>();
        await foreach (var entry in factory.Services.GetRequiredService<IAuditLog>().ReadAllAsync(Ct))
        {
            entries.Add(entry);
        }

        return entries;
    }
}
