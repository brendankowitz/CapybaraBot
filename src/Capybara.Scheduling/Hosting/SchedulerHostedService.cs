using Microsoft.Extensions.Hosting;
using Quartz;

namespace Capybara.Scheduling.Hosting;

/// <summary>Generic Host lifecycle adapter for the durable scheduler (FR-RTN-05).</summary>
public sealed class SchedulerHostedService(IScheduler scheduler, ScheduleSynchronizer synchronizer) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await synchronizer.SynchronizeAsync(cancellationToken);
        await scheduler.Start(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        synchronizer.Detach();
        await scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken);
    }
}
