using System.Text.Json;
using Capybara.Contracts.Routines;
using Capybara.Policy.Safety;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.Durable;
using Capybara.Scheduling.Jitter;
using Capybara.Scheduling.Lifecycle;
using Quartz;

namespace Capybara.Scheduling.Hosting;

/// <summary>Registers Quartz jobs and triggers from the routine registry (FR-RTN-01, FR-RTN-05).</summary>
public sealed class ScheduleSynchronizer(RoutineRegistry registry, IScheduler scheduler, RoutineDispatcher dispatcher, KillSwitch killSwitch, Capybara.Scheduling.Dispatch.SchedulingOptions options)
{
    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        scheduler.Context.Add(nameof(RoutineDispatcher), dispatcher);
        foreach (var routine in registry.Routines.Where(r => r.Kind != TriggerKind.Event))
        {
            var jobKey = new JobKey(routine.Id, routine.Owner.Value);
            var job = Quartz.JobBuilder.Create<RoutineJob>()
                .WithIdentity(jobKey)
                .UsingJobData(RoutineJob.RoutineJsonKey, JsonSerializer.Serialize(routine))
                .StoreDurably()
                .Build();

            await scheduler.AddJob(job, new AddJobOptions { Replace = true }, cancellationToken);
            if (!registry.IsRunnable(routine))
            {
                continue;
            }

            var trigger = BuildTrigger(routine, jobKey);
            await scheduler.ScheduleJob(trigger, new ScheduleJobOptions { Replace = true }, cancellationToken);
        }

        killSwitch.Engaged += OnKillSwitchEngaged;
        killSwitch.Released += OnKillSwitchReleased;
        if (killSwitch.IsEngaged)
        {
            await scheduler.PauseAll(cancellationToken);
        }
    }

    private ITrigger BuildTrigger(RoutineSpec routine, JobKey jobKey)
    {
        var delay = RoutineJitter.Apply(routine.Id, DateTimeOffset.UtcNow, routine.Interval ?? TimeSpan.FromMinutes(5));
        var builder = TriggerBuilder.Create(TimeProvider.System)
            .ForJob(jobKey)
            .WithIdentity($"{routine.Id}.trigger", routine.Owner.Value)
            .StartAt(DateTimeOffset.UtcNow + delay);

        if (routine.Kind == TriggerKind.Cron && !string.IsNullOrWhiteSpace(routine.Cron))
        {
            var schedule = CronScheduleBuilder.Create(routine.Cron)
                .WithMisfireInstruction(options.CatchUpPolicy == CatchUpPolicy.FireOnceNow ? CronTriggerMisfireInstruction.FireAndProceed : CronTriggerMisfireInstruction.DoNothing);
            return builder.WithSchedule(schedule).Build();
        }

        var simple = SimpleScheduleBuilder.Create()
            .RepeatForever()
            .WithInterval(routine.Interval ?? TimeSpan.FromMinutes(5))
            .WithMisfireInstruction(options.CatchUpPolicy == CatchUpPolicy.FireOnceNow ? SimpleTriggerMisfireInstruction.FireNow : SimpleTriggerMisfireInstruction.NextWithRemainingCount);
        return builder.WithSchedule(simple).Build();
    }

    private async void OnKillSwitchEngaged(object? sender, KillSwitchEventArgs e) => await scheduler.PauseAll(CancellationToken.None);

    private async void OnKillSwitchReleased(object? sender, KillSwitchEventArgs e) => await scheduler.ResumeAll(CancellationToken.None);
}
