using Capybara.Contracts.Agents;
using Capybara.Observability.Alerts;
using Capybara.Orchestration.Registry;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.DependencyInjection;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Supervision;
using Capybara.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>FR-AGT-04 / FR-AGT-09: production composition wires the fail-closed supervision pieces.</summary>
public sealed class RuntimeCompositionTests
{
    [Fact]
    public async Task Production_registration_wires_self_test_and_reconciliation_without_degraded_warnings()
    {
        using var state = new TempDirectory();
        var time = TimeProvider.System;
        using var killSwitch = new KillSwitch(time);
        var log = new CriticalLog();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(RecordingLogger<>));
        services.AddSingleton(log);
        services.AddSingleton(time);
        services.AddSingleton(killSwitch);
        services.AddSingleton<IAlertSink, InMemoryAlertSink>();
        services.AddSingleton(new AgentRegistry(Array.Empty<AgentDefinition>(), killSwitch));
        services.AddCapybaraCopilotRuntime(new CopilotRuntimeOptions { StateRoot = state.Path, AutoStart = true });

        await using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<RuntimeSupervisor>());
        Assert.IsType<EventLogReconciler>(provider.GetRequiredService<ISessionReconciler>());
        var hosted = provider.GetServices<IHostedService>().OfType<AgentRuntimeHostedService>().Single();
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(log.Messages);
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
