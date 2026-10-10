using Fleet.Observability.Alerts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Fleet.Connections.Health;

/// <summary>Credential health metadata for expiry alerting (FR-CON-05).</summary>
public sealed record CredentialHealth(string ConnectionId, CredentialKind Kind, DateTimeOffset ExpiresAt, bool GoogleAppInProduction = true, string? Detail = null);

public enum CredentialKind
{
    GoogleRefreshTokenTesting,
    MicrosoftRefreshToken,
    GitHubFineGrainedPat,
    GitHubAppInstallationToken,
}

public interface ICredentialHealthSource
{
    ValueTask<IReadOnlyList<CredentialHealth>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryCredentialHealthSource : ICredentialHealthSource
{
    private readonly List<CredentialHealth> _items = [];

    public void Set(IEnumerable<CredentialHealth> items)
    {
        _items.Clear();
        _items.AddRange(items);
    }

    public ValueTask<IReadOnlyList<CredentialHealth>> ListAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<CredentialHealth>>([.. _items]);
}

public sealed class TokenHealthOptions
{
    public TimeSpan LeadTime { get; set; } = TimeSpan.FromDays(3);

    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>Alerts before OAuth/PAT expiry and on unsafe Google testing app state (FR-CON-05).</summary>
public sealed class TokenHealthMonitor(ICredentialHealthSource source, IAlertSink alerts, TimeProvider time, IOptions<TokenHealthOptions> options)
{
    public async ValueTask CheckAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var lead = options.Value.LeadTime;
        foreach (var item in await source.ListAsync(cancellationToken))
        {
            if (item.Kind == CredentialKind.GoogleRefreshTokenTesting && !item.GoogleAppInProduction)
            {
                await alerts.PublishAsync(new Alert("token_health", AlertSeverity.Critical, $"{item.ConnectionId}: Google OAuth app is not in production; testing refresh tokens expire after 7 days.", now), cancellationToken);
            }

            if (item.ExpiresAt <= now.Add(lead))
            {
                await alerts.PublishAsync(new Alert("token_expiry", item.ExpiresAt <= now ? AlertSeverity.Critical : AlertSeverity.Warning, $"{item.ConnectionId}: {item.Kind} expires at {item.ExpiresAt:O}.", now), cancellationToken);
            }
        }
    }
}

/// <summary>Background token health check service; failures are alerted, not silent (FR-CON-05).</summary>
public sealed class TokenHealthBackgroundService(TokenHealthMonitor monitor, TimeProvider time, IOptions<TokenHealthOptions> options, IAlertSink alerts) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await monitor.CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await alerts.PublishAsync(new Alert("token_health_failure", AlertSeverity.Critical, "Token health check failed.", time.GetUtcNow()), stoppingToken);
            }

            await Task.Delay(options.Value.CheckInterval, time, stoppingToken);
        }
    }
}
