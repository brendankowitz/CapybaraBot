using System.Text.Json;
using Fleet.Contracts.Routines;
using Fleet.Scheduling.Dispatch;
using Quartz;

namespace Fleet.Scheduling.Durable;

/// <summary>Quartz job that dispatches one durable routine firing (FR-RTN-05).</summary>
[DisallowConcurrentExecution]
public sealed class RoutineJob : IJob
{
    public const string RoutineJsonKey = "routineJson";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        if (!context.Scheduler.Context.TryGetValue(nameof(RoutineDispatcher), out var dispatcherObject) || dispatcherObject is not RoutineDispatcher dispatcher)
        {
            throw new InvalidOperationException("Routine dispatcher is not configured.");
        }
        var routineJson = context.MergedJobDataMap[RoutineJsonKey]?.ToString() ?? throw new InvalidOperationException("Routine job is missing routine data.");
        var routine = JsonSerializer.Deserialize<RoutineSpec>(routineJson) ?? throw new InvalidOperationException("Routine job data is invalid.");
        var firing = new RoutineFiring(routine, context.FireTimeUtc, Guid.NewGuid().ToString("n"), IsMisfire: context.ScheduledFireTimeUtc is { } scheduled && context.FireTimeUtc - scheduled > TimeSpan.FromSeconds(1));
        await dispatcher.DispatchAsync(firing, cancellationToken);
    }
}
