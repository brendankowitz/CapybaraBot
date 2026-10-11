using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Memory.Consolidation;

/// <summary>Runs consolidation once per night using TimeProvider for testable scheduling (FR-MEM-08).</summary>
public sealed partial class ConsolidationHostedService : BackgroundService
{
    private readonly ConsolidationJob _job;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ConsolidationHostedService(ConsolidationJob job, TimeProvider time, ILogger<ConsolidationHostedService>? logger = null)
    {
        _job = job;
        _time = time;
        _logger = logger ?? NullLogger<ConsolidationHostedService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _time.GetLocalNow();
            var next = now.Date.AddDays(1).AddHours(2);
            var delay = next - now;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.FromHours(24);
            }

            await Task.Delay(delay, _time, stoppingToken);
            try
            {
                var report = await _job.RunAsync(stoppingToken);
                LogCompleted(report.Total);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed nightly run must not take the host down; the next night retries.
                LogFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory consolidation staged {Count} proposals for review.")]
    private partial void LogCompleted(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Memory consolidation failed.")]
    private partial void LogFailed(Exception ex);
}
