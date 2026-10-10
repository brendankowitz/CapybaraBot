using Fleet.Observability.Alerts;
using Fleet.Observability.Cost;
using Fleet.Policy.Safety;
using Fleet.Scheduling.Durable;
using Fleet.Scheduling.History;
using Fleet.Scheduling.Hosting;
using Fleet.Scheduling.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Fleet.Scheduling.DependencyInjection;

/// <summary>Dependency injection entry point for Fleet.Scheduling (FR-RTN-05).</summary>
public static class SchedulingServiceCollectionExtensions
{
    public static IServiceCollection AddFleetScheduling(this IServiceCollection services, Fleet.Scheduling.Dispatch.SchedulingOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAlertSink, InMemoryAlertSink>();
        services.AddSingleton<CostLedger>();
        services.AddSingleton<RoutineRunHistory>();
        services.AddSingleton<RoutineRegistry>();
        services.AddSingleton<KillSwitch>();
        services.AddSingleton<QuartzSchedulerFactory>();
        services.AddSingleton<IScheduler>(sp => sp.GetRequiredService<QuartzSchedulerFactory>().CreateSchedulerAsync().GetAwaiter().GetResult());
        services.AddSingleton<ScheduleSynchronizer>();
        services.AddHostedService<SchedulerHostedService>();
        return services;
    }
}
