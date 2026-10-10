using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Hosting.Backup;

/// <summary>Runs <see cref="BackupService"/> on a fixed interval; failures are logged and the next tick still runs (FR-HST-05).</summary>
public sealed partial class BackupHostedService(BackupService service, BackupScheduleOptions schedule, TimeProvider timeProvider, ILogger<BackupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(schedule.Interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.BackupAsync(cancellationToken);
            LogBackupCompleted(logger, result.Path, result.Manifest.Entries.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBackupFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup written to {Path} ({Files} files).")]
    private static partial void LogBackupCompleted(ILogger logger, string path, int files);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled backup failed.")]
    private static partial void LogBackupFailed(ILogger logger, Exception exception);
}
