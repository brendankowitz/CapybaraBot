using Capybara.Contracts.Agents;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Envelopes;
using Capybara.Policy.Provenance;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hooks;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Runtime.Copilot.Supervision;
using Capybara.Runtime.Copilot.Tools;
using Capybara.Runtime.Copilot.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.DependencyInjection;

public static class CopilotRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-agent Copilot runtimes, their supervisor and the <see cref="IAgentTurnExecutor"/>.
    /// Expects the policy, orchestration, memory and observability services to be registered.
    /// </summary>
    public static IServiceCollection AddCapybaraCopilotRuntime(
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
        services.AddSingleton<ISessionToolProvider, CapybaraToolProvider>();
        services.AddSingleton<SessionConfigFactory>();
        services.AddSingleton<TurnMemory>();

        services.AddSingleton(sp => new AgentRuntimePool(
            sp.GetRequiredService<Capybara.Orchestration.Registry.AgentRegistry>(),
            agent => CreateRuntime(sp, agent)));
        services.AddSingleton<IAgentTurnExecutor, CopilotTurnExecutor>();

        // Fail-closed supervision (FR-AGT-04, FR-AGT-09): the self-test reruns and the interrupted turn is reconciled
        // after every start and restart. RuntimeSupervisor logs Critical if either is ever missing.
        services.TryAddSingleton<SelfTestGuard>();
        services.TryAddSingleton<ISessionReconciler, EventLogReconciler>();
        services.AddSingleton(sp => new RuntimeSupervisor(
            sp.GetRequiredService<AgentRuntimePool>().Supervised,
            sp.GetRequiredService<Capybara.Observability.Alerts.IAlertSink>(),
            sp.GetRequiredService<SupervisorOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RuntimeSupervisor>>(),
            sp.GetRequiredService<SelfTestGuard>(),
            sp.GetRequiredService<ISessionReconciler>()));

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
        sp.GetRequiredService<TurnMemory>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<AgentRuntime>>());
}
