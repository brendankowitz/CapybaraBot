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
    private TaskCompletionSource? _stopped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await synchronizer.SynchronizeAsync(cancellationToken);
        await scheduler.Start(cancellationToken);
        await synchronizer.SynchronizeAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The host can stop twice at once (a test host's StopAsync and the app's own RunAsync both react to
        // ApplicationStopping). Quartz runs its shutdown for the first caller only and returns to any other at once,
        // so a second caller that went on to dispose the container would tear it down under a scheduler still
        // shutting down. Every caller therefore waits for the one shutdown; a caller's token bounds only its wait.
        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _stopped, mine, null) is { } running)
        {
            await running.Task.WaitAsync(cancellationToken);
            return;
        }

        try
        {
            synchronizer.Detach();
            await scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken);
            mine.SetResult();
        }
        catch (Exception ex)
        {
            mine.SetException(ex);
            throw;
        }
    }
}
