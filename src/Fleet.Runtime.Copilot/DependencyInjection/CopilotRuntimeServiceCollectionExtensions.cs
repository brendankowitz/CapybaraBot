using Fleet.Contracts.Agents;
using Fleet.Observability.Cost;
using Fleet.Orchestration.Coordination;
using Fleet.Orchestration.Envelopes;
using Fleet.Policy.Provenance;
using Fleet.Policy.Safety;
using Fleet.Runtime.Copilot.Configuration;
using Fleet.Runtime.Copilot.Hooks;
using Fleet.Runtime.Copilot.Hosting;
using Fleet.Runtime.Copilot.Sessions;
using Fleet.Runtime.Copilot.Supervision;
using Fleet.Runtime.Copilot.Tools;
using Fleet.Runtime.Copilot.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Fleet.Runtime.Copilot.DependencyInjection;

public static class CopilotRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-agent Copilot runtimes, their supervisor and the <see cref="IAgentTurnExecutor"/>.
    /// Expects the policy, orchestration, memory and observability services to be registered.
    /// </summary>
    public static IServiceCollection AddFleetCopilotRuntime(
        this IServiceCollection services,
        CopilotRuntimeOptions runtimeOptions,
        SupervisorOptions? supervisorOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runtimeOptions);

        services.AddSingleton(runtimeOptions);
        services.AddSingleton(supervisorOptions ?? new SupervisorOptions());
        services.TryAddSingleton<IUserInputBroker, UnattendedUserInputBroker>();
        services.AddSingleton<PermissionAllowances>();
        services.AddSingleton<SessionHandlers>();
        services.AddSingleton<UsageCapture>();
        services.AddSingleton(sp => new Lazy<A2ACoordinator>(sp.GetRequiredService<A2ACoordinator>));
        services.AddSingleton<ISessionToolProvider, FleetToolProvider>();
        services.AddSingleton<SessionConfigFactory>();

        services.AddSingleton(sp => new AgentRuntimePool(
            sp.GetRequiredService<Fleet.Orchestration.Registry.AgentRegistry>(),
            agent => CreateRuntime(sp, agent)));
        services.AddSingleton<IAgentTurnExecutor, CopilotTurnExecutor>();
        services.AddSingleton(sp => new RuntimeSupervisor(
            sp.GetRequiredService<AgentRuntimePool>().Supervised,
            sp.GetRequiredService<Fleet.Observability.Alerts.IAlertSink>(),
            sp.GetRequiredService<SupervisorOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RuntimeSupervisor>>()));

        if (runtimeOptions.AutoStart)
        {
            services.AddHostedService<AgentRuntimeHostedService>();
            services.AddHostedService<RuntimeSupervisorService>();
        }

        return services;
    }

    private static AgentRuntime CreateRuntime(IServiceProvider sp, AgentDefinition agent) => new(
        agent,
        sp.GetRequiredService<CopilotRuntimeOptions>(),
        sp.GetRequiredService<SessionConfigFactory>(),
        sp.GetRequiredService<EnvelopeFactory>(),
        sp.GetRequiredService<CostLedger>(),
        sp.GetRequiredService<KillSwitch>(),
        sp.GetRequiredService<TaintTracker>(),
        sp.GetRequiredService<PermissionAllowances>(),
        sp.GetRequiredService<ILogger<AgentRuntime>>());
}
