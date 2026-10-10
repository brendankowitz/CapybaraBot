using Capybara.Observability.Alerts;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.Lifecycle;
using Microsoft.Extensions.Hosting;

namespace Capybara.Scheduling.Hosting;

/// <summary>
/// Runs <see cref="RoutineAutoPauser.CheckAsync"/> on a timer so unattended routines actually auto-pause (FR-RTN-06).
/// A failed check raises an alert and the timer keeps running.
/// </summary>
public sealed class AutoPauseHostedService(RoutineAutoPauser autoPauser, SchedulingOptions options, IAlertSink alerts, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.AutoPauseCheckInterval, time);
        do
        {
            try
            {
                await autoPauser.CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await alerts.PublishAsync(new Alert("scheduler.error", AlertSeverity.Critical, $"Routine auto-pause check failed: {ex.Message}", time.GetUtcNow()), CancellationToken.None);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
