using Capybara.Gateway.Configuration;
using Capybara.Observability.Cost;
using Capybara.Policy.Approvals;

namespace Capybara.Gateway.Wiring;

/// <summary>
/// Expires overdue approvals even when nobody looks at them, so the expiry alert fires and the waiting turn learns it
/// was denied (FR-SAF-04, FR-OBS-05).
/// </summary>
public sealed class ApprovalSweepService(ApprovalService approvals, TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                approvals.Sweep();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Polls the plan quota and alerts at 50/80/100% (FR-OBS-02); an unknown quota is alerted, not assumed fine.</summary>
public sealed class QuotaPollingService(QuotaMonitor monitor, QuotaHostOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await monitor.RunAsync(options.PollInterval, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
