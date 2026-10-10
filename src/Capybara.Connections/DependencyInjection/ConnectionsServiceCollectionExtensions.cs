using System.Net.Http.Headers;
using Capybara.Connections.Broker;
using Capybara.Connections.Connectors;
using Capybara.Connections.Grants;
using Capybara.Connections.Health;
using Capybara.Connections.Mcp;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;
using Capybara.Connections.Vault;
using Capybara.Contracts.Storage;
using Capybara.Policy.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Capybara.Connections.DependencyInjection;

public static class ConnectionsServiceCollectionExtensions
{
    internal const string ConnectorHttpClient = "capybara-connector";
    internal const string CapybaradHttpClient = "capybara-capybarad";

    /// <summary>
    /// Registers everything the capybara-broker process runs (FR-CON-01..10): connectors over long-lived HTTP clients,
    /// read-only grants from config, the encrypted writable vault layered over systemd credentials, OAuth refresh and
    /// consent, the hash-chained audit log, the persistent freeze, the tool catalog, MCP and the startup validator.
    /// capybarad must not call this; it uses <see cref="BrokerClientServiceCollectionExtensions.AddCapybaraBrokerClient"/>.
    /// </summary>
    public static IServiceCollection AddCapybaraConnectionBroker(this IServiceCollection services, BrokerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(ConnectorHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) });
        services.AddHttpClient(CapybaradHttpClient, client =>
        {
            client.BaseAddress = options.CapybaradUrl;
            if (options.ReadApiKey() is { } key)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
        });

        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new ConnectorRegistry(options.Connections.Select(c => ConnectorRegistry.Create(ConnectionDefinition.Create(c.Id, c.Kind), factory.CreateClient(ConnectorHttpClient))));
        });
        services.AddSingleton(_ => ConfigGrantProvider.Load(options.ConfigRoot));
        services.AddSingleton<IGrantProvider>(sp => sp.GetRequiredService<ConfigGrantProvider>());
        services.AddSingleton<GrantEnforcer>();
        services.AddSingleton<BrokerToolCatalog>();

        services.AddSingleton(_ =>
        {
            var key = EncryptedFileSecretStore.LoadKey(Path.Combine(options.ResolveCredentialsDirectory(), options.VaultKeyCredential));
            return new EncryptedFileSecretStore(Path.Combine(options.ResolveStateDirectory(), "vault"), key);
        });
        services.AddSingleton<ISecretStore>(sp => new LayeredSecretStore(
            sp.GetRequiredService<EncryptedFileSecretStore>(),
            new CredentialsDirectorySecretStore(options.ResolveCredentialsDirectory())));
        services.AddSingleton<TokenVault>();
        services.AddSingleton<IReadOnlyDictionary<CredentialProvider, OAuthClientOptions>>(_ => OAuthRegistrations(options));
        services.AddSingleton(sp => new OAuthClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(ConnectorHttpClient)));
        services.AddSingleton<IInteractiveAuthRouter>(sp => new CapybaradInteractiveAuthRouter(sp.GetRequiredService<IHttpClientFactory>().CreateClient(CapybaradHttpClient)));
        services.AddSingleton<CredentialManager>();
        services.AddSingleton<ICredentialHealthSource, VaultCredentialHealthSource>();

        services.AddSingleton<IAuditLog>(sp => new FileAuditLog(Path.Combine(options.ResolveStateDirectory(), "audit", "broker-audit.jsonl"), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new BrokerFreeze(Path.Combine(options.ResolveStateDirectory(), "broker.frozen"), sp.GetRequiredService<IAuditLog>()));
        services.AddSingleton<ConnectionBroker>();
        services.AddSingleton(new McpServerCatalog([.. options.McpServers]));
        services.AddCapybaraBrokerMcp();
        services.AddHostedService<BrokerStartupValidator>();
        return services;
    }

    private static Dictionary<CredentialProvider, OAuthClientOptions> OAuthRegistrations(BrokerOptions options)
    {
        var registrations = new Dictionary<CredentialProvider, OAuthClientOptions>();
        foreach (var (name, registration) in options.OAuth)
        {
            if (!Enum.TryParse<CredentialProvider>(name, ignoreCase: true, out var provider) || provider == CredentialProvider.GitHub)
            {
                throw new InvalidOperationException($"Broker:OAuth:{name} is not an OAuth provider (Google, Microsoft).");
            }

            registrations[provider] = registration;
        }

        return registrations;
    }
}
