using System.Globalization;
using System.Text.Json;
using Capybara.Contracts.Routines;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.Hosting;
using Quartz;

namespace Capybara.Scheduling.Durable;

/// <summary>
/// Quartz job that dispatches one durable routine firing (FR-RTN-05) and then plans the routine's next fire, which
/// carries its own jitter (FR-RTN-03). The planned time travels in the trigger data, so a firing that arrives late — after
/// downtime, a kill-switch pause or a stalled thread pool — is recognised and handed to the catch-up policy.
/// </summary>
[DisallowConcurrentExecution]
public sealed class RoutineJob : IJob
{
    public const string RoutineJsonKey = "routineJson";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var schedulerContext = context.Scheduler.Context;
        if (!schedulerContext.TryGetValue(nameof(RoutineDispatcher), out var dispatcherObject) || dispatcherObject is not RoutineDispatcher dispatcher
            || !schedulerContext.TryGetValue(nameof(ScheduleSynchronizer), out var synchronizerObject) || synchronizerObject is not ScheduleSynchronizer synchronizer)
        {
            throw new InvalidOperationException("Routine dispatcher is not configured.");
        }

        var routineJson = context.MergedJobDataMap[RoutineJsonKey]?.ToString() ?? throw new InvalidOperationException("Routine job is missing routine data.");
        var routine = JsonSerializer.Deserialize<RoutineSpec>(routineJson) ?? throw new InvalidOperationException("Routine job data is invalid.");
        DateTimeOffset? scheduledFor = context.MergedJobDataMap.GetString(ScheduleSynchronizer.PlannedFireKey) is { } planned
            ? DateTimeOffset.Parse(planned, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

        var completed = false;
        try
        {
            await dispatcher.DispatchAsync(new RoutineFiring(routine, context.FireTimeUtc, Guid.NewGuid().ToString("n"), ScheduledFor: scheduledFor), cancellationToken);
            completed = true;
        }
        finally
        {
            // Cancellation means the host is shutting down: the pending-trigger repair at the next startup plans the next
            // fire. For any other outcome the chain must continue, even if recording the run failed.
            if (completed || !cancellationToken.IsCancellationRequested)
            {
                await synchronizer.AdvanceAsync(routine.Id, context.Trigger.Key, CancellationToken.None);
            }
        }
    }
}
