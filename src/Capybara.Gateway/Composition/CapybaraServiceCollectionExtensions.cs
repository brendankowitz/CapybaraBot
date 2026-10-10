using Capybara.Channels;
using Capybara.Channels.DependencyInjection;
using Capybara.Channels.Ntfy;
using Capybara.Channels.Telegram;
using Capybara.Channels.WebPush;
using Capybara.Connections.DependencyInjection;
using Capybara.Connections.Health;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Storage;
using Capybara.Gateway.Configuration;
using Capybara.Gateway.Realtime;
using Capybara.Gateway.Wiring;
using Capybara.Hosting.Agents;
using Capybara.Hosting.DependencyInjection;
using Capybara.Hosting.Sandboxing;
using Capybara.Hosting.Storage;
using Capybara.Memory.Consolidation;
using Capybara.Memory.DependencyInjection;
using Capybara.Memory.Search;
using Capybara.Memory.Store;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Configuration;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Envelopes;
using Capybara.Orchestration.Inbox;
using Capybara.Orchestration.Lifecycle;
using Capybara.Orchestration.Proposals;
using Capybara.Orchestration.Registry;
using Capybara.Orchestration.Tasks;
using Capybara.Orchestration.Thread;
using Capybara.Orchestration.Versioning;
using Capybara.Policy.Approvals;
using Capybara.Policy.Audit;
using Capybara.Policy.Gate;
using Capybara.Policy.Provenance;
using Capybara.Policy.Redaction;
using Capybara.Policy.Review;
using Capybara.Policy.Rules;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.DependencyInjection;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Supervision;
using Capybara.Runtime.Copilot.Usage;
using Capybara.Scheduling.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SchedulingOptions = Capybara.Scheduling.Dispatch.SchedulingOptions;

namespace Capybara.Gateway.Composition;

/// <summary>
/// Composition root for capybarad. Config is loaded and validated eagerly: an invalid config, or a state root the
/// hosting and runtime layers disagree on, never starts (fail closed). Every service is registered once.
/// </summary>
public static class CapybaraServiceCollectionExtensions
{
    /// <summary>The full gateway: every agent runtime plus channels, scheduling, realtime bridges and the kill switch.</summary>
    public static IServiceCollection AddCapybara(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var core = AddCore(services, configuration, soleAgent: null);
        AddGateway(services, configuration, core);
        return services;
    }

    /// <summary>
    /// <c>capybarad agent &lt;id&gt;</c> (Phase 2 per-agent unit): only that agent's runtime and its supervisor. No
    /// channels, scheduler, inbox pumps or approval resumption: the gateway still owns those, and without the
    /// gateway↔agent RPC (not implemented) this process receives no turns. It keeps the CLI started, self-tested and
    /// supervised under its own unit.
    /// </summary>
    public static IServiceCollection AddCapybaraAgentHost(this IServiceCollection services, IConfiguration configuration, AgentId agent)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var core = AddCore(services, configuration, agent);
        services.AddSingleton<IAlertSink>(sp => sp.GetRequiredService<FileAlertSink>());

        // The gateway owns these; running them here too would duplicate index rebuilds, consolidation and token alerts.
        RemoveHostedService<MemoryIndexHostedService>(services);
        RemoveHostedService<ConsolidationHostedService>(services);
        RemoveHostedService<TokenHealthBackgroundService>(services);

        if (core.Runtime.AutoStartRequested)
        {
            services.AddHostedService<AgentRuntimeHostedService>();
            services.AddHostedService<RuntimeSupervisorService>();
        }

        return services;
    }

    private static CoreSettings AddCore(IServiceCollection services, IConfiguration configuration, AgentId? soleAgent)
    {
        var host = configuration.GetSection(CapybaraHostOptions.SectionName).Get<CapybaraHostOptions>() ?? new CapybaraHostOptions();
        var hosting = HostingOptions.FromConfiguration(configuration);
        var runtime = configuration.GetSection(CopilotRuntimeOptions.SectionName).Get<CopilotRuntimeOptions>() ?? new CopilotRuntimeOptions();
        var telemetry = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();
        RequireOneStateRoot(host, hosting, configuration);

        var state = new StateLayout(host.StateRoot);
        var fleet = CapybaraConfigurationLoader.Load(host.ConfigRoot);
        if (soleAgent is { } only && fleet.Agents.All(a => a.Id != only))
        {
            throw new CapybaraConfigurationException([$"capybarad agent: no agent '{only}' in {host.ConfigRoot}."]);
        }

        // One state and config root for every layer; the CLI exports to the same collector as the host (FR-OBS-01).
        runtime.StateRoot = host.StateRoot;
        runtime.ConfigRoot = host.ConfigRoot;
        runtime.GitHubToken ??= SystemdCredentials.Read(host.GitHubTokenCredential);
        var warnings = new CompositionWarnings();
        if (runtime.OtlpEndpoint is null && telemetry.OtlpEndpoint is { } otlp)
        {
            // The CLI only exports OTLP over HTTP; a gRPC collector port would silently drop its spans.
            if (string.Equals(telemetry.OtlpProtocol, "http/protobuf", StringComparison.OrdinalIgnoreCase))
            {
                runtime.OtlpEndpoint = otlp.ToString();
            }
            else
            {
                warnings.Add($"{TelemetryOptions.SectionName}:OtlpProtocol is '{telemetry.OtlpProtocol}', which the Copilot CLI cannot export to; agent CLI spans are off. Set {CopilotRuntimeOptions.SectionName}:OtlpEndpoint to an OTLP/HTTP endpoint to enable them.");
            }
        }

        var autoStart = runtime.AutoStart;
        if (soleAgent is not null)
        {
            // The agent host adds only the runtime and supervisor services itself (no pumps, no approval resumer).
            runtime.AutoStart = false;
        }

        services.AddSingleton(host);
        services.AddSingleton(fleet);
        services.AddSingleton(state);
        services.AddSingleton(warnings);
        services.AddOptions<GatewayOptions>().Bind(configuration.GetSection(GatewayOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        // Hosting: stores, host registry and (gateway only, unless the timer owns it) the nightly backup (FR-HST-05).
        var backup = configuration.GetSection(BackupHostOptions.SectionName).Get<BackupHostOptions>() ?? new BackupHostOptions();
        services.AddCapybaraHosting(hosting, soleAgent is null && backup.InProcess ? configuration : null);

        // Secrets the sessions resolve (BYOK keys, http MCP tokens) come from systemd credentials (FR-AGT-10).
        services.AddSingleton<ISecretStore>(sp => new SystemdCredsSecretStore(Path.Combine(host.ConfigRoot, "credentials"), sp.GetRequiredService<ICommandRunner>()));

        AddPolicy(services, fleet, state, configuration, host);
        AddObservability(services, fleet, state);
        services.AddCapybaraMemory(new MemoryOptions { Root = state.Memory });
        AddOrchestration(services, fleet, state, host, configuration, soleAgent);
        AddBroker(services, configuration);
        if (soleAgent is null)
        {
            // Hosted services start in registration order. These subscribe to the kill switch and to approval, thread
            // and turn events, so they must be listening before the runtimes and inbox pumps start producing them.
            services.AddSingleton<KillSwitchCoordinator>();
            services.AddHostedService(sp => sp.GetRequiredService<KillSwitchCoordinator>());
            services.AddHostedService<EventBridge>();
        }

        AddRuntime(services, configuration, runtime, hosting, host, state);

        return new CoreSettings(host, state, fleet, new RuntimeSettings(autoStart));
    }

    private static void AddPolicy(IServiceCollection services, CapybaraConfiguration fleet, StateLayout state, IConfiguration configuration, CapybaraHostOptions host)
    {
        // A restart is never a release (FR-SAF-06): a journaled engagement is re-applied before anything can run a turn.
        services.AddSingleton(new KillSwitchJournal(state.KillSwitch));
        services.AddSingleton(sp =>
        {
            var killSwitch = new KillSwitch(sp.GetRequiredService<TimeProvider>());
            if (sp.GetRequiredService<KillSwitchJournal>().Load() is { } reason)
            {
                killSwitch.Engage(reason);
            }

            return killSwitch;
        });
        services.AddSingleton<TaintTracker>();
        services.AddSingleton(new LearnedRuleStore(state.LearnedRules));

        // Learned "Always allow" rules survive restarts and are saved as they are learned (FR-SAF-04).
        services.AddSingleton(sp => sp.GetRequiredService<LearnedRuleStore>().CreateEngine(fleet.Rules));
        services.AddSingleton<IAuditLog>(sp => new FileAuditLog(state.Audit, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(configuration.GetSection("Capybara:Approvals").Get<ApprovalOptions>() ?? new ApprovalOptions());
        services.AddSingleton<IApprovalStore>(_ => new FileApprovalStore(state.Approvals));
        services.AddSingleton(sp => new ApprovalService(
            sp.GetRequiredService<RulesEngine>(),
            sp.GetRequiredService<ApprovalOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IApprovalStore>(),
            sp.GetRequiredService<IAuditLog>()));

        // The reviewer (FR-SAF-05) is registered by the runtime and resolves its session factory lazily, so no cycle.
        services.AddSingleton(sp => new PolicyGate(
            sp.GetRequiredService<RulesEngine>(),
            sp.GetRequiredService<TaintTracker>(),
            sp.GetRequiredService<ApprovalService>(),
            sp.GetRequiredService<KillSwitch>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetService<IToolCallReviewer>()));

        // Every credential capybarad holds is scrubbed from tool output by literal value, not only by shape (FR-SAF-08).
        var broker = configuration.GetSection(BrokerHostOptions.SectionName).Get<BrokerHostOptions>() ?? new BrokerHostOptions();
        var channels = configuration.GetSection(ChannelHostOptions.SectionName).Get<ChannelHostOptions>() ?? new ChannelHostOptions();
        services.AddSingleton(_ => new SecretRedactor(new[]
        {
            SystemdCredentials.Read(host.GitHubTokenCredential),
            SystemdCredentials.Read(broker.ApiKeyCredential),
            SystemdCredentials.Read(channels.Telegram.BotTokenCredential),
            SystemdCredentials.Read(channels.Ntfy.AccessTokenCredential),
        }.OfType<string>().Where(s => s.Length > 0)));
    }

    private static void AddObservability(IServiceCollection services, CapybaraConfiguration fleet, StateLayout state)
    {
        // Durable alert history (FR-OBS-05); the IAlertSink everyone publishes to decorates it per host mode.
        services.AddSingleton(_ => new FileAlertSink(state.Alerts));
        services.AddSingleton<ICostLedgerStore>(_ => new FileCostLedgerStore(state.CostLedger));
        services.AddSingleton(sp =>
        {
            var ledger = new CostLedger(sp.GetRequiredService<IAlertSink>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ICostLedgerStore>());
            foreach (var agent in fleet.Agents)
            {
                ledger.SetBudget(agent.Id, agent.Budget);
            }

            return ledger;
        });
        services.AddSingleton(sp => new EgressSpikeDetector(sp.GetRequiredService<IAlertSink>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new CompactionStormDetector(sp.GetRequiredService<IAlertSink>(), sp.GetRequiredService<TimeProvider>()));
    }

    private static void AddOrchestration(IServiceCollection services, CapybaraConfiguration fleet, StateLayout state, CapybaraHostOptions host, IConfiguration configuration, AgentId? soleAgent)
    {
        var agents = soleAgent is { } only ? fleet.Agents.Where(a => a.Id == only).ToList() : fleet.Agents;
        services.AddSingleton(sp => new AgentRegistry(agents, sp.GetRequiredService<KillSwitch>()));
        services.AddSingleton<EnvelopeFactory>();
        services.AddSingleton<IInboxStore>(sp => new FileInboxStore(state.Inbox, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ITaskStore>(_ => new FileTaskStore(state.Tasks));
        services.AddSingleton(_ => new FleetThread());
        services.AddSingleton(configuration.GetSection("Capybara:A2A").Get<A2AOptions>() ?? new A2AOptions());
        services.AddSingleton<A2ACoordinator>();

        // Human config changes are validated, applied, reloaded and git-committed in one place (FR-AGT-06, FR-AGT-07).
        services.AddSingleton<IConfigVersioning>(_ => new GitCliConfigVersioning(host.ConfigRoot));
        services.AddSingleton(sp => new CapybaraConfigurationHost(
            host.ConfigRoot,
            fleet,
            sp.GetRequiredService<AgentRegistry>(),
            sp.GetRequiredService<RulesEngine>(),
            sp.GetRequiredService<IConfigVersioning>(),
            state.ConfigScratch));
        services.AddSingleton<IProposalStore>(sp => new FileProposalStore(state.Proposals, sp.GetService<ILogger<FileProposalStore>>()));
        services.AddSingleton(sp => new ProposalService(
            sp.GetRequiredService<IProposalStore>(),
            sp.GetRequiredService<CapybaraConfigurationHost>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<ProposalService>>()));
    }

    private static void AddBroker(IServiceCollection services, IConfiguration configuration)
    {
        // capybarad is only ever the broker's client; the broker (with the vault) is a separate process (FR-CON-01).
        var broker = configuration.GetSection(BrokerHostOptions.SectionName).Get<BrokerHostOptions>() ?? new BrokerHostOptions();
        services.AddCapybaraBrokerClient(new BrokerClientOptions
        {
            BaseAddress = broker.BaseAddress,
            UnixSocketPath = broker.UnixSocketPath,
            ApiKeyFile = SystemdCredentials.PathOf(broker.ApiKeyCredential),
            TokenExpiryLeadTime = broker.TokenExpiryLeadTime,
        });
    }

    private static void AddRuntime(IServiceCollection services, IConfiguration configuration, CopilotRuntimeOptions runtime, HostingOptions hosting, CapybaraHostOptions host, StateLayout state)
    {
        // Per-agent srt sandbox when configured (FR-AGT-07); the planner refuses agents its read denials do not isolate.
        var sandbox = configuration.GetSection(SandboxOptions.SectionName).Get<SandboxOptions>();
        if (sandbox?.SrtPath is { Length: > 0 } srt)
        {
            if (string.IsNullOrWhiteSpace(sandbox.CopilotCliPath))
            {
                throw new CapybaraConfigurationException([$"{SandboxOptions.SectionName}:CopilotCliPath is required when SrtPath is set."]);
            }

            var options = AgentSandboxOptions.FromHosting(hosting, srt, sandbox.CopilotCliPath, sandbox.SettingsDirectory ?? state.SandboxSettings, host.ConfigRoot);
            services.AddSingleton(new AgentSandboxPlanner(options));
            services.AddSingleton<IAgentCliLauncher, SandboxCliLauncher>();
        }

        var supervisor = configuration.GetSection(SupervisorOptions.SectionName).Get<SupervisorOptions>();
        services.AddCapybaraCopilotRuntime(runtime, supervisor);
    }

    private static void AddGateway(IServiceCollection services, IConfiguration configuration, CoreSettings core)
    {
        AddChannels(services, configuration, core);

        // Order of the hosted services below is start order: config sync loads routines before the scheduler starts.
        services.AddSingleton<ConfigurationSync>();
        services.AddHostedService(sp => sp.GetRequiredService<ConfigurationSync>());
        AddScheduling(services, configuration, core.State);

        services.AddHostedService<ApprovalSweepService>();

        // Account quota (FR-OBS-02): read through a running agent CLI, so only when runtimes start.
        services.AddSingleton<IQuotaSource, CopilotQuotaSource>();
        services.AddSingleton(sp => new QuotaMonitor(sp.GetRequiredService<IQuotaSource>(), sp.GetRequiredService<IAlertSink>(), sp.GetRequiredService<TimeProvider>()));
        if (core.Runtime.AutoStartRequested)
        {
            services.AddSingleton(configuration.GetSection(QuotaHostOptions.SectionName).Get<QuotaHostOptions>() ?? new QuotaHostOptions());
            services.AddHostedService<QuotaPollingService>();
        }

        // FR-AGT-07: awaited in this order when an agent is archived or deleted; all run before its state is removed.
        services.AddSingleton<IAgentLifecycleObserver, RevokeBrokerGrantsObserver>();
        services.AddSingleton<IAgentLifecycleObserver, UnscheduleRoutinesObserver>();
        services.AddSingleton<IAgentLifecycleObserver, StopRuntimeObserver>();
        services.AddSingleton<IAgentLifecycleObserver, ClearAgentMemoryObserver>();
        services.AddSingleton<IAgentLifecycleObserver, RemoveLearnedRulesObserver>();
        services.AddSingleton<IAgentLifecycleObserver>(sp => sp.GetRequiredService<ProposalService>());
        services.AddSingleton(sp => new AgentLifecycleService(
            sp.GetRequiredService<CapybaraConfigurationHost>(),
            core.Host.StateRoot,
            sp.GetServices<IAgentLifecycleObserver>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<TimeProvider>()));
    }

    private static void AddChannels(IServiceCollection services, IConfiguration configuration, CoreSettings core)
    {
        var settings = configuration.GetSection(ChannelHostOptions.SectionName).Get<ChannelHostOptions>() ?? new ChannelHostOptions();
        var options = ChannelOptions.FromAgents(core.Fleet.Agents) with
        {
            QuietHours = new QuietHoursOptions { Enabled = settings.QuietHours.Enabled, StartsAt = settings.QuietHours.StartsAt, EndsAt = settings.QuietHours.EndsAt },
        };

        // Alerts: durable first, then live in the clients, then (Warning and up) to the phone. The router reports its
        // own delivery failures to the non-notifying sink, so a dead channel cannot loop.
        services.AddSingleton(sp => new RealtimeAlertSink(sp.GetRequiredService<FileAlertSink>(), sp.GetRequiredService<ICapybaraEvents>(), sp.GetRequiredService<ILogger<RealtimeAlertSink>>()));
        services.AddSingleton(sp => new NotificationRouter(
            sp.GetServices<INotificationChannel>(),
            sp.GetRequiredService<IPresenceTracker>(),
            sp.GetRequiredService<RealtimeAlertSink>(),
            sp.GetRequiredService<ChannelOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<NotificationRouter>>()));
        services.AddSingleton<IAlertSink>(sp => new NotifyingAlertSink(sp.GetRequiredService<RealtimeAlertSink>(), sp.GetRequiredService<NotificationRouter>()));
        services.AddCapybaraChannels(options, core.State.WebPushSubscriptions);

        // Preference order: self-hosted ntfy, then PWA Web Push, then Telegram (which never carries sensitive content).
        if (settings.Ntfy is { BaseUrl: { } ntfyUrl, Topic: { Length: > 0 } topic })
        {
            services.AddCapybaraNtfyChannel(new NtfyOptions(ntfyUrl, topic)
            {
                AccessToken = SystemdCredentials.Read(settings.Ntfy.AccessTokenCredential),
                SupportsSensitiveContent = settings.Ntfy.SupportsSensitiveContent,
                GatewayBaseUrl = settings.Ntfy.GatewayBaseUrl,
            });
        }

        if (settings.WebPush.PublicKey is { Length: > 0 } publicKey && SystemdCredentials.Read(settings.WebPush.PrivateKeyCredential) is { } privateKey)
        {
            var vapid = new WebPushVapidOptions(settings.WebPush.Subject, publicKey, privateKey);
            // Push endpoint URLs are capability paths: never log them.
            services.AddHttpClient("capybara-webpush").RemoveAllLoggers();
            services.AddSingleton<INotificationChannel>(sp => new WebPushChannel(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("capybara-webpush"),
                sp.GetRequiredService<WebPushSubscriptionStore>(),
                vapid,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILogger<WebPushChannel>>()));
        }

        if (settings.Telegram.ChatId is { Length: > 0 } chatId && SystemdCredentials.Read(settings.Telegram.BotTokenCredential) is { } botToken)
        {
            services.AddCapybaraTelegramChannel(new TelegramOptions(botToken, chatId)
            {
                AllowedUserId = settings.Telegram.AllowedUserId,
                DeepLinkBaseUrl = settings.Telegram.DeepLinkBaseUrl,
            });
        }

        BoundChannelSends(services);
    }

    /// <summary>
    /// Wraps every channel registered so far in <see cref="TimeBoundedNotificationChannel"/>, so a hung endpoint falls
    /// back to the next channel within 10 s instead of holding an approval push (NFR-04).
    /// </summary>
    private static void BoundChannelSends(IServiceCollection services)
    {
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(INotificationChannel) || descriptor.IsKeyedService)
            {
                continue;
            }

            Func<IServiceProvider, object> create = descriptor.ImplementationFactory
                ?? (descriptor.ImplementationInstance is { } instance
                    ? _ => instance
                    : sp => ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));
            services[i] = ServiceDescriptor.Singleton<INotificationChannel>(sp => new TimeBoundedNotificationChannel(
                (INotificationChannel)create(sp),
                TimeBoundedNotificationChannel.DefaultTimeout,
                sp.GetRequiredService<TimeProvider>()));
        }
    }

    private static void AddScheduling(IServiceCollection services, IConfiguration configuration, StateLayout state)
    {
        var settings = configuration.GetSection(SchedulingHostOptions.SectionName).Get<SchedulingHostOptions>() ?? new SchedulingHostOptions();
        Directory.CreateDirectory(Path.GetDirectoryName(state.SchedulingDatabase)!);
        services.AddCapybaraScheduling(new SchedulingOptions
        {
            SQLitePath = state.SchedulingDatabase,
            ConcurrencyCap = settings.ConcurrencyCap,
            AutoPauseDays = settings.AutoPauseDays,
            CatchUpPolicy = Enum.Parse<Capybara.Scheduling.Dispatch.CatchUpPolicy>(settings.CatchUpPolicy, ignoreCase: true),
            TimeZone = settings.TimeZone is { Length: > 0 } zone ? TimeZoneInfo.FindSystemTimeZoneById(zone) : TimeZoneInfo.Local,
        });
    }

    /// <summary>
    /// Hosting (backups, sandbox read denials) and the runtime (agent directories) must agree on one state root, or the
    /// sandbox would protect one tree while agents live in another. Refuse to start otherwise.
    /// </summary>
    private static void RequireOneStateRoot(CapybaraHostOptions host, HostingOptions hosting, IConfiguration configuration)
    {
        var expected = Path.GetFullPath(host.StateRoot);
        var candidates = new (string Name, string? Value)[]
        {
            ("Capybara:Hosting:StateRoot", hosting.StateRoot),
            ("Capybara:Runtime:StateRoot", configuration[$"{CopilotRuntimeOptions.SectionName}:StateRoot"]),
        };
        foreach (var (name, value) in candidates)
        {
            if (value is { Length: > 0 } && !string.Equals(Path.GetFullPath(value), expected, StringComparison.Ordinal))
            {
                throw new CapybaraConfigurationException([$"{name} ('{value}') must equal Capybara:StateRoot ('{host.StateRoot}')."]);
            }
        }
    }

    private static void RemoveHostedService<T>(IServiceCollection services)
        where T : IHostedService
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }

    private sealed record CoreSettings(CapybaraHostOptions Host, StateLayout State, CapybaraConfiguration Fleet, RuntimeSettings Runtime);

    private sealed record RuntimeSettings(bool AutoStartRequested);
}
