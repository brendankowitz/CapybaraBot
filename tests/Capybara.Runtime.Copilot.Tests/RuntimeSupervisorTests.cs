using Capybara.Contracts.Agents;
using Capybara.Observability.Alerts;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Supervision;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Runtime.Copilot.Tests;

public sealed class RuntimeSupervisorTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly InMemoryAlertSink _alerts = new();
    private readonly SupervisorOptions _options = new() { MaxCrashes = 3, InitialBackoff = TimeSpan.FromSeconds(5), MaxBackoff = TimeSpan.FromSeconds(60) };

    private RuntimeSupervisor Create(FakeRuntime runtime) =>
        new([runtime], _alerts, _options, _time, NullLogger<RuntimeSupervisor>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Healthy_runtime_is_left_alone()
    {
        var runtime = new FakeRuntime { Healthy = true };
        await Create(runtime).CheckOnceAsync(Ct);
        Assert.Equal(0, runtime.Restarts);
    }

    [Fact]
    public async Task Unhealthy_runtime_is_restarted()
    {
        var runtime = new FakeRuntime { Healthy = false };
        await Create(runtime).CheckOnceAsync(Ct);
        Assert.Equal(1, runtime.Restarts);
    }

    [Fact]
    public async Task Failed_restarts_back_off_exponentially()
    {
        var runtime = new FakeRuntime { Healthy = false, RestartThrows = true };
        var supervisor = Create(runtime);

        await supervisor.CheckOnceAsync(Ct);
        await supervisor.CheckOnceAsync(Ct);
        Assert.Equal(1, runtime.Restarts);
        Assert.Equal(RuntimeHealthState.BackingOff, Assert.Single(supervisor.Health).State);

        _time.Advance(TimeSpan.FromSeconds(5));
        await supervisor.CheckOnceAsync(Ct);
        Assert.Equal(2, runtime.Restarts);

        Assert.Equal(TimeSpan.FromSeconds(10), supervisor.Backoff(2));
        Assert.Equal(TimeSpan.FromSeconds(60), supervisor.Backoff(10));
    }

    [Fact]
    public async Task Crash_loop_is_quarantined_with_an_alert()
    {
        var runtime = new FakeRuntime { Healthy = false };
        var supervisor = Create(runtime);

        for (var i = 0; i < 5; i++)
        {
            await supervisor.CheckOnceAsync(Ct);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(2, runtime.Restarts);
        Assert.Equal(RuntimeHealthState.Quarantined, Assert.Single(supervisor.Health).State);
        Assert.Contains(_alerts.Recent, a => a.Kind == "runtime.quarantined");

        supervisor.Unquarantine("ea");
        await supervisor.CheckOnceAsync(Ct);
        Assert.Equal(3, runtime.Restarts);
    }

    [Fact]
    public async Task Suspended_runtimes_are_stopped_and_not_restarted_until_resumed_without_counting_a_crash()
    {
        var runtime = new FakeRuntime { Healthy = true };
        var supervisor = Create(runtime);

        await supervisor.SuspendAsync(Ct);
        Assert.True(supervisor.IsSuspended);
        Assert.Equal(1, runtime.Stops);

        await supervisor.CheckOnceAsync(Ct);
        Assert.Equal(0, runtime.Restarts);

        supervisor.Resume();
        await supervisor.CheckOnceAsync(Ct);

        Assert.Equal(1, runtime.Restarts);
        var health = Assert.Single(supervisor.Health);
        Assert.Equal(RuntimeHealthState.Healthy, health.State);
        Assert.Equal(0, health.RecentCrashes);
    }

    [Fact]
    public async Task A_runtime_that_fails_to_stop_does_not_keep_the_others_running()
    {
        var failing = new FakeRuntime { Healthy = true, StopThrows = true };
        var other = new FakeRuntime(AgentId.Parse("code")) { Healthy = true };
        var supervisor = new RuntimeSupervisor([failing, other], _alerts, _options, _time, NullLogger<RuntimeSupervisor>.Instance);

        await supervisor.SuspendAsync(Ct);

        Assert.Equal(1, other.Stops);
    }

    [Fact]
    public async Task Suspension_takes_effect_at_once_and_a_check_in_progress_restarts_nothing_more()
    {
        var pinging = new BlockingRuntime(AgentId.Parse("ea"));
        var next = new FakeRuntime(AgentId.Parse("code")) { Healthy = false };
        var supervisor = new RuntimeSupervisor([pinging, next], _alerts, _options, _time, NullLogger<RuntimeSupervisor>.Instance);

        var check = supervisor.CheckOnceAsync(Ct);
        await pinging.PingStarted.Task.WaitAsync(Ct);
        var suspend = supervisor.SuspendAsync(Ct);

        // Checks are off before the check in progress finishes; the stop itself waits for it.
        Assert.True(supervisor.IsSuspended);
        Assert.False(suspend.IsCompleted);

        pinging.Ping.SetResult(false);
        await check;
        await suspend;

        Assert.Equal(0, pinging.Restarts);
        Assert.Equal(0, next.Restarts);
        Assert.Equal(1, pinging.Stops);
        Assert.Equal(1, next.Stops);
    }

    [Fact]
    public async Task A_runtime_unquarantined_while_suspended_is_restarted_after_resume_without_counting_a_crash()
    {
        var runtime = new FakeRuntime { Healthy = false };
        var supervisor = Create(runtime);
        for (var i = 0; i < 3; i++)
        {
            await supervisor.CheckOnceAsync(Ct);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(RuntimeHealthState.Quarantined, Assert.Single(supervisor.Health).State);
        await supervisor.SuspendAsync(Ct);
        supervisor.Unquarantine("ea");
        supervisor.Resume();
        var restartsBefore = runtime.Restarts;

        await supervisor.CheckOnceAsync(Ct);

        Assert.Equal(restartsBefore + 1, runtime.Restarts);
        Assert.Equal(0, Assert.Single(supervisor.Health).RecentCrashes);
    }

    private sealed class BlockingRuntime(AgentId agent) : ISupervisedRuntime
    {
        public AgentId Agent { get; } = agent;

        public TaskCompletionSource PingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Ping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Restarts { get; private set; }

        public int Stops { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            return Task.CompletedTask;
        }

        public Task<bool> PingAsync(CancellationToken cancellationToken)
        {
            PingStarted.TrySetResult();
            return Ping.Task;
        }

        public Task<SelfTestResult> SelfTestAsync(CancellationToken cancellationToken) => Task.FromResult(new SelfTestResult(true, [], []));

        public Task RestartAsync(CancellationToken cancellationToken)
        {
            Restarts++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRuntime(AgentId? agent = null) : ISupervisedRuntime
    {
        public AgentId Agent { get; } = agent ?? AgentId.Parse("ea");

        public bool Healthy { get; set; }

        public bool RestartThrows { get; set; }

        public bool StopThrows { get; set; }

        public int Restarts { get; private set; }

        public int Stops { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            Healthy = false;
            return StopThrows ? throw new InvalidOperationException("stuck") : Task.CompletedTask;
        }

        public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(Healthy);

        public Task<SelfTestResult> SelfTestAsync(CancellationToken cancellationToken) => Task.FromResult(new SelfTestResult(true, [], []));

        public Task RestartAsync(CancellationToken cancellationToken)
        {
            Restarts++;
            if (RestartThrows)
            {
                throw new InvalidOperationException("still broken");
            }

            Healthy = Healthy || Stops > 0;
            return Task.CompletedTask;
        }
    }
}
