using System.Globalization;
using System.Text.Json;
using Capybara.Contracts.Routines;
using Capybara.Observability.Alerts;
using Capybara.Policy.Safety;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.Durable;
using Capybara.Scheduling.Jitter;
using Capybara.Scheduling.Lifecycle;
using Capybara.Scheduling.Planning;
using Quartz;
using SchedulingOptions = Capybara.Scheduling.Dispatch.SchedulingOptions;

namespace Capybara.Scheduling.Hosting;

/// <summary>
/// Keeps the durable Quartz store in step with the <see cref="RoutineRegistry"/> (FR-RTN-01, FR-RTN-05).
/// <para>
/// Each heartbeat or cron routine has exactly one pending one-shot trigger. When it fires, <see cref="RoutineJob"/> asks
/// <see cref="AdvanceAsync"/> for the next one, which is how every fire gets its own ±10% jitter (FR-RTN-03) and how
/// several fires missed during downtime collapse into a single, explicit catch-up decision (see <see cref="CatchUpPolicy"/>).
/// <see cref="SynchronizeAsync"/> is idempotent: it runs at startup, and again whenever the registry changes (config reload,
/// enable, disable, pause, resume), and repairs any routine whose chain was interrupted by a crash.
/// </para>
/// <para>The kill switch pauses every Quartz trigger when engaged and resumes them when released (FR-SAF-06).</para>
/// </summary>
public sealed class ScheduleSynchronizer(
    RoutineRegistry registry,
    IScheduler scheduler,
    RoutineDispatcher dispatcher,
    KillSwitch killSwitch,
    SchedulingOptions options,
    IAlertSink alerts,
    TimeProvider time) : IDisposable
{
    private const string SignatureKey = "scheduleSignature";

    /// <summary>Trigger data key holding the ISO-8601 time the trigger was planned for.</summary>
    internal const string PlannedFireKey = "plannedFire";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _attached;

    /// <summary>Registers jobs and triggers for every runnable routine and removes those that are gone, disabled or paused.</summary>
    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Attach();
            await ReconcileAsync(cancellationToken);
            if (killSwitch.IsEngaged)
            {
                await scheduler.PauseAll(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Schedules the next fire of <paramref name="routineId"/> after a run finished, unless the routine was removed,
    /// paused or disabled in the meantime, or another trigger is already pending. <paramref name="current"/> is the
    /// trigger that just fired; it still exists while its job executes and is not counted as pending.
    /// </summary>
    public async Task AdvanceAsync(string routineId, TriggerKey current, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (registry.Find(routineId) is { } routine && registry.IsRunnable(routine) && routine.Kind != TriggerKind.Event)
            {
                try
                {
                    await EnsureTriggerAsync(routine, current, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await PublishScheduleFailureAsync(routine, ex, cancellationToken);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops reacting to registry and kill switch changes. Call before shutting the scheduler down.</summary>
    public void Detach()
    {
        if (_attached)
        {
            registry.Changed -= OnRegistryChanged;
            killSwitch.Engaged -= OnKillSwitchEngaged;
            killSwitch.Released -= OnKillSwitchReleased;
            _attached = false;
        }

        _lifetime.Cancel();
    }

    public void Dispose()
    {
        Detach();
        _lifetime.Dispose();
        _gate.Dispose();
    }

    /// <summary>The next planned fire of a routine after <paramref name="after"/>; null if it never fires again.</summary>
    internal DateTimeOffset? NextPlannedFire(RoutineSpec routine, DateTimeOffset after)
    {
        if (routine.Kind == TriggerKind.Cron && !string.IsNullOrWhiteSpace(routine.Cron))
        {
            return CronSchedule.TryParse(routine.Cron, options.TimeZone, out var cron, out var error)
                ? CronSchedule.NextFire(cron, after)
                : throw new FormatException($"cron expression '{routine.Cron}' is invalid: {error}");
        }

        return RoutineJitter.NextFire(routine, after);
    }

    private void Attach()
    {
        if (_attached)
        {
            return;
        }

        scheduler.Context[nameof(RoutineDispatcher)] = dispatcher;
        scheduler.Context[nameof(ScheduleSynchronizer)] = this;
        registry.Changed += OnRegistryChanged;
        killSwitch.Engaged += OnKillSwitchEngaged;
        killSwitch.Released += OnKillSwitchReleased;
        _attached = true;
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var routines = registry.Routines.Where(r => r.Kind != TriggerKind.Event).ToList();
        var wanted = routines.Select(JobKeyOf).ToHashSet();

        foreach (var jobKey in await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), cancellationToken))
        {
            if (!wanted.Contains(jobKey))
            {
                await scheduler.DeleteJob(jobKey, cancellationToken);
            }
        }

        foreach (var routine in routines)
        {
            try
            {
                var job = JobBuilder.Create<RoutineJob>()
                    .WithIdentity(JobKeyOf(routine))
                    .UsingJobData(RoutineJob.RoutineJsonKey, JsonSerializer.Serialize(routine))
                    .StoreDurably()
                    .Build();
                await scheduler.AddJob(job, new AddJobOptions { Replace = true }, cancellationToken);

                if (registry.IsRunnable(routine))
                {
                    await EnsureTriggerAsync(routine, current: null, cancellationToken);
                }
                else
                {
                    var triggers = await scheduler.GetTriggersOfJob(JobKeyOf(routine), cancellationToken);
                    await scheduler.UnscheduleJobs([.. triggers.Select(t => t.Key)], cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad routine must not stop the others from being scheduled, but it must not fail silently either.
                await PublishScheduleFailureAsync(routine, ex, cancellationToken);
            }
        }
    }

    private ValueTask PublishScheduleFailureAsync(RoutineSpec routine, Exception ex, CancellationToken cancellationToken) =>
        alerts.PublishAsync(
            new Alert("routine.schedule-failed", AlertSeverity.Critical, $"Routine '{routine.Id}' could not be scheduled: {ex.Message}", time.GetUtcNow(), routine.Owner.Value),
            cancellationToken);

    private async Task EnsureTriggerAsync(RoutineSpec routine, TriggerKey? current, CancellationToken cancellationToken)
    {
        var jobKey = JobKeyOf(routine);
        var signature = SignatureOf(routine);
        var pending = false;
        foreach (var trigger in await scheduler.GetTriggersOfJob(jobKey, cancellationToken))
        {
            if (trigger.Key.Equals(current))
            {
                continue;
            }

            if (trigger.JobDataMap.GetString(SignatureKey) == signature)
            {
                pending = true;
            }
            else
            {
                // The routine's schedule was edited: drop the trigger planned under the old definition.
                await scheduler.UnscheduleJob(trigger.Key, cancellationToken);
            }
        }

        if (pending || NextPlannedFire(routine, time.GetUtcNow()) is not { } planned)
        {
            return;
        }

        var next = TriggerBuilder.Create()
            .ForJob(jobKey)
            .WithIdentity($"{routine.Id}.{planned.UtcTicks}", routine.Owner.Value)
            .UsingJobData(PlannedFireKey, planned.ToString("O", CultureInfo.InvariantCulture))
            .UsingJobData(SignatureKey, signature)
            .StartAt(planned)
            // If the process was down at the planned time the trigger still fires once on startup; RoutineDispatcher then
            // applies the explicit catch-up policy. It must never be dropped here or the routine's chain would end.
            .WithSimpleSchedule(s => s.WithMisfireInstruction(SimpleTriggerMisfireInstruction.FireNow))
            .Build();
        await scheduler.ScheduleJob(next, new ScheduleJobOptions(), cancellationToken);
    }

    private static JobKey JobKeyOf(RoutineSpec routine) => new(routine.Id, routine.Owner.Value);

    private static string SignatureOf(RoutineSpec routine) =>
        routine.Kind == TriggerKind.Cron && !string.IsNullOrWhiteSpace(routine.Cron)
            ? $"cron:{routine.Cron}"
            : $"interval:{routine.Interval?.Ticks.ToString(CultureInfo.InvariantCulture)}";

    private void OnRegistryChanged(object? sender, EventArgs e) =>
        _ = RunSafelyAsync("resynchronize routines", ct => SynchronizeAsync(ct));

    private void OnKillSwitchEngaged(object? sender, KillSwitchEventArgs e) =>
        _ = RunSafelyAsync("pause the scheduler for the kill switch", async ct => await scheduler.PauseAll(ct));

    private void OnKillSwitchReleased(object? sender, KillSwitchEventArgs e) =>
        _ = RunSafelyAsync("resume the scheduler after the kill switch", async ct => await scheduler.ResumeAll(ct));

    /// <summary>Runs an event-driven scheduler action. A failure raises an alert instead of vanishing in an unobserved task.</summary>
    private async Task RunSafelyAsync(string what, Func<CancellationToken, Task> action)
    {
        try
        {
            await action(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await alerts.PublishAsync(new Alert("scheduler.error", AlertSeverity.Critical, $"Failed to {what}: {ex.Message}", time.GetUtcNow()), CancellationToken.None);
        }
    }
}
