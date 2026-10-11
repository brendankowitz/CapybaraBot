using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capybara.Hosting.Backup;

/// <summary>
/// Runs <see cref="BackupService"/> daily at <see cref="BackupScheduleOptions.UtcTimeOfDay"/> (FR-HST-05). On start it runs immediately
/// when the newest archive predates the most recent scheduled slot, so frequent restarts or downtime across the slot never skip a night.
/// Failures are logged and the next slot still runs.
/// </summary>
public sealed partial class BackupHostedService(BackupService service, BackupScheduleOptions schedule, TimeProvider timeProvider, ILogger<BackupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (await IsOverdueAsync(stoppingToken))
            {
                LogCatchUp(logger);
                await RunOnceAsync(stoppingToken);
            }

            while (true)
            {
                var now = timeProvider.GetUtcNow();
                await Task.Delay(schedule.NextRunAfter(now) - now, timeProvider, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
    }

    private async Task<bool> IsOverdueAsync(CancellationToken cancellationToken)
    {
        try
        {
            var last = await service.GetLastBackupTimeAsync(cancellationToken);
            return last is null || last < schedule.LastRunAtOrBefore(timeProvider.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogLastBackupUnknown(logger, ex);
            return true;
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.BackupAsync(cancellationToken);
            LogBackupCompleted(logger, result.Path, result.Manifest.Entries.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Only a shutdown-driven cancellation propagates; any other failure (including a stray cancellation) is logged so the worker stays alive.
            LogBackupFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Latest backup is older than the last scheduled slot (or missing); running a catch-up backup now.")]
    private static partial void LogCatchUp(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not determine the last backup time; assuming a backup is due.")]
    private static partial void LogLastBackupUnknown(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup written to {Path} ({Files} files).")]
    private static partial void LogBackupCompleted(ILogger logger, string path, int files);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled backup failed.")]
    private static partial void LogBackupFailed(ILogger logger, Exception exception);
}
