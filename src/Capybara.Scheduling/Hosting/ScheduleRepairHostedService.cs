using Capybara.Observability.Alerts;
using Capybara.Scheduling.Dispatch;
using Microsoft.Extensions.Hosting;

namespace Capybara.Scheduling.Hosting;

/// <summary>
/// Periodically runs <see cref="ScheduleSynchronizer.RepairAsync"/> so a routine that lost its trigger — however it
/// happened — is re-planned within <see cref="SchedulingOptions.RepairInterval"/> instead of staying dead (FR-RTN-05).
/// A failed pass raises an alert and the timer keeps running.
/// </summary>
public sealed class ScheduleRepairHostedService(ScheduleSynchronizer synchronizer, SchedulingOptions options, IAlertSink alerts, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RepairInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await synchronizer.RepairAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await alerts.PublishAsync(new Alert("scheduler.error", AlertSeverity.Critical, $"Routine schedule repair failed: {ex.Message}", time.GetUtcNow()), CancellationToken.None);
            }
        }
    }
}
