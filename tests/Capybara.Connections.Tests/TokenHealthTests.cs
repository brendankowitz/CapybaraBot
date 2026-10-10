using Capybara.Connections.Connectors;
using Capybara.Connections.Health;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;
using Capybara.Connections.Vault;
using Capybara.Observability.Alerts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Connections.Tests;

public sealed class TokenHealthTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task Alerts_before_expiry_and_for_google_testing_status()
    {
        var source = new InMemoryCredentialHealthSource();
        source.Set([new CredentialHealth("google", CredentialKind.GoogleRefreshTokenTesting, _time.GetUtcNow().AddDays(2), GoogleAppInProduction: false)]);
        var sink = new InMemoryAlertSink();
        var monitor = new TokenHealthMonitor(source, sink, _time, Options.Create(new TokenHealthOptions { LeadTime = TimeSpan.FromDays(3) }));

        await monitor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Contains(sink.Recent, a => a.Kind == "token_health");
        Assert.Contains(sink.Recent, a => a.Kind == "token_expiry");
    }

    [Fact]
    public async Task Vault_metadata_drives_credential_health()
    {
        var secrets = new TestSecretStore();
        var vault = new TokenVault(secrets);
        var now = _time.GetUtcNow();
        var ct = TestContext.Current.CancellationToken;
        await vault.SetAsync("outlook", new ConnectionCredential { AccessToken = "a", RefreshToken = "r", ConsentedAt = now.AddDays(-200), RefreshedAt = now.AddDays(-89) }, ct);
        await vault.SetAsync("email", new ConnectionCredential { AccessToken = "a", RefreshToken = "r", ConsentedAt = now.AddDays(-6) }, ct);
        await vault.SetAsync("github", new ConnectionCredential { AccessToken = "github_pat_x", ExpiresAt = now.AddDays(30) }, ct);
        var registry = new ConnectorRegistry(
        [
            ConnectorRegistry.Create(ConnectionDefinition.Create("outlook", ConnectorKind.GraphMail), new HttpClient()),
            ConnectorRegistry.Create(ConnectionDefinition.Create("email", ConnectorKind.Gmail), new HttpClient()),
            ConnectorRegistry.Create(ConnectionDefinition.Create("github", ConnectorKind.GitHub), new HttpClient()),
            ConnectorRegistry.Create(ConnectionDefinition.Create("calendar", ConnectorKind.GoogleCalendar), new HttpClient()),
        ]);
        var registrations = new Dictionary<CredentialProvider, OAuthClientOptions> { [CredentialProvider.Google] = new() { InProduction = false } };
        var source = new VaultCredentialHealthSource(vault, registry, registrations, _time);

        var health = (await source.ListAsync(ct)).ToDictionary(h => h.ConnectionId);

        Assert.Equal((CredentialKind.MicrosoftRefreshToken, now.AddDays(1)), (health["outlook"].Kind, health["outlook"].ExpiresAt));
        Assert.Equal((CredentialKind.GoogleRefreshTokenTesting, now.AddDays(1), false), (health["email"].Kind, health["email"].ExpiresAt, health["email"].GoogleAppInProduction));
        Assert.Equal((CredentialKind.GitHubFineGrainedPat, now.AddDays(30)), (health["github"].Kind, health["github"].ExpiresAt));
        Assert.Equal(now, health["calendar"].ExpiresAt);
        Assert.Contains("no credential", health["calendar"].Detail, StringComparison.Ordinal);

        var sink = new InMemoryAlertSink();
        await new TokenHealthMonitor(source, sink, _time, Options.Create(new TokenHealthOptions { LeadTime = TimeSpan.FromDays(3) })).CheckAsync(ct);
        Assert.Contains(sink.Recent, a => a.Kind == "token_expiry" && a.Message.StartsWith("outlook", StringComparison.Ordinal));
        Assert.Contains(sink.Recent, a => a.Kind == "token_expiry" && a.Severity == AlertSeverity.Critical && a.Message.Contains("calendar: no credential", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Recent, a => a.Message.StartsWith("github", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pat_without_recorded_expiry_raises_a_standing_warning()
    {
        var vault = new TokenVault(new TestSecretStore());
        await vault.SetAsync("github", new ConnectionCredential { AccessToken = "github_pat_x" }, TestContext.Current.CancellationToken);
        var registry = new ConnectorRegistry([ConnectorRegistry.Create(ConnectionDefinition.Create("github", ConnectorKind.GitHub), new HttpClient())]);
        var source = new VaultCredentialHealthSource(vault, registry, new Dictionary<CredentialProvider, OAuthClientOptions>(), _time);
        var sink = new InMemoryAlertSink();

        var health = Assert.Single(await source.ListAsync(TestContext.Current.CancellationToken));
        await new TokenHealthMonitor(source, sink, _time, Options.Create(new TokenHealthOptions())).CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(health.ExpiresAt);
        var alert = Assert.Single(sink.Recent);
        Assert.Equal(("token_expiry_unknown", AlertSeverity.Warning), (alert.Kind, alert.Severity));
        Assert.Contains("expiresAt", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consent_required_connection_reports_as_expired()
    {
        var vault = new TokenVault(new TestSecretStore());
        var since = _time.GetUtcNow().AddHours(-1);
        await vault.SetAsync("email", new ConnectionCredential { AccessToken = "a", ConsentRequiredSince = since, RefreshedAt = _time.GetUtcNow() }, TestContext.Current.CancellationToken);
        var registry = new ConnectorRegistry([ConnectorRegistry.Create(ConnectionDefinition.Create("email", ConnectorKind.GraphMail), new HttpClient())]);
        var source = new VaultCredentialHealthSource(vault, registry, new Dictionary<CredentialProvider, OAuthClientOptions>(), _time);
        var sink = new InMemoryAlertSink();

        var health = Assert.Single(await source.ListAsync(TestContext.Current.CancellationToken));
        await new TokenHealthMonitor(source, sink, _time, Options.Create(new TokenHealthOptions())).CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(since, health.ExpiresAt);
        var alert = Assert.Single(sink.Recent);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("consent required", alert.Message, StringComparison.Ordinal);
    }
}