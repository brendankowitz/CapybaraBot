using Microsoft.Extensions.Hosting;
using Quartz;

namespace Capybara.Scheduling.Hosting;

/// <summary>
/// Generic Host lifecycle adapter for the durable scheduler (FR-RTN-05). Start order matters: reconcile, start (Quartz's
/// start-up recovery purges triggers that completed just before a crash), then reconcile again so a routine whose
/// chain was cut by that crash gets a new trigger.
/// </summary>
public sealed class SchedulerHostedService(IScheduler scheduler, ScheduleSynchronizer synchronizer) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await synchronizer.SynchronizeAsync(cancellationToken);
        await scheduler.Start(cancellationToken);
        await synchronizer.SynchronizeAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        synchronizer.Detach();
        await scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken);
    }
}
