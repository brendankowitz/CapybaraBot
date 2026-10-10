using Microsoft.Extensions.Hosting;

namespace Capybara.Memory.Consolidation;

/// <summary>Runs consolidation once per night using TimeProvider for testable scheduling (FR-MEM-08).</summary>
public sealed class ConsolidationHostedService(ConsolidationJob job, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetLocalNow();
            var next = now.Date.AddDays(1).AddHours(2);
            var delay = next - now;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.FromHours(24);
            }

            await Task.Delay(delay, time, stoppingToken);
            await job.RunAsync(stoppingToken);
        }
    }
}
