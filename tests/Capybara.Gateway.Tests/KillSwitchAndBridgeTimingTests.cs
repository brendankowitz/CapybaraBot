using System.Collections.Concurrent;
using System.Net;
using Capybara.Channels;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Gateway.Composition;
using Capybara.Gateway.Realtime;
using Capybara.Gateway.Wiring;
using Capybara.Observability.Alerts;
using Capybara.Orchestration.Registry;
using Capybara.Policy.Approvals;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Runtime.Copilot.Supervision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Gateway.Tests;

/// <summary>
/// WP10 review round 1: no CLI starts under a restored kill switch, stopping runtimes never waits on the broker,
/// activity states cannot stick, an approval broadcast never waits on the phone, and every channel send is bounded.
/// </summary>
public sealed class KillSwitchAndBridgeTimingTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_runtimes_auto_started_each_runtime_starts_once_at_boot()
    {
        using var factory = new AutoStartFactory();
        _ = factory.CreateClient();

        Assert.Equal(3, factory.Runtimes.Count);
        Assert.All(factory.Runtimes, r => Assert.Equal(1, r.Starts));
    }

    [Fact]
    public async Task A_kill_switch_restored_at_boot_never_starts_a_runtime_and_the_desktop_release_starts_them()
    {
        using var factory = new AutoStartFactory();
        new KillSwitchJournal(Path.Combine(factory.StateRoot, "policy", "kill-switch.json")).SaveEngaged("before restart", DateTimeOffset.UnixEpoch);
        var desktop = factory.CreateDeviceClient(factory.DesktopToken);

        Assert.Equal(3, factory.Runtimes.Count);
        Assert.All(factory.Runtimes, r => Assert.Equal((0, 0), (r.Starts, r.Restarts)));
        Assert.True(factory.Services.GetRequiredService<RuntimeSupervisor>().IsSuspended);

        var release = await desktop.DeleteAsync(new Uri("/api/killswitch", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);
        await Eventually.TrueAsync(() => factory.Runtimes.All(r => r.Restarts == 1), "the release starts every runtime");
        Assert.All(factory.Runtimes, r => Assert.Equal(0, r.Starts));
        Assert.All(factory.Services.GetRequiredService<RuntimeSupervisor>().Health, h => Assert.Equal((RuntimeHealthState.Healthy, 0), (h.State, h.RecentCrashes)));
    }

    [Fact]
    public async Task Engaging_again_during_a_release_restart_pass_stops_it_at_once()
    {
        using var factory = new AutoStartFactory();
        new KillSwitchJournal(Path.Combine(factory.StateRoot, "policy", "kill-switch.json")).SaveEngaged("before restart", DateTimeOffset.UnixEpoch);
        var desktop = factory.CreateDeviceClient(factory.DesktopToken);
        var supervisor = factory.Services.GetRequiredService<RuntimeSupervisor>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var runtime in factory.Runtimes)
        {
            runtime.RestartGate = gate;
        }

        // The release must return while the restart pass is still running (it is no longer inside the queue).
        var release = await desktop.DeleteAsync(new Uri("/api/killswitch", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);
        await Task.WhenAny(factory.Runtimes.Select(r => r.RestartEntered.Task)).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var stopsBefore = factory.Runtimes.ToDictionary(r => r.Agent, r => r.Stops);
        factory.Services.GetRequiredService<KillSwitch>().Engage("again");

        Assert.True(supervisor.IsSuspended);
        gate.SetResult();
        await Eventually.TrueAsync(() => factory.Runtimes.All(r => r.Stops > stopsBefore[r.Agent]), "the new engagement stops every runtime");
        await Eventually.TrueAsync(() => factory.Events.Published.Count(p => p.Name == "KillSwitchChanged" && Equals(p.Payload, true)) >= 2, "the new engagement is broadcast");
        Assert.Equal(1, factory.Runtimes.Sum(r => r.Restarts));
        Assert.True(supervisor.IsSuspended);
    }

    [Fact]
    public async Task An_unreadable_kill_switch_journal_boots_engaged_with_a_critical_alert()
    {
        using var factory = new GatewayFactory();
        var path = Path.Combine(factory.StateRoot, "policy", "kill-switch.json");
        new KillSwitchJournal(path).SaveEngaged("x", DateTimeOffset.UnixEpoch);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _ = factory.CreateClient();

            var killSwitch = factory.Services.GetRequiredService<KillSwitch>();
            Assert.True(killSwitch.IsEngaged);
            Assert.Contains("journal unreadable", killSwitch.Reason, StringComparison.Ordinal);
            Assert.Contains(factory.Services.GetRequiredService<FileAlertSink>().Recent, a => a.Kind == KillSwitchCoordinator.JournalAlertKind && a.Severity == AlertSeverity.Critical);
        }
    }

    [Fact]
    public void Only_a_journal_that_provably_does_not_exist_reads_as_released()
    {
        using var dir = new Testing.TempDirectory();
        var missingFile = new KillSwitchJournal(Path.Combine(dir.Path, "kill-switch.json"));
        var missingDirectory = new KillSwitchJournal(Path.Combine(dir.Path, "nope", "kill-switch.json"));
        var corruptPath = Path.Combine(dir.Path, "corrupt.json");
        File.WriteAllText(corruptPath, "{ not json");
        var corrupt = new KillSwitchJournal(corruptPath);

        Assert.Null(missingFile.Load());
        Assert.Null(missingDirectory.Load());
        Assert.Null(missingFile.LoadFailure);
        Assert.Contains("journal unreadable: JsonException", corrupt.Load(), StringComparison.Ordinal);
        Assert.Equal("JsonException", corrupt.LoadFailure);
    }

    [Fact]
    public async Task A_hung_broker_never_delays_stopping_the_runtimes_and_is_given_up_on_with_a_critical_alert()
    {
        using var factory = new GatewayFactory();
        factory.Broker.HangFreeze = true;
        _ = factory.CreateClient();
        var supervisor = factory.Services.GetRequiredService<RuntimeSupervisor>();

        factory.Services.GetRequiredService<KillSwitch>().Engage("test");

        await Eventually.TrueAsync(() => factory.Broker.Received("POST /broker/freeze"), "the freeze is sent");
        await Eventually.TrueAsync(() => supervisor.IsSuspended, "the runtimes are stopped while the broker hangs");
        Assert.DoesNotContain(("KillSwitchChanged", (object?)true), factory.Events.Published);

        factory.Time.Advance(KillSwitchCoordinator.BrokerTimeout);

        await Eventually.TrueAsync(() => factory.Events.Published.Contains(("KillSwitchChanged", true)), "the sequence completes");
        Assert.Contains(factory.Services.GetRequiredService<FileAlertSink>().Recent, a => a.Kind == KillSwitchCoordinator.BrokerAlertKind && a.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public async Task Turn_activity_is_applied_in_order_so_an_agent_never_stays_working()
    {
        using var factory = new GatewayFactory();
        _ = factory.CreateClient();
        var activity = factory.Services.GetRequiredService<RuntimeActivity>();
        const int turns = 300;

        for (var i = 0; i < turns; i++)
        {
            activity.OnTurnStarted(Ea, SessionKind.Main);
            activity.OnTurnEnded(Ea, SessionKind.Main, TurnResult.Blocked("done"));
        }

        await Eventually.TrueAsync(() => Snapshot(factory).Count(r => r.AgentId == "ea") == 2 * turns, "every update is broadcast");
        var updates = Snapshot(factory).Where(r => r.AgentId == "ea").ToList();
        for (var i = 0; i < updates.Count; i++)
        {
            Assert.Equal(i % 2 == 0 ? AgentActivityState.Working : AgentActivityState.Idle, updates[i].State);
        }

        Assert.Equal(AgentActivityState.Idle, factory.Services.GetRequiredService<AgentActivityTracker>().Get("ea").State);
    }

    [Fact]
    public async Task An_approval_broadcast_does_not_wait_for_a_hung_phone_push()
    {
        using var factory = new GatewayFactory();
        _ = factory.CreateClient();
        factory.Channel.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        factory.Services.GetRequiredService<ApprovalService>().Request(Ea, "session-1", "email_send", "hash", "preview", RiskLevel.Medium, MessageOrigin.User);

        await Eventually.TrueAsync(() => factory.Events.Published.Any(p => p.Name == "ApprovalRequested"), "the clients see the approval");
        Assert.DoesNotContain(factory.Channel.Sent, n => n.Kind == NotificationKind.Approval);

        factory.Channel.Hold.SetResult();
        await Eventually.TrueAsync(() => factory.Channel.Sent.Any(n => n.Kind == NotificationKind.Approval), "the push goes out once the channel answers");
    }

    [Fact]
    public async Task A_channel_that_does_not_answer_in_time_falls_back_to_the_next_one()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var hung = new RecordingChannel { Hold = new TaskCompletionSource() };
        var fallback = new RecordingChannel();
        var router = new NotificationRouter(
            [new TimeBoundedNotificationChannel(hung, TimeBoundedNotificationChannel.DefaultTimeout, time), fallback],
            new PresenceTracker(time, TimeSpan.FromSeconds(45)),
            new InMemoryAlertSink(),
            new ChannelOptions(),
            time);

        var route = router.RouteAsync(NotificationFactory.Finished(Ea), Ct).AsTask();
        Assert.False(route.IsCompleted);
        time.Advance(TimeBoundedNotificationChannel.DefaultTimeout);
        await route.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Single(fallback.Sent);
        Assert.Empty(hung.Sent);
    }

    [Fact]
    public void Configured_channels_are_registered_with_a_bounded_send()
    {
        using var factory = new ConfiguredFactory(
            ("Capybara:Channels:Ntfy:BaseUrl", "http://127.0.0.1:2586/"),
            ("Capybara:Channels:Ntfy:Topic", "capybara"));
        _ = factory.CreateClient();

        var channels = factory.Services.GetServices<INotificationChannel>().ToList();

        var ntfy = Assert.Single(channels, c => c.Name == "ntfy");
        Assert.IsType<TimeBoundedNotificationChannel>(ntfy);
    }

    [Theory]
    [InlineData("http/protobuf", true)]
    [InlineData("grpc", false)]
    public void The_telemetry_endpoint_reaches_the_cli_only_over_http(string protocol, bool passed)
    {
        using var factory = new ConfiguredFactory(
            ("Capybara:Telemetry:OtlpEndpoint", "http://127.0.0.1:4318/"),
            ("Capybara:Telemetry:OtlpProtocol", protocol));
        _ = factory.CreateClient();

        var runtime = factory.Services.GetRequiredService<CopilotRuntimeOptions>();
        var warnings = factory.Services.GetRequiredService<CompositionWarnings>().Messages;

        Assert.Equal(passed, runtime.OtlpEndpoint is not null);
        Assert.Equal(passed, warnings.Count == 0);
    }

    private static List<AgentActivitySnapshot> Snapshot(GatewayFactory factory)
    {
        lock (factory.Events.Records)
        {
            return [.. factory.Events.Records];
        }
    }

    private sealed class AutoStartFactory : GatewayFactory
    {
        private readonly ConcurrentQueue<FakeSupervisedRuntime> _runtimes = new();

        public IReadOnlyList<FakeSupervisedRuntime> Runtimes => [.. _runtimes];

        protected override IEnumerable<KeyValuePair<string, string?>> Settings() => [new("Capybara:Runtime:AutoStart", "true")];

        protected override void ConfigureServices(IServiceCollection services)
        {
            services.RemoveAll<AgentRuntimePool>();
            services.AddSingleton(sp => new AgentRuntimePool(sp.GetRequiredService<AgentRegistry>(), agent =>
            {
                var runtime = new FakeSupervisedRuntime(agent);
                _runtimes.Enqueue(runtime);
                return runtime;
            }));
        }
    }

    private sealed class ConfiguredFactory(params (string Key, string Value)[] settings) : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings() =>
            settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value));
    }
}
