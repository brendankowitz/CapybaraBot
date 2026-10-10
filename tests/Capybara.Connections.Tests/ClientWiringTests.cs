using System.Text.Json;
using Capybara.Connections.Client;
using Capybara.Connections.DependencyInjection;
using Capybara.Connections.Health;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;
using Capybara.Observability.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Capybara.Connections.Tests;

public sealed class ClientWiringTests
{
    [Fact]
    public void Capybarad_side_registration_resolves_without_captive_dependencies()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAlertSink, InMemoryAlertSink>();
        services.AddCapybaraBrokerClient(new BrokerClientOptions { ApiKey = "key" });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<BrokerToolSource>(provider.GetRequiredService<IBrokerToolSource>());
        Assert.IsType<BrokerCredentialHealthSource>(provider.GetRequiredService<ICredentialHealthSource>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is TokenHealthBackgroundService);
    }

    [Fact]
    public async Task Consent_requests_are_posted_to_capybarad_without_secrets()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var handler = new FakeHttpMessageHandler(r =>
        {
            sent = r;
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
        });
        var router = new CapybaradInteractiveAuthRouter(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8787") });
        var request = new InteractiveAuthRequest("email", CredentialProvider.Google, new Uri("https://accounts.google.com/o/oauth2/v2/auth?state=s"), DateTimeOffset.UnixEpoch);

        await router.RouteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("http://127.0.0.1:8787/api/connections/auth-requests", sent!.RequestUri!.AbsoluteUri);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("email", json.RootElement.GetProperty("connection").GetString());
        Assert.Equal("Google", json.RootElement.GetProperty("provider").GetString());
        Assert.False(json.RootElement.TryGetProperty("codeVerifier", out _));
    }

    [Fact]
    public async Task Undeliverable_consent_request_throws()
    {
        var router = new CapybaradInteractiveAuthRouter(new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized))) { BaseAddress = new Uri("http://127.0.0.1:8787") });

        await Assert.ThrowsAsync<HttpRequestException>(async () => await router.RouteAsync(new InteractiveAuthRequest("email", CredentialProvider.Google, new Uri("https://example.test"), DateTimeOffset.UnixEpoch), TestContext.Current.CancellationToken));
    }
}
