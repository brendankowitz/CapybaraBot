using Azure.Core;
using Azure.Identity;
using Fleet.Contracts.Storage;
using Fleet.Hosting.Agents;
using Fleet.Hosting.Backup;
using Fleet.Hosting.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Hosting.DependencyInjection;

/// <summary>Registers Fleet.Hosting abstractions, host registry, stores, and optional backup worker (FR-HST-01, FR-HST-05, FR-HST-06).</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFleetHosting(this IServiceCollection services, HostingOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

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

        if (options.Backup is not null)
        {
            services.AddSingleton(options.Backup);
            services.AddSingleton<BackupService>();
            services.AddSingleton(new BackupScheduleOptions(TimeSpan.FromHours(2)));
            services.AddHostedService<BackupHostedService>();
        }

        return services;
    }
}
