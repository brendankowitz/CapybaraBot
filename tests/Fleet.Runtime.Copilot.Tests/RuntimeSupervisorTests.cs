using Fleet.Contracts.Agents;
using Fleet.Observability.Alerts;
using Fleet.Runtime.Copilot.Supervision;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Fleet.Runtime.Copilot.Tests;

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

    private sealed class FakeRuntime : ISupervisedRuntime
    {
        public AgentId Agent { get; } = AgentId.Parse("ea");

        public bool Healthy { get; set; }

        public bool RestartThrows { get; set; }

        public int Restarts { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(Healthy);

        public Task RestartAsync(CancellationToken cancellationToken)
        {
            Restarts++;
            return RestartThrows ? throw new InvalidOperationException("still broken") : Task.CompletedTask;
        }
    }
}
