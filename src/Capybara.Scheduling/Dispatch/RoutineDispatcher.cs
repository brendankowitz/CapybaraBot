using Capybara.Contracts.Routines;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Policy.Safety;
using Capybara.Scheduling.Execution;
using Capybara.Scheduling.History;
using Capybara.Scheduling.Lifecycle;
using Capybara.Scheduling.Planning;

namespace Capybara.Scheduling.Dispatch;

/// <summary>Testable scheduler core that enforces safety, lifecycle, concurrency and budget before execution (FR-RTN-05, FR-SAF-06).</summary>
public sealed class RoutineDispatcher : IDisposable
{
    private readonly IRoutineExecutor _executor;
    private readonly RoutineRunHistory _history;
    private readonly IAlertSink _alerts;
    private readonly CostLedger _costLedger;
    private readonly RoutineRegistry _registry;
    private readonly KillSwitch _killSwitch;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _concurrency;
    private readonly Func<RoutineSpec, bool> _inputSourceExists;
    private readonly SchedulingOptions _options;

    /// <summary>
    /// Creates the dispatcher. <paramref name="executor"/> is implemented by the runtime project and must be registered by the
    /// composition root; <paramref name="options"/> defaults to <see cref="SchedulingOptions"/> defaults; and
    /// <paramref name="inputSourceExists"/> checks a routine's input source (default: always present).
    /// </summary>
    public RoutineDispatcher(
        IRoutineExecutor executor,
        RoutineRunHistory history,
        IAlertSink alerts,
        CostLedger costLedger,
        RoutineRegistry registry,
        KillSwitch killSwitch,
        TimeProvider time,
        SchedulingOptions? options = null,
        Func<RoutineSpec, bool>? inputSourceExists = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(costLedger);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(killSwitch);
        ArgumentNullException.ThrowIfNull(time);

        _options = options ?? new SchedulingOptions();
        _executor = executor;
        _history = history;
        _alerts = alerts;
        _costLedger = costLedger;
        _registry = registry;
        _killSwitch = killSwitch;
        _time = time;
        _concurrency = new SemaphoreSlim(Math.Max(1, _options.ConcurrencyCap));
        _inputSourceExists = inputSourceExists ?? (_ => true);
    }

    /// <summary>
    /// Runs a routine as a dry run now (FR-RTN-08), for testing before it is enabled. A dry run bypasses the lifecycle
    /// state and active hours — the routine may be disabled — but never the kill switch, concurrency cap or budget.
    /// </summary>
    public Task<RoutineExecutionResult> DryRunAsync(string routineId, CancellationToken cancellationToken = default)
    {
        var routine = _registry.Find(routineId) ?? throw new KeyNotFoundException($"Routine '{routineId}' is not registered.");
        return DispatchAsync(new RoutineFiring(routine, _time.GetUtcNow(), Guid.NewGuid().ToString("n"), DryRun: true), cancellationToken);
    }

    /// <summary>
    /// Applies the safety, lifecycle, concurrency and budget gates, then executes the routine. Every outcome other than
    /// success is recorded and raises an alert (FR-RTN-05); only a silent heartbeat (<see cref="RoutineExecutionStatus.NothingToReport"/>,
    /// or outside active hours) leaves no record.
    /// </summary>
    public async Task<RoutineExecutionResult> DispatchAsync(RoutineFiring firing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firing);
        var routine = firing.Routine;
        var dryRun = firing.DryRun || routine.DryRun;

        if (firing.IsMisfire)
        {
            await RecordAsync(routine, firing.FireTime, RoutineRunOutcome.Missed, "missed while scheduler was down", cancellationToken);
            return new RoutineExecutionResult(RoutineExecutionStatus.Skipped, "missed while scheduler was down");
        }

        if (firing.ScheduledFor is { } scheduledFor && firing.FireTime - scheduledFor > _options.MisfireTolerance)
        {
            var late = firing.FireTime - scheduledFor;
            var catchUp = _options.CatchUpPolicy == CatchUpPolicy.FireOnceNow;
            var summary = $"missed scheduled run at {scheduledFor:u} (fired {late.TotalMinutes:0.#} min late); " +
                (catchUp ? "catching up with one run now" : "skipped by catch-up policy, next regular run continues");
            await RecordAsync(routine, scheduledFor, RoutineRunOutcome.Missed, summary, cancellationToken);
            if (!catchUp)
            {
                return new RoutineExecutionResult(RoutineExecutionStatus.Skipped, summary);
            }
        }

        if (_killSwitch.IsEngaged || _killSwitch.IsBlocked(routine.Owner))
        {
            return await StopBeforeExecutionAsync(routine, firing.FireTime, RoutineRunOutcome.Skipped, "kill switch engaged", cancellationToken);
        }

        if (!dryRun && !_registry.IsRunnable(routine))
        {
            return await StopBeforeExecutionAsync(routine, firing.FireTime, RoutineRunOutcome.Skipped, _registry.GetState(routine.Id).Reason ?? "routine paused", cancellationToken);
        }

        if (!dryRun && routine.Kind == TriggerKind.Heartbeat && !RoutinePlanner.IsWithinActiveHours(routine, TimeZoneInfo.ConvertTime(firing.FireTime, _options.TimeZone)))
        {
            return new RoutineExecutionResult(RoutineExecutionStatus.NothingToReport, "outside active hours");
        }

        if (!_concurrency.Wait(0, cancellationToken))
        {
            return await StopBeforeExecutionAsync(routine, firing.FireTime, RoutineRunOutcome.Skipped, "fleet concurrency cap saturated; run skipped", cancellationToken);
        }

        try
        {
            if (!_costLedger.CanSpend(routine.Owner))
            {
                return await StopBeforeExecutionAsync(routine, firing.FireTime, RoutineRunOutcome.OverBudget, "routine budget exhausted", cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(routine.InputSource) && !_inputSourceExists(routine))
            {
                return routine.OnMissingInput switch
                {
                    MissingInputBehavior.RunWithoutInput => await ExecuteAsync(firing, cancellationToken),
                    MissingInputBehavior.Fail => await StopBeforeExecutionAsync(routine, firing.FireTime, RoutineRunOutcome.Failed, "required input source missing", cancellationToken),
                    _ => await StopBeforeExecutionAsync(routine, firing.FireTime, RoutineRunOutcome.Skipped, "input source missing", cancellationToken),
                };
            }

            return await ExecuteAsync(firing, cancellationToken);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void Dispose() => _concurrency.Dispose();

    private async Task<RoutineExecutionResult> ExecuteAsync(RoutineFiring firing, CancellationToken cancellationToken)
    {
        var routine = firing.Routine;
        var dryRun = firing.DryRun || routine.DryRun;
        var invocation = new RoutineInvocation(
            routine,
            routine.Kind switch
            {
                TriggerKind.Heartbeat => RoutineTriggerKind.Heartbeat,
                TriggerKind.Event => RoutineTriggerKind.Event,
                _ => RoutineTriggerKind.Cron,
            },
            firing.FireTime,
            firing.RunId,
            dryRun,
            firing.EventPayload,
            routine.Model,
            routine.Kind == TriggerKind.Heartbeat ? SessionTarget.Main : SessionTarget.IsolatedTask);

        RoutineExecutionResult result;
        try
        {
            result = await _executor.ExecuteAsync(invocation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new RoutineExecutionResult(RoutineExecutionStatus.Failed, ex.Message, Model: routine.Model);
        }

        // A silent heartbeat leaves no trace — except a dry run, where the user is waiting for the answer.
        if (result.Status == RoutineExecutionStatus.NothingToReport && routine.Kind == TriggerKind.Heartbeat && !dryRun)
        {
            return result;
        }

        await RecordAsync(routine, firing.FireTime, ToOutcome(result.Status), dryRun ? $"[dry run] {result.Summary}" : result.Summary, cancellationToken, result);
        if (result.CreditsUsed > 0)
        {
            await _costLedger.RecordAsync(new UsageRecord(routine.Owner, result.Model ?? routine.Model, result.CreditsUsed, _time.GetUtcNow(), RoutineId: routine.Id), cancellationToken);
        }

        return result;
    }

    private async Task<RoutineExecutionResult> StopBeforeExecutionAsync(
        RoutineSpec routine,
        DateTimeOffset at,
        RoutineRunOutcome outcome,
        string summary,
        CancellationToken cancellationToken)
    {
        await RecordAsync(routine, at, outcome, summary, cancellationToken);
        return new RoutineExecutionResult(ToStatus(outcome), summary, Model: routine.Model);
    }

    private async ValueTask RecordAsync(
        RoutineSpec routine,
        DateTimeOffset at,
        RoutineRunOutcome outcome,
        string? summary,
        CancellationToken cancellationToken,
        RoutineExecutionResult? result = null)
    {
        var evidence = result?.Evidence is null
            ? null
            : (IReadOnlyList<string>)[
                .. (result.Evidence.Sources ?? []),
                .. (result.Evidence.ActionLog ?? []),
                .. (result.Evidence.UnverifiableItems ?? []),
                .. (result.Evidence.DeniedApprovals ?? []).Select(d => $"denied in dry run: {d}")];
        await _history.RecordAsync(new RoutineRun(routine.Id, at, outcome, summary, result?.CreditsUsed ?? 0, evidence), cancellationToken);
    }

    private static RoutineRunOutcome ToOutcome(RoutineExecutionStatus status) => status switch
    {
        RoutineExecutionStatus.Succeeded or RoutineExecutionStatus.NothingToReport => RoutineRunOutcome.Succeeded,
        RoutineExecutionStatus.Skipped => RoutineRunOutcome.Skipped,
        RoutineExecutionStatus.Refused => RoutineRunOutcome.Refused,
        RoutineExecutionStatus.OverBudget => RoutineRunOutcome.OverBudget,
        _ => RoutineRunOutcome.Failed,
    };

    private static RoutineExecutionStatus ToStatus(RoutineRunOutcome outcome) => outcome switch
    {
        RoutineRunOutcome.Skipped or RoutineRunOutcome.Missed => RoutineExecutionStatus.Skipped,
        RoutineRunOutcome.Refused => RoutineExecutionStatus.Refused,
        RoutineRunOutcome.OverBudget => RoutineExecutionStatus.OverBudget,
        RoutineRunOutcome.Succeeded => RoutineExecutionStatus.Succeeded,
        _ => RoutineExecutionStatus.Failed,
    };
}
