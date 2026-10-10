using System.Net.Http.Headers;
using System.Net.Sockets;
using Capybara.Connections.Client;
using Capybara.Connections.Health;
using Capybara.Policy.Provenance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Capybara.Connections.DependencyInjection;

/// <summary>How capybarad reaches capybara-broker.</summary>
public sealed class BrokerClientOptions
{
    public Uri BaseAddress { get; set; } = new("http://127.0.0.1:8788");

    /// <summary>When set, connect over this Unix socket instead of TCP (matches <c>Broker:UnixSocketPath</c>).</summary>
    public string? UnixSocketPath { get; set; }

    /// <summary>Development override; production reads <see cref="ApiKeyFile"/>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>File holding the shared broker API key, typically <c>$CREDENTIALS_DIRECTORY/broker-api-key</c>.</summary>
    public string? ApiKeyFile { get; set; }

    public TimeSpan TokenExpiryLeadTime { get; set; } = TimeSpan.FromDays(3);

    internal string? ReadApiKey() =>
        !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey
        : ApiKeyFile is not null && File.Exists(ApiKeyFile) ? File.ReadAllText(ApiKeyFile).Trim()
        : null;
}

public static class BrokerClientServiceCollectionExtensions
{
    public const string HttpClientName = "capybara-broker";

    /// <summary>
    /// capybarad side of the broker (FR-CON-01, FR-CON-03, FR-CON-05): <see cref="BrokerClient"/>,
    /// <see cref="IBrokerToolSource"/> and the token health monitor fed from broker vault metadata. Requires an
    /// <c>IAlertSink</c> registration; registers <see cref="TaintTracker"/> only if absent.
    /// </summary>
    public static IServiceCollection AddCapybaraBrokerClient(this IServiceCollection services, BrokerClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddHttpClient(HttpClientName, client =>
            {
                client.BaseAddress = options.BaseAddress;
                if (options.ReadApiKey() is { } key)
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }
            })
            .ConfigurePrimaryHttpMessageHandler(() => CreateHandler(options));
        services.TryAddSingleton(sp => new BrokerClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));
        services.TryAddSingleton<TaintTracker>();
        services.TryAddSingleton<IBrokerToolSource, BrokerToolSource>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICredentialHealthSource, BrokerCredentialHealthSource>();
        services.Configure<TokenHealthOptions>(o => o.LeadTime = options.TokenExpiryLeadTime);
        services.TryAddSingleton<TokenHealthMonitor>();
        services.AddHostedService<TokenHealthBackgroundService>();
        return services;
    }

    private static SocketsHttpHandler CreateHandler(BrokerClientOptions options)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) };
        if (options.UnixSocketPath is { Length: > 0 } socketPath)
        {
            handler.ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        return handler;
    }
}
