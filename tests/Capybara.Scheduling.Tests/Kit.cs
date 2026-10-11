using Capybara.Contracts.Agents;
using Capybara.Contracts.Routines;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Policy.Safety;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.Execution;
using Capybara.Scheduling.History;
using Capybara.Scheduling.Hosting;
using Capybara.Scheduling.Lifecycle;
using Capybara.Testing;
using Quartz;
using SchedulingOptions = Capybara.Scheduling.Dispatch.SchedulingOptions;
namespace Capybara.Scheduling.Tests;

internal static class Kit
{
    public static readonly AgentId Ea = AgentId.Parse("ea");

    public static readonly RoutinePrincipal User = RoutinePrincipal.Human("user");

    public static RoutineSpec Heartbeat(string id = "hb", TimeSpan? interval = null) => new()
    {
        Id = id,
        Owner = Ea,
        Kind = TriggerKind.Heartbeat,
        Interval = interval ?? TimeSpan.FromMinutes(10),
        Prompt = "check",
    };

    public static RoutineSpec Cron(string expression, string id = "cron") => new()
    {
        Id = id,
        Owner = Ea,
        Kind = TriggerKind.Cron,
        Cron = expression,
        Prompt = "report",
    };

    public static RoutineSpec Event(string filter, string id = "evt") => new()
    {
        Id = id,
        Owner = Ea,
        Kind = TriggerKind.Event,
        EventFilter = filter,
        Prompt = "triage",
    };

    public static async Task EventuallyAsync(Func<ValueTask<bool>> condition, string because, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for: {because}");
    }
}

/// <summary>Executor double that records invocations and returns a scripted result. With <c>blocking</c> it parks until <see cref="Release"/> is completed, ignoring cancellation (a hung runtime).</summary>
internal sealed class ScriptedExecutor(Func<RoutineInvocation, RoutineExecutionResult>? script = null, bool blocking = false) : IRoutineExecutor
{
    private readonly List<RoutineInvocation> _invocations = [];
    private readonly Lock _gate = new();

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<RoutineInvocation> Invocations
    {
        get
        {
            lock (_gate)
            {
                return [.. _invocations];
            }
        }
    }

    public async Task<RoutineExecutionResult> ExecuteAsync(RoutineInvocation invocation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _invocations.Add(invocation);
        }

        Started.TrySetResult();
        if (blocking)
        {
            await Release.Task;
        }

        return script?.Invoke(invocation) ?? new RoutineExecutionResult(RoutineExecutionStatus.Succeeded, "ok");
    }
}

/// <summary>The dispatcher and everything it depends on, in memory.</summary>
internal sealed class Harness : IDisposable
{
    public Harness(SchedulingOptions? options = null, ScriptedExecutor? executor = null, TimeProvider? time = null, string? historyPath = null)
    {
        Options = options ?? new SchedulingOptions { TimeZone = TimeZoneInfo.Utc };
        Time = time ?? TimeProvider.System;
        Executor = executor ?? new ScriptedExecutor();
        Alerts = new InMemoryAlertSink();
        Ledger = new CostLedger(Alerts, Time);
        Registry = new RoutineRegistry(Options.TimeZone);
        KillSwitch = new KillSwitch(Time);
        History = new RoutineRunHistory(Alerts, historyPath);
        Dispatcher = new RoutineDispatcher(Executor, History, Alerts, Ledger, Registry, KillSwitch, Time, Options);
    }

    public SchedulingOptions Options { get; }

    public TimeProvider Time { get; }

    public ScriptedExecutor Executor { get; }

    public InMemoryAlertSink Alerts { get; }

    public CostLedger Ledger { get; }

    public RoutineRegistry Registry { get; }

    public KillSwitch KillSwitch { get; }

    public RoutineRunHistory History { get; }

    public RoutineDispatcher Dispatcher { get; }

    public void Dispose()
    {
        Dispatcher.Dispose();
        KillSwitch.Dispose();
    }
}

/// <summary>A <see cref="Harness"/> wired to a real durable Quartz scheduler on a temp SQLite file.</summary>
internal sealed class QuartzHarness : IAsyncDisposable
{
    private readonly TempDirectory? _directory;

    private QuartzHarness(TempDirectory? directory, Harness inner, IScheduler scheduler, ScheduleSynchronizer synchronizer, string databasePath)
    {
        _directory = directory;
        Inner = inner;
        Scheduler = scheduler;
        Synchronizer = synchronizer;
        DatabasePath = databasePath;
    }

    public Harness Inner { get; }

    public IScheduler Scheduler { get; }

    public ScheduleSynchronizer Synchronizer { get; }

    public string DatabasePath { get; }

    /// <summary>Makes the routine's single pending chain trigger fire right now (same data, so it is still "ours"), as if its time had come.</summary>
    public async Task FireChainNowAsync(string routineId = "hb", string owner = "ea")
    {
        var ct = TestContext.Current.CancellationToken;
        var existing = Assert.Single(await Scheduler.GetTriggersOfJob(new JobKey(routineId, owner), ct));
        var data = new JobDataMap();
        foreach (var (key, value) in existing.JobDataMap)
        {
            data[key] = value;
        }

        data["plannedFire"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var now = TriggerBuilder.Create().WithIdentity(existing.Key).ForJob(existing.JobKey).UsingJobData(data).StartNow().Build();
        await Scheduler.RescheduleJob(existing.Key, now, ct);
    }
    /// <summary>Starts a scheduler on a fresh temp SQLite file, or on <paramref name="existingDatabase"/> to simulate a restart.</summary>
    public static async Task<QuartzHarness> StartAsync(Func<string, SchedulingOptions> options, ScriptedExecutor? executor = null, TimeProvider? time = null, string? existingDatabase = null)
    {
        var directory = existingDatabase is null ? new TempDirectory() : null;
        var path = existingDatabase ?? Path.Combine(directory!.Path, "scheduling.sqlite");
        var resolved = options(path);
        var inner = new Harness(resolved, executor, time, path);
        var scheduler = await new Durable.QuartzSchedulerFactory(resolved).CreateSchedulerAsync(TestContext.Current.CancellationToken);
        var synchronizer = new ScheduleSynchronizer(inner.Registry, scheduler, inner.Dispatcher, inner.KillSwitch, resolved, inner.Alerts, inner.Time);
        return new QuartzHarness(directory, inner, scheduler, synchronizer, path);
    }

    public async ValueTask DisposeAsync()
    {
        Synchronizer.Dispose();
        await Scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None);
        Inner.Dispose();
        _directory?.Dispose();
    }
}
