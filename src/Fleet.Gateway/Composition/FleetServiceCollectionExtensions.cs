using Fleet.Gateway.Configuration;
using Fleet.Memory.Store;
using Fleet.Memory.Versioning;
using Fleet.Observability.Alerts;
using Fleet.Observability.Cost;
using Fleet.Orchestration.Configuration;
using Fleet.Orchestration.Coordination;
using Fleet.Orchestration.Envelopes;
using Fleet.Orchestration.Inbox;
using Fleet.Orchestration.Registry;
using Fleet.Orchestration.Tasks;
using Fleet.Orchestration.Thread;
using Fleet.Policy.Approvals;
using Fleet.Policy.Audit;
using Fleet.Policy.Gate;
using Fleet.Policy.Provenance;
using Fleet.Policy.Rules;
using Fleet.Policy.Safety;
using Fleet.Runtime.Copilot.Configuration;
using Fleet.Runtime.Copilot.DependencyInjection;
using Fleet.Runtime.Copilot.Supervision;

namespace Fleet.Gateway.Composition;

/// <summary>Composition root for the fleet. Config is loaded and validated eagerly: an invalid config never starts.</summary>
public static class FleetServiceCollectionExtensions
{
    public static IServiceCollection AddFleet(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var host = configuration.GetSection(FleetHostOptions.SectionName).Get<FleetHostOptions>() ?? new FleetHostOptions();
        var fleet = FleetConfigurationLoader.Load(host.ConfigRoot);

        services.AddSingleton(host);
        services.AddSingleton(fleet);
        services.AddOptions<GatewayOptions>().Bind(configuration.GetSection(GatewayOptions.SectionName));
        services.AddSingleton(TimeProvider.System);

        // Policy
        services.AddSingleton<KillSwitch>();
        services.AddSingleton(_ => new RulesEngine(fleet.Rules));
        services.AddSingleton<TaintTracker>();
        services.AddSingleton(configuration.GetSection("Fleet:Approvals").Get<ApprovalOptions>() ?? new ApprovalOptions());
        services.AddSingleton<ApprovalService>();
        services.AddSingleton<IAuditLog>(sp => new FileAuditLog(Path.Combine(host.StateRoot, "audit", "audit.jsonl"), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new PolicyGate(
            sp.GetRequiredService<RulesEngine>(),
            sp.GetRequiredService<TaintTracker>(),
            sp.GetRequiredService<ApprovalService>(),
            sp.GetRequiredService<KillSwitch>(),
            sp.GetRequiredService<IAuditLog>()));

        // Observability
        services.AddSingleton<InMemoryAlertSink>();
        services.AddSingleton<IAlertSink>(sp => sp.GetRequiredService<InMemoryAlertSink>());
        services.AddSingleton(sp =>
        {
            var ledger = new CostLedger(sp.GetRequiredService<IAlertSink>(), sp.GetRequiredService<TimeProvider>());
            foreach (var agent in fleet.Agents)
            {
                ledger.SetBudget(agent.Id, agent.Budget);
            }

            return ledger;
        });

        // Memory
        services.AddSingleton<IMemoryVersioning>(_ => new GitCliMemoryVersioning(Path.Combine(host.StateRoot, "memory")));
        services.AddSingleton(sp => new WikiStore(
            new MemoryOptions { Root = Path.Combine(host.StateRoot, "memory") },
            sp.GetRequiredService<IMemoryVersioning>(),
            sp.GetRequiredService<TimeProvider>()));

        // Orchestration
        services.AddSingleton(sp => new AgentRegistry(fleet.Agents, sp.GetRequiredService<KillSwitch>()));
        services.AddSingleton<EnvelopeFactory>();
        services.AddSingleton<IInboxStore>(sp => new FileInboxStore(Path.Combine(host.StateRoot, "inbox"), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ITaskStore>(_ => new FileTaskStore(Path.Combine(host.StateRoot, "tasks")));
        services.AddSingleton(_ => new FleetThread());
        services.AddSingleton(configuration.GetSection("Fleet:A2A").Get<A2AOptions>() ?? new A2AOptions());
        services.AddSingleton<A2ACoordinator>();

        // Runtime
        var runtime = configuration.GetSection(CopilotRuntimeOptions.SectionName).Get<CopilotRuntimeOptions>() ?? new CopilotRuntimeOptions();
        runtime.StateRoot = host.StateRoot;
        runtime.ConfigRoot = host.ConfigRoot;
        runtime.GitHubToken ??= ReadCredential(host.GitHubTokenCredential);
        var supervisor = configuration.GetSection(SupervisorOptions.SectionName).Get<SupervisorOptions>();
        services.AddFleetCopilotRuntime(runtime, supervisor);

        return services;
    }

    /// <summary>Reads a systemd credential (<c>LoadCredentialEncrypted=</c>) so the PAT never sits in config (FR-HST-03).</summary>
    private static string? ReadCredential(string name)
    {
        var directory = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        if (string.IsNullOrEmpty(directory) || name.IndexOfAny(['/', '\\']) >= 0)
        {
            return null;
        }

        var path = Path.Combine(directory, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
}
