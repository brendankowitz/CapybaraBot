using System.Collections.Concurrent;
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
using Capybara.Scheduling.Validation;
using Quartz;
using SchedulingOptions = Capybara.Scheduling.Dispatch.SchedulingOptions;

namespace Capybara.Scheduling.Hosting;

/// <summary>
/// Keeps the durable Quartz store in step with the <see cref="RoutineRegistry"/> (FR-RTN-01, FR-RTN-05).
/// <para>
/// Each heartbeat or cron routine has one pending one-shot trigger. When it fires, <see cref="RoutineJob"/> calls
/// <see cref="AdvanceAsync"/> to plan the next one, which is how every fire gets its own ±10% jitter (FR-RTN-03) and how
/// several fires missed during downtime collapse into one explicit catch-up decision (see <see cref="CatchUpPolicy"/>).
/// </para>
/// <para>
/// Because the chain lives in one trigger at a time, a crash between a fire and the re-plan, or a failed re-plan, would
/// leave a routine with no trigger at all. Three things prevent that from going unnoticed or unrepaired:
/// (1) a trigger only counts as pending while Quartz considers it live (Normal, Paused or Blocked) — a Complete, Error
/// or missing trigger left over from a crash is removed and replaced; (2) <see cref="SchedulerHostedService"/> reconciles
/// again after the scheduler has started, because Quartz's start-up recovery purges completed triggers; and (3)
/// <see cref="AdvanceAsync"/> retries with backoff and alerts when it finally fails, and <see cref="RepairAsync"/> (run
/// periodically by <see cref="ScheduleRepairHostedService"/>) re-plans any runnable routine that lacks a live trigger.
/// </para>
/// <para>
/// <see cref="SynchronizeAsync"/> is idempotent and also runs whenever the registry changes (config reload, enable,
/// disable, pause, resume). The kill switch pauses every Quartz trigger when engaged and resumes them on release (FR-SAF-06).
/// </para>
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
    private const int MaxRescheduleAttempts = 4;

    /// <summary>Trigger data key holding the ISO-8601 time the trigger was planned for.</summary>
    internal const string PlannedFireKey = "plannedFire";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, int> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _reported = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CronAnalysis> _crons = new(StringComparer.Ordinal);
    private bool _attached;

    /// <summary>Registers jobs and triggers for every runnable routine and removes those that are gone, disabled or paused.</summary>
    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Attach();
            await ReconcileAsync(cancellationToken);
            await HonourKillSwitchAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Safety net: gives every runnable heartbeat or cron routine that has no live trigger a new one, and alerts about
    /// any that cannot be scheduled. Routines whose job is running are skipped; their completion plans the next fire.
    /// </summary>
    public async Task RepairAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var routine in registry.Routines.Where(r => r.Kind != TriggerKind.Event && registry.IsRunnable(r)))
            {
                await EnsureTriggerReportingAsync(routine, current: null, cancellationToken);
            }

            await HonourKillSwitchAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Plans the next fire of <paramref name="routineId"/> after a run finished, unless the routine was removed, paused or
    /// disabled in the meantime, or a live trigger is already pending. <paramref name="current"/> is the trigger that just
    /// fired; it still exists while its job executes and is not counted as pending. A scheduler failure is retried with
    /// exponential backoff (<see cref="SchedulingOptions.RescheduleRetryDelay"/>, four attempts) and then alerted; a
    /// routine that can never be scheduled (invalid or too-frequent cron) is alerted at once.
    /// </summary>
    public async Task AdvanceAsync(string routineId, TriggerKey current, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        var delay = options.RescheduleRetryDelay;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _gate.WaitAsync(token);
                    try
                    {
                        if (registry.Find(routineId) is { } routine && registry.IsRunnable(routine) && routine.Kind != TriggerKind.Event)
                        {
                            await EnsureTriggerAsync(routine, current, token);
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    return;
                }
                catch (UnschedulableRoutineException ex)
                {
                    await ReportAsync(routineId, ex.Message, CancellationToken.None);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (attempt >= MaxRescheduleAttempts)
                    {
                        await ReportAsync(routineId, $"could not plan the next run after {attempt} attempts: {ex.Message}", CancellationToken.None);
                        return;
                    }

                    await Task.Delay(delay, time, token);
                    delay *= 2;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Shutting down: the next start's reconcile plans the next fire.
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

    /// <summary>Marks the routine's job as executing, so reconciliation leaves its next fire to the job's own completion.</summary>
    internal void BeginRun(string routineId) => _running.AddOrUpdate(routineId, 1, static (_, n) => n + 1);

    internal void EndRun(string routineId)
    {
        if (_running.AddOrUpdate(routineId, 0, static (_, n) => n - 1) <= 0)
        {
            _running.TryRemove(routineId, out _);
        }
    }

    /// <summary>
    /// The next planned fire of a routine after <paramref name="after"/>; null if it never fires again.
    /// Fails closed — <see cref="UnschedulableRoutineException"/> — for anything the FR-RTN-03 limits forbid, whatever
    /// got it into the registry.
    /// </summary>
    internal DateTimeOffset? NextPlannedFire(RoutineSpec routine, DateTimeOffset after)
    {
        if (routine.Kind == TriggerKind.Cron && !string.IsNullOrWhiteSpace(routine.Cron))
        {
            var analysis = _crons.GetOrAdd(routine.Cron, expression => CronAnalysis.Analyze(expression, options.TimeZone));
            return analysis.Problem is null
                ? CronSchedule.NextFire(analysis.Cron!, after)
                : throw new UnschedulableRoutineException($"cron expression '{routine.Cron}' {analysis.Problem}");
        }

        if (routine.Interval is not { } interval)
        {
            throw new UnschedulableRoutineException("it has no interval and no cron expression");
        }

        return interval < RoutineLimits.MinimumInterval
            ? throw new UnschedulableRoutineException($"interval {interval} is below the {RoutineLimits.MinimumInterval.TotalMinutes}-minute minimum")
            : RoutineJitter.NextFire(routine, after);
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

    private async Task HonourKillSwitchAsync(CancellationToken cancellationToken)
    {
        if (killSwitch.IsEngaged)
        {
            await scheduler.PauseAll(cancellationToken);
        }
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad routine must not stop the others from being scheduled, but it must not fail silently either.
                await ReportAsync(routine.Id, $"could not be stored: {ex.Message}", cancellationToken);
                continue;
            }

            if (registry.IsRunnable(routine))
            {
                await EnsureTriggerReportingAsync(routine, current: null, cancellationToken);
            }
            else
            {
                await UnscheduleAllAsync(routine, cancellationToken);
            }
        }
    }

    private async Task UnscheduleAllAsync(RoutineSpec routine, CancellationToken cancellationToken)
    {
        try
        {
            var triggers = await scheduler.GetTriggersOfJob(JobKeyOf(routine), cancellationToken);
            await scheduler.UnscheduleJobs([.. triggers.Select(t => t.Key)], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ReportAsync(routine.Id, $"could not be unscheduled: {ex.Message}", cancellationToken);
        }
    }

    private async Task EnsureTriggerReportingAsync(RoutineSpec routine, TriggerKey? current, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureTriggerAsync(routine, current, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ReportAsync(routine.Id, ex is UnschedulableRoutineException ? ex.Message : $"could not be scheduled: {ex.Message}", cancellationToken);
        }
    }

    private async Task EnsureTriggerAsync(RoutineSpec routine, TriggerKey? current, CancellationToken cancellationToken)
    {
        // A running job plans its own next fire when it completes; planning one now would measure the interval from the wrong moment.
        if (current is null && _running.ContainsKey(routine.Id))
        {
            return;
        }

        var jobKey = JobKeyOf(routine);
        var signature = SignatureOf(routine);
        var pending = false;
        foreach (var trigger in await scheduler.GetTriggersOfJob(jobKey, cancellationToken))
        {
            if (trigger.Key.Equals(current) || !trigger.JobDataMap.ContainsKey(SignatureKey))
            {
                // The trigger that just fired, or one we did not plan (an on-demand TriggerJob run).
                continue;
            }

            var state = await scheduler.GetTriggerState(trigger.Key, cancellationToken);
            var live = state is TriggerState.Normal or TriggerState.Paused or TriggerState.Blocked;
            if (!pending && live && trigger.JobDataMap.GetString(SignatureKey) == signature)
            {
                pending = true;
            }
            else
            {
                // Completed, errored or vanished (left over from a crash), planned under an older schedule, or a duplicate.
                await scheduler.UnscheduleJob(trigger.Key, cancellationToken);
            }
        }

        if (!pending && NextPlannedFire(routine, time.GetUtcNow()) is { } planned)
        {
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

        _reported.TryRemove(routine.Id, out _);
    }

    /// <summary>Raises a schedule-failed alert, once per distinct problem until the routine schedules successfully again.</summary>
    private async ValueTask ReportAsync(string routineId, string problem, CancellationToken cancellationToken)
    {
        if (_reported.TryGetValue(routineId, out var last) && last == problem)
        {
            return;
        }

        _reported[routineId] = problem;
        var owner = registry.Find(routineId)?.Owner.Value;
        await alerts.PublishAsync(
            new Alert("routine.schedule-failed", AlertSeverity.Critical, $"Routine '{routineId}' is not scheduled: {problem}", time.GetUtcNow(), owner),
            cancellationToken);
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

    /// <summary>A cron expression parsed once, with the reason it can never be scheduled (if any).</summary>
    private sealed record CronAnalysis(CronExpression? Cron, string? Problem)
    {
        public static CronAnalysis Analyze(string expression, TimeZoneInfo timeZone)
        {
            if (!CronSchedule.TryParse(expression, timeZone, out var cron, out var error))
            {
                return new CronAnalysis(null, $"is invalid: {error}");
            }

            return CronSchedule.TooFrequentGap(cron) is { } gap
                ? new CronAnalysis(null, $"can fire {gap.TotalSeconds:0} seconds apart, below the {RoutineLimits.MinimumInterval.TotalMinutes}-minute minimum")
                : new CronAnalysis(cron, null);
        }
    }
}

/// <summary>A routine that the FR-RTN-03 limits forbid scheduling; planning fails closed rather than firing it.</summary>
internal sealed class UnschedulableRoutineException(string message) : InvalidOperationException(message);
