using Capybara.Connections.Health;
using Capybara.Observability.Alerts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Connections.Tests;

public sealed class TokenHealthTests
{
    [Fact]
    public async Task Alerts_before_expiry_and_for_google_testing_status()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-10T00:00:00Z"));
        var source = new InMemoryCredentialHealthSource();
        source.Set([new CredentialHealth("google", CredentialKind.GoogleRefreshTokenTesting, time.GetUtcNow().AddDays(2), GoogleAppInProduction: false)]);
        var sink = new InMemoryAlertSink();
        var monitor = new TokenHealthMonitor(source, sink, time, Options.Create(new TokenHealthOptions { LeadTime = TimeSpan.FromDays(3) }));

        await monitor.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Contains(sink.Recent, a => a.Kind == "token_health");
        Assert.Contains(sink.Recent, a => a.Kind == "token_expiry");
    }
}
