using Azure.Core;
using Azure.Identity;
using Fleet.Contracts.Storage;
using Fleet.Hosting.Agents;
using Fleet.Hosting.Backup;
using Fleet.Hosting.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Hosting.DependencyInjection;

/// <summary>Registers Fleet.Hosting abstractions, host registry, stores, and optional backup worker (FR-HST-01, FR-HST-05, FR-HST-06).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers hosting services. Backup settings come from <see cref="HostingOptions.Backup"/>/<see cref="HostingOptions.BackupSchedule"/>
    /// when set, otherwise from the <c>Fleet:Backup</c> section of <paramref name="configuration"/> (see <see cref="BackupConfiguration"/>).
    /// </summary>
    public static IServiceCollection AddFleetHosting(this IServiceCollection services, HostingOptions options, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        var bound = configuration is null ? null : BackupConfiguration.Bind(configuration);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
        services.AddSingleton<IStateStore>(_ => new SqliteStateStore(options.StateDatabasePath));
        services.AddSingleton<IBlobStore>(_ => new FileSystemBlobStore(options.BlobRoot));
        services.AddSingleton<TokenCredential, DefaultAzureCredential>();
        services.AddHttpClient();
        services.AddSingleton<SystemdHost>();
        services.AddSingleton<PodmanHost>();
        services.AddHttpClient<RemoteAgentHost>(client =>
        {
            if (options.RemoteBaseUri is not null)
            {
                client.BaseAddress = options.RemoteBaseUri;
            }
        });
        services.AddHttpClient<AcaSandboxHost>((provider, client) =>
        {
            if (options.AcaBaseUri is not null)
            {
                client.BaseAddress = options.AcaBaseUri;
            }
        });
        services.AddSingleton(provider => new AcaSandboxHostOptions(options.AcaBaseUri ?? new Uri("https://management.azure.com/"), options.AcaApiVersion));
        services.AddSingleton(provider => new AgentHostFactory(
            new Dictionary<AgentHostKind, IAgentHost>
            {
                [AgentHostKind.Systemd] = provider.GetRequiredService<SystemdHost>(),
                [AgentHostKind.Podman] = provider.GetRequiredService<PodmanHost>(),
                [AgentHostKind.Aca] = provider.GetRequiredService<AcaSandboxHost>(),
                [AgentHostKind.Hybrid] = provider.GetRequiredService<RemoteAgentHost>(),
            },
            options.AgentHosts,
            options.DefaultHost));

        if (options.Backup is not null || bound is not null)
        {
            // Explicit options win over configuration. The blob store holds the archives, so it must never be archived itself.
            var backup = options.Backup ?? bound!.Value.Options;
            services.AddSingleton(backup with { ExcludedDirectories = [.. backup.ExcludedDirectories ?? [], options.BlobRoot] });
            services.AddSingleton<BackupService>();
            services.AddSingleton(options.BackupSchedule ?? bound?.Schedule ?? BackupScheduleOptions.Default);
            services.AddHostedService<BackupHostedService>();
        }

        return services;
    }
}
