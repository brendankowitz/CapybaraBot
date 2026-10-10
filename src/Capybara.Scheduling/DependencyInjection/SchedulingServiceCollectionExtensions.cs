using Capybara.Observability.Alerts;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.Durable;
using Capybara.Scheduling.History;
using Capybara.Scheduling.Hosting;
using Capybara.Scheduling.Lifecycle;
using Capybara.Scheduling.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;
using SchedulingOptions = Capybara.Scheduling.Dispatch.SchedulingOptions;
namespace Capybara.Scheduling.DependencyInjection;

/// <summary>Dependency injection entry point for Capybara.Scheduling (FR-RTN-05).</summary>
public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the durable scheduler, dispatcher, run history, routine registry, event router, auto-pause timer and
    /// the hosted services that run them. Everything is added with <c>TryAdd</c>, so a host can substitute any piece.
    /// <para>
    /// The composition root must register what this package consumes but does not own: <see cref="IAlertSink"/>,
    /// <see cref="Capybara.Observability.Cost.CostLedger"/>, <see cref="Capybara.Policy.Safety.KillSwitch"/> (shared with the rest
    /// of the gateway so one alert stream, one budget and one kill switch exist) and an
    /// <see cref="Capybara.Scheduling.Execution.IRoutineExecutor"/> (implemented by the runtime project). It may also register a
    /// <c>Func&lt;RoutineSpec, bool&gt;</c> input-source check for <see cref="RoutineDispatcher"/> and any number of
    /// <see cref="IEventTriggerSource"/>s.
    /// </para>
    /// </summary>
    public static IServiceCollection AddCapybaraScheduling(this IServiceCollection services, SchedulingOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new RoutineRunHistory(sp.GetRequiredService<IAlertSink>(), sp.GetRequiredService<SchedulingOptions>().SQLitePath));
        services.TryAddSingleton(sp => new RoutineRegistry(sp.GetRequiredService<SchedulingOptions>().TimeZone));
        services.TryAddSingleton(sp =>
        {
            var configured = sp.GetRequiredService<SchedulingOptions>();
            return new RoutineAutoPauser(
                sp.GetRequiredService<RoutineRegistry>(),
                sp.GetRequiredService<IAlertSink>(),
                sp.GetRequiredService<TimeProvider>(),
                configured.AutoPauseDays,
                configured.SQLitePath);
        });
        services.TryAddSingleton<QuartzSchedulerFactory>();
        // DI factories are synchronous; this only builds the scheduler. It is started by SchedulerHostedService.
        services.TryAddSingleton<IScheduler>(sp => sp.GetRequiredService<QuartzSchedulerFactory>().CreateSchedulerAsync().GetAwaiter().GetResult());
        services.TryAddSingleton<RoutineDispatcher>();
        services.TryAddSingleton<ScheduleSynchronizer>();
        services.TryAddSingleton<RoutineConfigLoader>();
        services.TryAddSingleton<EventRouter>();

        services.AddHostedService<SchedulerHostedService>();
        services.AddHostedService<AutoPauseHostedService>();
        services.AddHostedService<ScheduleRepairHostedService>();
        services.AddHostedService<EventSourceHostedService>();
        return services;
    }
}
