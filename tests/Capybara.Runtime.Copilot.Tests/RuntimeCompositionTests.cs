using Capybara.Channels;
using Capybara.Contracts.Agents;
using Capybara.Memory.Abstractions;
using Capybara.Memory.Store;
using Capybara.Memory.Versioning;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Envelopes;
using Capybara.Orchestration.Inbox;
using Capybara.Orchestration.Registry;
using Capybara.Orchestration.Tasks;
using Capybara.Orchestration.Thread;
using Capybara.Policy.Approvals;
using Capybara.Policy.Audit;
using Capybara.Policy.Gate;
using Capybara.Policy.Provenance;
using Capybara.Policy.Review;
using Capybara.Policy.Rules;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.DependencyInjection;
using Capybara.Runtime.Copilot.Hooks;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Inbox;
using Capybara.Runtime.Copilot.Review;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Runtime.Copilot.Supervision;
using Capybara.Scheduling.Execution;
using Capybara.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>Production composition wires the fail-closed supervision pieces and every WP9 execution service.</summary>
public sealed class RuntimeCompositionTests
{
    [Fact]
    public async Task Production_registration_wires_self_test_and_reconciliation_without_degraded_warnings()
    {
        using var state = new TempDirectory();
        using var killSwitch = new KillSwitch(TimeProvider.System);
        var log = new CriticalLog();
        await using var provider = Compose(state.Path, killSwitch, log);

        Assert.NotNull(provider.GetRequiredService<RuntimeSupervisor>());
        Assert.IsType<EventLogReconciler>(provider.GetRequiredService<ISessionReconciler>());
        var hosted = provider.GetServices<IHostedService>().OfType<AgentRuntimeHostedService>().Single();
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(log.Messages);
    }

    [Fact]
    public async Task Execution_services_are_registered_and_the_reviewer_does_not_form_a_cycle_with_the_gate()
    {
        using var state = new TempDirectory();
        using var killSwitch = new KillSwitch(TimeProvider.System);
        await using var provider = Compose(state.Path, killSwitch, new CriticalLog());

        Assert.IsType<CopilotToolCallReviewer>(provider.GetRequiredService<IToolCallReviewer>());
        Assert.IsType<CopilotRoutineExecutor>(provider.GetRequiredService<IRoutineExecutor>());
        Assert.IsType<InboundMessageSink>(provider.GetRequiredService<IInboundMessageSink>());
        Assert.IsType<EscalatingUserInputBroker>(provider.GetRequiredService<IUserInputBroker>());
        Assert.IsType<CompositeMemoryStore>(provider.GetRequiredService<IMemoryStore>());

        // Gate → reviewer → (lazy) session factory → handlers → gate.
        Assert.NotNull(provider.GetRequiredService<PolicyGate>());
        Assert.NotNull(provider.GetRequiredService<Lazy<SessionConfigFactory>>().Value);

        var hosted = provider.GetServices<IHostedService>().ToList();
        var runtimes = hosted.FindIndex(h => h is AgentRuntimeHostedService);
        Assert.True(runtimes < hosted.FindIndex(h => h is ApprovalResumer));
        Assert.True(runtimes < hosted.FindIndex(h => h is InboxPumpHostedService));
    }

    private static ServiceProvider Compose(string root, KillSwitch killSwitch, CriticalLog log)
    {
        var time = TimeProvider.System;
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(RecordingLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(log);
        services.AddSingleton(time);
        services.AddSingleton(killSwitch);
        services.AddSingleton<IAlertSink, InMemoryAlertSink>();
        services.AddSingleton(sp => new CostLedger(sp.GetRequiredService<IAlertSink>(), time));
        services.AddSingleton(new AgentRegistry(Array.Empty<AgentDefinition>(), killSwitch));
        services.AddSingleton(new RulesEngine([]));
        services.AddSingleton<TaintTracker>();
        services.AddSingleton<IAuditLog, InMemoryAuditLog>();
        services.AddSingleton(sp => new ApprovalService(sp.GetRequiredService<RulesEngine>(), new ApprovalOptions(), time));
        services.AddSingleton(sp => new PolicyGate(
            sp.GetRequiredService<RulesEngine>(),
            sp.GetRequiredService<TaintTracker>(),
            sp.GetRequiredService<ApprovalService>(),
            killSwitch,
            sp.GetRequiredService<IAuditLog>(),
            sp.GetService<IToolCallReviewer>()));
        services.AddSingleton(new WikiStore(new MemoryOptions { Root = Path.Combine(root, "memory") }, new NoMemoryVersioning(), time));
        services.AddSingleton<EnvelopeFactory>();
        services.AddSingleton<IInboxStore>(new FileInboxStore(Path.Combine(root, "inbox"), time));
        services.AddSingleton<ITaskStore>(new FileTaskStore(Path.Combine(root, "tasks")));
        services.AddSingleton(new FleetThread());
        services.AddSingleton(new A2AOptions());
        services.AddSingleton<A2ACoordinator>();
        services.AddCapybaraCopilotRuntime(new CopilotRuntimeOptions { StateRoot = root, AutoStart = true });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private sealed class CriticalLog
    {
        public List<string> Messages { get; } = [];
    }

    private sealed class RecordingLogger<T>(CriticalLog log) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Critical)
            {
                lock (log)
                {
                    log.Messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
