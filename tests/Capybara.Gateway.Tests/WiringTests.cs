using System.Net;
using System.Net.Http.Json;
using Capybara.Channels;
using Capybara.Connections.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Storage;
using Capybara.Gateway.Composition;
using Capybara.Gateway.Wiring;
using Capybara.Memory.Abstractions;
using Capybara.Memory.Access;
using Capybara.Memory.Admin;
using Capybara.Memory.Store;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Configuration;
using Capybara.Orchestration.Lifecycle;
using Capybara.Orchestration.Proposals;
using Capybara.Orchestration.Registry;
using Capybara.Policy.Approvals;
using Capybara.Policy.Audit;
using Capybara.Policy.Gate;
using Capybara.Policy.Provenance;
using Capybara.Policy.Redaction;
using Capybara.Policy.Review;
using Capybara.Policy.Rules;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Supervision;
using Capybara.Scheduling.Execution;
using Capybara.Scheduling.Hosting;
using Capybara.Scheduling.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;

namespace Capybara.Gateway.Tests;

/// <summary>
/// WP10 composition and event wiring: the in-memory host resolves everything once, the kill switch reaches the broker,
/// runtimes, scheduler, audit and clients (FR-SAF-06), approvals reach the phone without waiting on time (NFR-04),
/// config reloads resync routines (FR-RTN-04) and agent deletion runs every cleanup (FR-AGT-07).
/// </summary>
public sealed class WiringTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_hosted_service_resolves_and_shared_services_are_registered_once()
    {
        using var factory = new GatewayFactory();
        _ = factory.CreateClient();

        var hosted = factory.Services.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, h => h is KillSwitchCoordinator);
        Assert.Contains(hosted, h => h is EventBridge);
        Assert.Contains(hosted, h => h is ConfigurationSync);
        Assert.Contains(hosted, h => h is ApprovalSweepService);
        Assert.Contains(hosted, h => h is SchedulerHostedService);

        // AutoStart=false: no runtime, pump or quota poller is started.
        Assert.DoesNotContain(hosted, h => h is AgentRuntimeHostedService or QuotaPollingService);

        Type[] once =
        [
            typeof(TimeProvider), typeof(KillSwitch), typeof(IAlertSink), typeof(FileAlertSink), typeof(CostLedger), typeof(RulesEngine),
            typeof(ApprovalService), typeof(PolicyGate), typeof(IAuditLog), typeof(TaintTracker), typeof(SecretRedactor), typeof(ISecretStore),
            typeof(AgentRegistry), typeof(CapybaraConfigurationHost), typeof(ProposalService), typeof(AgentLifecycleService),
            typeof(WikiStore), typeof(IMemoryStore), typeof(MemoryAdmin), typeof(NotificationRouter), typeof(IInboundMessageSink),
            typeof(IToolCallReviewer), typeof(IRoutineExecutor), typeof(RoutineConfigLoader), typeof(IScheduler), typeof(BrokerClient),
            typeof(Connections.Client.IBrokerToolSource), typeof(AgentRuntimePool), typeof(RuntimeSupervisor), typeof(RuntimeActivity),
        ];
        foreach (var type in once)
        {
            Assert.True(factory.Registrations.Count(d => d.ServiceType == type) == 1, $"{type.Name} is registered {factory.Registrations.Count(d => d.ServiceType == type)} times.");
        }

        // FR-AGT-07: cleanup order, ending with the very ProposalService singleton the endpoints use.
        var observers = factory.Services.GetServices<IAgentLifecycleObserver>().ToList();
        Assert.Equal(
            [typeof(RevokeBrokerGrantsObserver), typeof(UnscheduleRoutinesObserver), typeof(StopRuntimeObserver), typeof(ClearAgentMemoryObserver), typeof(RemoveLearnedRulesObserver), typeof(Chat.ClearChatHistoryObserver), typeof(ProposalService)],
            observers.Select(o => o.GetType()));
        Assert.Same(factory.Services.GetRequiredService<ProposalService>(), observers[^1]);
    }

    [Fact]
    public async Task Kill_switch_freezes_the_broker_stops_runtimes_pauses_the_scheduler_audits_and_broadcasts_then_release_reverses_it()
    {
        using var factory = new GatewayFactory();
        var phone = factory.CreateDeviceClient(factory.PhoneToken);
        var desktop = factory.CreateDeviceClient(factory.DesktopToken);
        var supervisor = factory.Services.GetRequiredService<RuntimeSupervisor>();
        var scheduler = factory.Services.GetRequiredService<IScheduler>();
        var journal = Path.Combine(factory.StateRoot, "policy", "kill-switch.json");

        var engage = await phone.PostAsJsonAsync(new Uri("/api/killswitch", UriKind.Relative), new { reason = "test" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, engage.StatusCode);
        await Eventually.TrueAsync(() => factory.Events.Published.Contains(("KillSwitchChanged", true)), "the engagement is broadcast");
        Assert.True(factory.Broker.Received("POST /broker/freeze"));
        Assert.True(supervisor.IsSuspended);
        Assert.True(File.Exists(journal));
        await Eventually.TrueAsync(async () => (await scheduler.GetPausedTriggerGroups(Ct)).Count > 0, "Quartz is paused");
        var audit = await ReadAuditAsync(factory);
        Assert.Contains(audit, e => e.Kind == "kill_switch" && e.Decision == "engaged" && e.Detail == "test");
        Assert.Contains(factory.Services.GetRequiredService<FileAlertSink>().Recent, a => a.Kind == KillSwitchCoordinator.EngagedAlertKind);

        var release = await desktop.DeleteAsync(new Uri("/api/killswitch", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);
        await Eventually.TrueAsync(() => factory.Events.Published.Contains(("KillSwitchChanged", false)), "the release is broadcast");
        Assert.True(factory.Broker.Received("POST /broker/unfreeze"));
        Assert.False(supervisor.IsSuspended);
        Assert.False(File.Exists(journal));
        await Eventually.TrueAsync(async () => (await scheduler.GetPausedTriggerGroups(Ct)).Count == 0, "Quartz is resumed");
        Assert.Contains(await ReadAuditAsync(factory), e => e.Kind == "kill_switch" && e.Decision == "released");
    }

    [Fact]
    public async Task A_broker_frozen_but_not_durably_counts_as_engaged_with_a_critical_alert_and_the_sequence_continues()
    {
        using var factory = new GatewayFactory();
        factory.Broker.FreezeStatus = HttpStatusCode.InternalServerError;
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<KillSwitch>().Engage("test");

        await Eventually.TrueAsync(() => factory.Events.Published.Contains(("KillSwitchChanged", true)), "the sequence completes");
        Assert.True(factory.Services.GetRequiredService<RuntimeSupervisor>().IsSuspended);
        Assert.Contains(factory.Services.GetRequiredService<FileAlertSink>().Recent, a => a.Kind == KillSwitchCoordinator.BrokerAlertKind && a.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public async Task A_kill_switch_engaged_before_a_restart_is_still_engaged_after_it()
    {
        using var factory = new GatewayFactory();
        var journal = new KillSwitchJournal(Path.Combine(factory.StateRoot, "policy", "kill-switch.json"));
        journal.SaveEngaged("before restart", DateTimeOffset.UnixEpoch);

        _ = factory.CreateClient();

        var killSwitch = factory.Services.GetRequiredService<KillSwitch>();
        Assert.True(killSwitch.IsEngaged);
        Assert.Equal("before restart", killSwitch.Reason);
        await Eventually.TrueAsync(() => factory.Services.GetRequiredService<RuntimeSupervisor>().IsSuspended, "the restored switch stops the runtimes");
        Assert.True(factory.Broker.Received("POST /broker/freeze"));
    }

    [Fact]
    public void A_session_tainted_before_a_restart_is_still_tainted_after_it()
    {
        using var factory = new GatewayFactory();
        new TaintTracker(Path.Combine(factory.StateRoot, "policy", "taint.jsonl")).MarkTainted("ea:main");

        _ = factory.CreateClient();

        Assert.Equal(Taint.Tainted, factory.Services.GetRequiredService<TaintTracker>().Of("ea:main"));
    }

    [Fact]
    public async Task An_approval_request_reaches_the_phone_and_the_clients_without_any_time_passing()
    {
        using var factory = new GatewayFactory();
        _ = factory.CreateClient();
        var started = factory.Time.GetUtcNow();

        var approval = factory.Services.GetRequiredService<ApprovalService>()
            .Request(Ea, "session-1", "email_send", "hash", "To: someone", RiskLevel.Medium, MessageOrigin.User);

        await Eventually.TrueAsync(() => factory.Channel.Sent.Any(n => n.Kind == NotificationKind.Approval && n.Agent == Ea), "the approval push is sent");
        await Eventually.TrueAsync(() => factory.Events.Published.Any(p => p.Name == "ApprovalRequested" && p.Payload is Client.ApprovalDto dto && dto.Id == approval.Id), "the approval is broadcast");
        Assert.Equal(started, factory.Time.GetUtcNow());
        var push = factory.Channel.Sent.Single(n => n.Kind == NotificationKind.Approval);
        Assert.Contains(push.Actions, a => a.CallbackPath == NotificationFactory.ApprovalActionPath(approval.Id, approval.Nonce, ApprovalDecision.AllowOnce));
        Assert.Equal(string.Empty, push.Body);
    }

    [Fact]
    public async Task An_unanswered_approval_expires_into_an_alert()
    {
        using var factory = new GatewayFactory();
        _ = factory.CreateClient();
        var approvals = factory.Services.GetRequiredService<ApprovalService>();
        var approval = approvals.Request(Ea, "session-1", "email_send", "hash", "preview", RiskLevel.High, MessageOrigin.Agent);

        factory.Time.Advance(new ApprovalOptions().UnattendedTtl + ApprovalSweepService.Interval);

        await Eventually.TrueAsync(
            () => factory.Services.GetRequiredService<FileAlertSink>().Recent.Any(a => a.Kind == EventBridge.ApprovalExpiredAlertKind),
            "the sweep expires the approval and alerts");
        await Eventually.TrueAsync(() => factory.Events.Published.Contains(("ApprovalResolved", approval.Id)), "clients drop the expired approval");
    }

    [Fact]
    public async Task A_config_reload_resyncs_routines()
    {
        using var factory = new GatewayFactory();
        _ = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<RoutineRegistry>();
        Assert.NotNull(registry.Find("ea-heartbeat"));

        await File.AppendAllTextAsync(
            Path.Combine(factory.ConfigRoot, "routines.yaml"),
            "\n- id: research-digest\n  owner: research\n  kind: heartbeat\n  every: 6h\n  activeHours: 08:00-20:00\n  prompt: Summarize new papers.\n  deliver: push\n  budgetCredits: 1\n",
            Ct);
        factory.Services.GetRequiredService<CapybaraConfigurationHost>().Reload();

        await Eventually.TrueAsync(() => registry.Find("research-digest") is not null, "the new routine is loaded");
        Assert.NotNull(registry.Find("ea-heartbeat"));
    }

    [Fact]
    public async Task Deleting_an_agent_runs_every_cleanup()
    {
        using var factory = new GatewayFactory();
        _ = factory.CreateClient();
        var services = factory.Services;
        var rules = services.GetRequiredService<RulesEngine>();
        rules.Add(new PolicyRule { Effect = RuleEffect.Allow, Agent = "ea", Tool = "email_draft" });
        rules.Add(new PolicyRule { Effect = RuleEffect.Allow, Agent = "*", Tool = "memory_read" });
        await services.GetRequiredService<MemoryAdmin>().EditPageAsync(MemoryAdminPrincipal.Human, "/agents/ea/notes.md", "private notes", "notes", Ct);
        var registry = services.GetRequiredService<RoutineRegistry>();
        Assert.Contains(registry.Routines, r => r.Owner == Ea);

        var result = await services.GetRequiredService<AgentLifecycleService>().DeleteAsync(Ea, ConfigPrincipal.Human("owner"), Ct);

        Assert.Empty(result.Errors);
        Assert.True(factory.Broker.Received("POST /broker/agents/ea/revoke"));
        Assert.DoesNotContain(registry.Routines, r => r.Owner == Ea);
        Assert.Null(services.GetRequiredService<AgentRuntimePool>().Find(Ea));
        Assert.True(services.GetRequiredService<KillSwitch>().IsBlocked(Ea));
        var wiki = services.GetRequiredService<WikiStore>();
        Assert.Empty(wiki.List(new MemoryAccessContext { Agent = Ea, SessionId = "test" }, "/agents/ea/"));
        Assert.DoesNotContain(rules.LearnedRules, r => r.Agent == "ea");
        Assert.Contains(rules.LearnedRules, r => r.Agent == "*");
        Assert.DoesNotContain("\"ea\"", await File.ReadAllTextAsync(Path.Combine(factory.StateRoot, "policy", "learned-rules.json"), Ct), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(factory.StateRoot, "agents", "ea")));
    }

    [Fact]
    public void Agent_mode_hosts_only_that_agents_runtime_and_supervisor()
    {
        using var dirs = new GatewayFactory();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing", DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capybara:ConfigRoot"] = dirs.ConfigRoot,
            ["Capybara:StateRoot"] = dirs.StateRoot,
        });
        builder.Services.AddLogging();
        builder.Services.AddCapybaraAgentHost(builder.Configuration, AgentId.Parse("research"));
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

        using var host = builder.Build();

        var hosted = host.Services.GetServices<IHostedService>().Select(h => h.GetType()).ToList();
        Assert.Equal([typeof(AgentRuntimeHostedService), typeof(RuntimeSupervisorService)], hosted);
        Assert.Equal(["research"], host.Services.GetRequiredService<AgentRuntimePool>().All.Select(r => r.Agent.Value));
        Assert.IsType<FileAlertSink>(host.Services.GetRequiredService<IAlertSink>());
    }

    [Fact]
    public void A_hosting_state_root_that_differs_from_the_runtime_state_root_fails_closed()
    {
        using var factory = new MismatchedStateRootFactory();

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("must equal Capybara:StateRoot", ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("restore")]
    [InlineData("agent|Not An Id")]
    [InlineData("agent")]
    [InlineData("bakup")]
    public void Command_line_modes_exit_without_starting_the_gateway(string commandLine)
    {
        // backup without configured sources and restore without arguments are usage errors (2), as is a bad agent id.
        var exit = typeof(Program).Assembly.EntryPoint!.Invoke(null, [commandLine.Split('|')]);

        Assert.Equal(2, exit);
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

    private sealed class MismatchedStateRootFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings() =>
            [new("Capybara:Hosting:StateRoot", Path.Combine(StateRoot, "elsewhere"))];
    }
}
