using Fleet.Connections.Broker;
using Fleet.Connections.Client;
using Fleet.Connections.Connectors;
using Fleet.Connections.Grants;
using Fleet.Connections.Health;
using Fleet.Connections.Mcp;
using Fleet.Connections.Vault;
using Fleet.Contracts.Storage;
using Fleet.Observability.Alerts;
using Fleet.Policy.Audit;
using Fleet.Policy.Safety;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fleet.Connections.DependencyInjection;

/// <summary>Options for registering Fleet.Connections (FR-CON-01).</summary>
public sealed class ConnectionsOptions
{
    public string? CredentialsDirectory { get; set; }

    public TimeSpan TokenExpiryLeadTime { get; set; } = TimeSpan.FromDays(3);
}

public static class ConnectionsServiceCollectionExtensions
{
    /// <summary>Adds the connection broker, grant enforcement, token vault, MCP façade and health monitor (FR-CON-01..10).</summary>
    public static IServiceCollection AddFleetConnections(this IServiceCollection services, ConnectionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IGrantProvider, InMemoryGrantProvider>();
        services.TryAddSingleton<GrantEnforcer>();
        services.TryAddSingleton<IConnectorRegistry, InMemoryConnectorRegistry>();
        services.TryAddSingleton<TokenVault>();
        services.TryAddSingleton<SecretRedactor>();
        services.TryAddSingleton<PerAgentBearerTokenIssuer>();
        services.TryAddSingleton<ICredentialHealthSource, InMemoryCredentialHealthSource>();
        services.Configure<TokenHealthOptions>(o => o.LeadTime = options.TokenExpiryLeadTime);
        services.TryAddSingleton<TokenHealthMonitor>();
        services.AddHostedService<TokenHealthBackgroundService>();
        services.TryAddSingleton<ConnectionBroker>();
        services.TryAddSingleton<IBrokerToolSource, BrokerToolSource>();
        services.TryAddSingleton<KillSwitch>();
        services.TryAddSingleton<IAlertSink, InMemoryAlertSink>();
        services.TryAddSingleton<IAuditLog, NullAuditLog>();

        if (options.CredentialsDirectory is { Length: > 0 })
        {
            services.TryAddSingleton<ISecretStore>(_ => new CredentialsDirectorySecretStore(options.CredentialsDirectory));
        }

        services.TryAddSingleton<ISecretStore, EmptySecretStore>();
        services.AddHttpClient<BrokerClient>();
        return services;
    }
}

internal sealed class NullAuditLog : IAuditLog
{
    public ValueTask<AuditEntry> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default) => ValueTask.FromResult(new AuditEntry
    {
        Sequence = 0,
        At = DateTimeOffset.UnixEpoch,
        Kind = record.Kind,
        Decision = record.Decision,
        Agent = record.Agent,
        SessionId = record.SessionId,
        Tool = record.Tool,
        ArgsHash = record.ArgsHash,
        Detail = record.Detail,
        TraceId = record.TraceId,
        PreviousHash = string.Empty,
    });

    public async IAsyncEnumerable<AuditEntry> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<bool> VerifyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}


internal sealed class EmptySecretStore : ISecretStore
{
    public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);

    public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException("No credential store is configured.");

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException("No credential store is configured.");

    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<string>>([]);
}
