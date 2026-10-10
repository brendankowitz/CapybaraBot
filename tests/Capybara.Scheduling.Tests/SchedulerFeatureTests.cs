using System.Security.Cryptography;
using System.Text;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Routines;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Policy.Safety;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.Durable;
using Capybara.Scheduling.Execution;
using Capybara.Scheduling.History;
using Capybara.Scheduling.Jitter;
using Capybara.Scheduling.Lifecycle;
using Capybara.Scheduling.Triggers;
using Microsoft.Extensions.Time.Testing;
using Quartz;

namespace Capybara.Scheduling.Tests;

public sealed class SchedulerFeatureTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static RoutineSpec Routine(TriggerKind kind = TriggerKind.Heartbeat) => new()
    {
        Id = "routine-a",
        Owner = Ea,
        Kind = kind,
        Interval = TimeSpan.FromMinutes(10),
        Prompt = "check",
        EventFilter = kind == TriggerKind.Event ? "github:pull_request.opened repo=owner/name" : null,
        InputSource = "inbox",
    };

    [Fact]
    public async Task Dispatcher_enforces_ordering_and_dry_run_passthrough()
    {
        var harness = new DispatcherHarness(new CapturingExecutor(new RoutineExecutionResult(RoutineExecutionStatus.Succeeded, "ok")));
        harness.Registry.AddOrUpdate(Routine(), RoutinePrincipal.Human("user"));
        harness.KillSwitch.Engage("stop");

        var killed = await harness.Dispatcher.DispatchAsync(new RoutineFiring(Routine(), DateTimeOffset.UnixEpoch, "run"), TestContext.Current.CancellationToken);
        Assert.Equal(RoutineExecutionStatus.Skipped, killed.Status);
        Assert.Single(harness.Alerts.Recent, a => a.Kind == "routine.skipped");

        harness = new DispatcherHarness(new CapturingExecutor(new RoutineExecutionResult(RoutineExecutionStatus.Succeeded, "ok")));
        harness.Registry.AddOrUpdate(Routine(), RoutinePrincipal.Human("user"));
        var dry = await harness.Dispatcher.DispatchAsync(new RoutineFiring(Routine(), DateTimeOffset.UnixEpoch, "run", DryRun: true), TestContext.Current.CancellationToken);
        Assert.Equal(RoutineExecutionStatus.Succeeded, dry.Status);
        Assert.True(((CapturingExecutor)harness.Executor).LastInvocation?.DryRun);
    }

    [Fact]
    public async Task Dispatcher_handles_concurrency_budget_missing_input_and_silent_heartbeat()
    {
        var blocking = new BlockingExecutor();
        var harness = new DispatcherHarness(blocking, new Capybara.Scheduling.Dispatch.SchedulingOptions { ConcurrencyCap = 1 });
        harness.Registry.AddOrUpdate(Routine(), RoutinePrincipal.Human("user"));
        var first = harness.Dispatcher.DispatchAsync(new RoutineFiring(Routine(), DateTimeOffset.UnixEpoch, "one"), TestContext.Current.CancellationToken);
        await blocking.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var saturated = await harness.Dispatcher.DispatchAsync(new RoutineFiring(Routine(), DateTimeOffset.UnixEpoch, "two"), TestContext.Current.CancellationToken);
        Assert.Equal(RoutineExecutionStatus.Skipped, saturated.Status);
        blocking.Release.SetResult();
        await first;

        harness = new DispatcherHarness(new CapturingExecutor(new RoutineExecutionResult(RoutineExecutionStatus.Succeeded)), inputSourceExists: false);
        harness.Registry.AddOrUpdate(Routine(), RoutinePrincipal.Human("user"));
        var missing = await harness.Dispatcher.DispatchAsync(new RoutineFiring(Routine(), DateTimeOffset.UnixEpoch, "missing"), TestContext.Current.CancellationToken);
        Assert.Equal(RoutineExecutionStatus.Skipped, missing.Status);

        harness = new DispatcherHarness(new CapturingExecutor(new RoutineExecutionResult(RoutineExecutionStatus.Succeeded)));
        harness.Registry.AddOrUpdate(Routine() with { InputSource = null, ActiveFrom = new TimeOnly(9, 0), ActiveTo = new TimeOnly(10, 0) }, RoutinePrincipal.Human("user"));
        var silent = await harness.Dispatcher.DispatchAsync(new RoutineFiring(Routine() with { InputSource = null, ActiveFrom = new TimeOnly(9, 0), ActiveTo = new TimeOnly(10, 0) }, new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero), "silent"), TestContext.Current.CancellationToken);
        Assert.Equal(RoutineExecutionStatus.NothingToReport, silent.Status);
        Assert.Empty(harness.Alerts.Recent);

        harness = new DispatcherHarness(new CapturingExecutor(new RoutineExecutionResult(RoutineExecutionStatus.Succeeded)));
        harness.Ledger.SetBudget(Ea, new AgentBudget { DailyCredits = 0, MonthlyCredits = 0 });
        harness.Registry.AddOrUpdate(Routine() with { InputSource = null }, RoutinePrincipal.Human("user"));
        var overBudget = await harness.Dispatcher.DispatchAsync(new RoutineFiring(Routine() with { InputSource = null }, DateTimeOffset.UnixEpoch, "budget"), TestContext.Current.CancellationToken);
        Assert.Equal(RoutineExecutionStatus.OverBudget, overBudget.Status);
    }

    [Fact]
    public void Jitter_is_bounded_and_deterministic()
    {
        var interval = TimeSpan.FromMinutes(10);
        var fire = DateTimeOffset.UnixEpoch.AddHours(1);
        var first = RoutineJitter.Apply("abc", fire, interval);
        var second = RoutineJitter.Apply("abc", fire, interval);
        Assert.Equal(first, second);
        Assert.InRange(first.TotalSeconds, interval.TotalSeconds * 0.9, interval.TotalSeconds * 1.1);
    }

    [Fact]
    public void Webhook_hmac_filter_and_taint_behaviour_is_correct()
    {
        var body = Encoding.UTF8.GetBytes("{\"repo\":\"owner/name\",\"action\":\"opened\"}");
        var secret = Encoding.UTF8.GetBytes("secret");
        using var hmac = new HMACSHA256(secret);
        var signature = "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();

        Assert.True(WebhookEventSource.VerifyGitHubSignature(body, signature, secret));
        Assert.False(WebhookEventSource.VerifyGitHubSignature(Encoding.UTF8.GetBytes("{}"), signature, secret));

        var filter = EventFilter.Parse("github:pull_request.opened repo=owner/name action=opened");
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.True(filter.Matches("github", "pull_request.opened", doc.RootElement));
        Assert.Throws<FormatException>(() => EventFilter.Parse("github:pull_request.opened"));
        Assert.Equal(Taint.Tainted, WebhookEventSource.CreatePayload("github", "pull_request.opened", body).Taint);
    }

    [Fact]
    public async Task Auto_pauser_and_registry_require_human_cost_acknowledgement()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var alerts = new InMemoryAlertSink();
        var registry = new RoutineRegistry();
        var routine = Routine() with { InputSource = null };
        registry.AddOrUpdate(routine, RoutinePrincipal.Human("user"));
        Assert.Throws<InvalidOperationException>(() => registry.Enable(routine.Id, 1, RoutinePrincipal.Agent("ea")));
        Assert.Throws<InvalidOperationException>(() => registry.Enable(routine.Id, 1, RoutinePrincipal.Human("user")));
        registry.Enable(routine.Id, Capybara.Scheduling.Planning.CostProjection.MonthlyCredits(routine), RoutinePrincipal.Human("user"));

        var auto = new RoutineAutoPauser(registry, alerts, time, autoPauseDays: 14);
        auto.RecordUserInteraction(routine.Id);
        time.Advance(TimeSpan.FromDays(15));
        await auto.CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(registry.GetState(routine.Id).AutoPaused);
        Assert.Single(alerts.Recent, a => a.Kind == "routine.autopaused");
    }

    [Fact]
    public async Task Quartz_sqlite_store_starts_and_fires_job()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "test-artifacts");
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, $"quartz-{Guid.NewGuid():n}.sqlite");
        var executor = new SignalingExecutor();
        var harness = new DispatcherHarness(executor, new Capybara.Scheduling.Dispatch.SchedulingOptions { SQLitePath = db });
        var factory = new QuartzSchedulerFactory(new Capybara.Scheduling.Dispatch.SchedulingOptions { SQLitePath = db });
        var scheduler = await factory.CreateSchedulerAsync(TestContext.Current.CancellationToken);
        try
        {
            scheduler.Context.Add(nameof(RoutineDispatcher), harness.Dispatcher);
            harness.Registry.AddOrUpdate(Routine() with { InputSource = null }, RoutinePrincipal.Human("user"));
            var job = Quartz.JobBuilder.Create<RoutineJob>()
                .WithIdentity(new JobKey("routine-a", "ea"))
                .UsingJobData(RoutineJob.RoutineJsonKey, System.Text.Json.JsonSerializer.Serialize(Routine() with { InputSource = null }))
                .StoreDurably()
                .Build();
            await scheduler.AddJob(job, new AddJobOptions { Replace = true }, TestContext.Current.CancellationToken);
            await scheduler.Start(TestContext.Current.CancellationToken);
            await scheduler.TriggerJob(new JobKey("routine-a", "ea"), new JobDataMap(), TestContext.Current.CancellationToken);
            await executor.Fired.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await scheduler.Shutdown(true, CancellationToken.None);
            if (File.Exists(db))
            {
                File.Delete(db);
            }
        }
    }

    private sealed class DispatcherHarness
    {
        public DispatcherHarness(IRoutineExecutor executor, Capybara.Scheduling.Dispatch.SchedulingOptions? options = null, bool inputSourceExists = true)
        {
            Executor = executor;
            Alerts = new InMemoryAlertSink();
            Ledger = new CostLedger(Alerts, TimeProvider.System);
            Registry = new RoutineRegistry();
            KillSwitch = new KillSwitch(TimeProvider.System);
            History = new RoutineRunHistory(Alerts);
            Dispatcher = new RoutineDispatcher(executor, History, Alerts, Ledger, Registry, KillSwitch, TimeProvider.System, options, _ => inputSourceExists);
        }

        public IRoutineExecutor Executor { get; }

        public InMemoryAlertSink Alerts { get; }

        public CostLedger Ledger { get; }

        public RoutineRegistry Registry { get; }

        public KillSwitch KillSwitch { get; }

        public RoutineRunHistory History { get; }

        public RoutineDispatcher Dispatcher { get; }
    }

    private sealed class CapturingExecutor(RoutineExecutionResult result) : IRoutineExecutor
    {
        public RoutineInvocation? LastInvocation { get; private set; }

        public Task<RoutineExecutionResult> ExecuteAsync(RoutineInvocation invocation, CancellationToken cancellationToken)
        {
            LastInvocation = invocation;
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingExecutor : IRoutineExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RoutineExecutionResult> ExecuteAsync(RoutineInvocation invocation, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new RoutineExecutionResult(RoutineExecutionStatus.Succeeded);
        }
    }

    private sealed class SignalingExecutor : IRoutineExecutor
    {
        public TaskCompletionSource Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RoutineExecutionResult> ExecuteAsync(RoutineInvocation invocation, CancellationToken cancellationToken)
        {
            Fired.TrySetResult();
            return Task.FromResult(new RoutineExecutionResult(RoutineExecutionStatus.Succeeded));
        }
    }
}
