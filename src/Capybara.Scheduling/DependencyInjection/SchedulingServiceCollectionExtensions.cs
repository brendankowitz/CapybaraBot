using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Policy.Safety;
using Capybara.Scheduling.Durable;
using Capybara.Scheduling.History;
using Capybara.Scheduling.Hosting;
using Capybara.Scheduling.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Capybara.Scheduling.DependencyInjection;

/// <summary>Dependency injection entry point for Capybara.Scheduling (FR-RTN-05).</summary>
public static class SchedulingServiceCollectionExtensions
{
    public static IServiceCollection AddCapybaraScheduling(this IServiceCollection services, Capybara.Scheduling.Dispatch.SchedulingOptions options)
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
