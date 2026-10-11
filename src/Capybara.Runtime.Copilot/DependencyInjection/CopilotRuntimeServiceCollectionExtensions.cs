using Capybara.Channels;
using Capybara.Contracts.Agents;
using Capybara.Memory.Abstractions;
using Capybara.Memory.Store;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Envelopes;
using Capybara.Policy.Provenance;
using Capybara.Policy.Review;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hooks;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Inbox;
using Capybara.Runtime.Copilot.Review;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Runtime.Copilot.Supervision;
using Capybara.Runtime.Copilot.Tools;
using Capybara.Runtime.Copilot.Usage;
using Capybara.Scheduling.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.DependencyInjection;

public static class CopilotRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-agent Copilot runtimes and everything that drives them: the supervisor (with the fail-closed
    /// self-test guard and turn reconciler), the inbox pumps, approval resumption, <c>ask_user</c> escalation, the
    /// routine executor (<see cref="IRoutineExecutor"/>), the inbound channel sink (<see cref="IInboundMessageSink"/>),
    /// the tool-call reviewer (<see cref="IToolCallReviewer"/>) and the <see cref="IAgentTurnExecutor"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Expects the policy, orchestration, observability and memory services (<see cref="WikiStore"/> at least) to be
    /// registered. Optional collaborators are used when present: <c>IBrokerToolSource</c> (broker tools),
    /// <see cref="IUserEscalation"/> (phone channel), <c>ProposalService</c> (<c>propose_change</c>), <c>ISecretStore</c>,
    /// <see cref="IAgentCliLauncher"/> (per-agent sandbox) and <see cref="IMemoryProvider"/>s. <see cref="InboxPumpOptions"/>
    /// can be registered first to override defaults. Turn, compaction and egress-denial signals are raised on
    /// <see cref="RuntimeActivity"/>.
    /// </para>
    /// <para>
    /// The host must pass <see cref="IToolCallReviewer"/> to its <c>PolicyGate</c>; the reviewer resolves the session
    /// factory lazily, so that does not form a cycle.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCapybaraCopilotRuntime(
        this IServiceCollection services,
        CopilotRuntimeOptions runtimeOptions,
        SupervisorOptions? supervisorOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runtimeOptions);

        services.AddSingleton(runtimeOptions);
        services.AddSingleton(supervisorOptions ?? new SupervisorOptions());
        services.TryAddSingleton(new InboxPumpOptions());

        // FR-A2A-11: one fleet-wide slot pool, shared with the coordinator (which takes it as an optional dependency).
        services.TryAddSingleton(sp => new SessionLimiter(sp.GetRequiredService<A2AOptions>()));

        // FR-A2A-11: per-pair exchange counts survive restarts; the coordinator takes the ledger as an optional dependency.
        services.TryAddSingleton(sp => new ExchangeLedger(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<A2AOptions>().ExchangeRetention,
            Path.Combine(runtimeOptions.StateRoot, "a2a", "exchanges.json")));

        // Memory goes through IMemoryStore (FR-MEM-12); composed over the wiki unless the host registered its own.
        services.TryAddSingleton(sp => new CompositeMemoryStore(
            sp.GetRequiredService<WikiStore>(),
            sp.GetServices<IMemoryProvider>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IMemoryStore>(sp => sp.GetRequiredService<CompositeMemoryStore>());

        // Sessions, hooks and tools.
        services.TryAddSingleton<RuntimeActivity>();
        services.TryAddSingleton<IUserInputBroker, EscalatingUserInputBroker>();
        services.AddSingleton<PermissionAllowances>();
        services.AddSingleton<ApprovalTurnStore>();
        services.AddSingleton<SessionHandlers>();
        services.AddSingleton<UsageCapture>();
        services.AddSingleton(sp => new Lazy<A2ACoordinator>(sp.GetRequiredService<A2ACoordinator>));
        services.AddSingleton<ISessionToolProvider, CapybaraToolProvider>();
        services.AddSingleton<SessionConfigFactory>();
        services.AddSingleton(sp => new Lazy<SessionConfigFactory>(sp.GetRequiredService<SessionConfigFactory>));
        services.AddSingleton<TurnMemory>();

        // Reviewer (FR-SAF-05).
        services.TryAddSingleton<IReviewerModel, CopilotReviewerModel>();
        services.TryAddSingleton<IToolCallReviewer, CopilotToolCallReviewer>();

        // Runtimes.
        services.AddSingleton(sp => new AgentRuntimePool(
            sp.GetRequiredService<Capybara.Orchestration.Registry.AgentRegistry>(),
            agent => CreateRuntime(sp, agent)));
        services.AddSingleton<IAgentTurnExecutor, CopilotTurnExecutor>();

        // Execution: routines, channel DMs, approvals.
        services.AddSingleton<CopilotRoutineExecutor>();
        services.AddSingleton<IRoutineExecutor>(sp => sp.GetRequiredService<CopilotRoutineExecutor>());
        services.TryAddSingleton<InboundMessageSink>();
        services.TryAddSingleton<IInboundMessageSink>(sp => sp.GetRequiredService<InboundMessageSink>());
        services.AddSingleton<ApprovalResumer>();

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
        services.AddSingleton<InboxPumpHostedService>();

        if (runtimeOptions.AutoStart)
        {
            // Order matters: runtimes start (and self-test) before anything feeds them turns.
            services.AddHostedService<AgentRuntimeHostedService>();
            services.AddHostedService<RuntimeSupervisorService>();
            services.AddHostedService(sp => sp.GetRequiredService<ApprovalResumer>());
            services.AddHostedService(sp => sp.GetRequiredService<InboxPumpHostedService>());
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
        sp.GetRequiredService<Capybara.Observability.Alerts.IAlertSink>(),
        sp.GetRequiredService<SessionLimiter>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<AgentRuntime>>(),
        sp.GetRequiredService<RuntimeActivity>(),
        sp.GetService<IAgentCliLauncher>());
}
