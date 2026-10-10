using Microsoft.Extensions.Hosting;

namespace Fleet.Runtime.Copilot.Supervision;

/// <summary>Drives <see cref="RuntimeSupervisor"/> on a fixed interval.</summary>
public sealed class RuntimeSupervisorService(RuntimeSupervisor supervisor, SupervisorOptions options, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.CheckInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await supervisor.CheckOnceAsync(stoppingToken);
        }
    }
}
