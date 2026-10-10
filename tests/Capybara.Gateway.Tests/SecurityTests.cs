using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Capybara.Channels;
using Capybara.Client;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;
using Capybara.Gateway.Security;
using Capybara.Policy.Audit;
using Capybara.Policy.Safety;
using Microsoft.Extensions.DependencyInjection;

namespace Capybara.Gateway.Tests;

/// <summary>
/// WP11 acceptance 3, 10, 11: desktop-only actions, the broker's consent route, the tokenless SPA shell and the
/// security headers.
/// </summary>
public sealed class SecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, object?> DesktopOnly => new()
    {
        { "GET", "/api/lifecycle/templates", null },
        { "POST", "/api/agents", new CreateAgentDto("assistant", "phoneagent", "Phone agent") },
        { "POST", "/api/agents/ea/duplicate", new DuplicateAgentDto("ea3", "EA three") },
        { "POST", "/api/agents/ea/hide", new SetHiddenDto(true) },
        { "POST", "/api/agents/ea/archive", null },
        { "DELETE", "/api/agents/ea", null },
        { "DELETE", "/api/killswitch", null },
        { "PUT", $"/api/connections/{ApiFactory.Connection}/enabled", new SetConnectionEnabledDto(false) },
    };

    [Theory]
    [MemberData(nameof(DesktopOnly))]
    public async Task Desktop_only_actions_are_refused_to_a_phone_before_anything_changes(string method, string path, object? body)
    {
        var configBefore = Directory.GetFileSystemEntries(Path.Combine(factory.ConfigRoot, "agents")).Order().ToList();
        var client = factory.CreateDeviceClient(factory.PhoneToken);

        var response = await client.SendAsync(Request(method, path, body), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(configBefore, Directory.GetFileSystemEntries(Path.Combine(factory.ConfigRoot, "agents")).Order().ToList());
        Assert.True(factory.Services.GetRequiredService<ConnectionControl.ConnectionSwitches>().IsEnabled(ApiFactory.Connection));
    }

    [Fact]
    public async Task The_spa_shell_is_served_tokenless_for_client_routes_with_security_headers()
    {
        var client = factory.CreateDeviceClient(token: null);

        foreach (var path in new[] { "/", "/index.html", "/questions/q-123", "/approvals", "/agents/ea" })
        {
            var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }
    }

    [Theory]
    [InlineData("GET", "/api/agents")]
    [InlineData("GET", "/API/agents")]
    [InlineData("GET", "/Api/Approvals")]
    [InlineData("GET", "/hubs/capybara")]
    [InlineData("POST", "/questions/q-123")]
    public async Task Without_a_device_token_only_the_shell_is_served(string method, string path)
    {
        var client = factory.CreateDeviceClient(token: null);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative)), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_responses_carry_the_security_headers_too()
    {
        var response = await factory.CreateDeviceClient(factory.PhoneToken).GetAsync(new Uri("/api/agents", UriKind.Relative), Ct);

        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task The_broker_consent_route_takes_only_the_broker_key_and_pushes_no_link()
    {
        var consent = new InteractiveAuthRequest(ApiFactory.Connection, CredentialProvider.Google, new Uri("https://accounts.google.com/o/oauth2/v2/auth?state=s3cr3t"), factory.Time.GetUtcNow().AddMinutes(10));
        var route = new Uri(TailnetAuthenticationMiddleware.BrokerConsentPath, UriKind.Relative);

        var anonymous = await factory.CreateClient().PostAsJsonAsync(route, consent, Ct);
        var device = await factory.CreateDeviceClient(factory.DesktopToken).PostAsJsonAsync(route, consent, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, device.StatusCode);

        var broker = factory.CreateClient();
        broker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.BrokerKey);
        var accepted = await broker.PostAsJsonAsync(route, consent, Ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var push = Assert.Single(factory.Channel.Sent, n => n.Kind == NotificationKind.NeedsInput && n.DeepLinkPath == "/connections");
        Assert.DoesNotContain("s3cr3t", push.Title + push.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(push.Actions, a => a.CallbackPath.Contains("s3cr3t", StringComparison.Ordinal));

        // The link is shown only on the authenticated connections page.
        var connection = Assert.Single(await factory.Api(factory.PhoneToken).GetConnectionsAsync(Ct));
        Assert.Contains("s3cr3t", connection.Health?.Detail, StringComparison.Ordinal);

        // The broker key opens nothing else.
        var elsewhere = await broker.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, elsewhere.StatusCode);

        var insecure = await broker.PostAsJsonAsync(route, consent with { AuthorizationUri = new Uri("http://evil.example/") }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, insecure.StatusCode);
    }

    [Fact]
    public async Task Human_actions_are_attributed_to_the_device_never_to_body_fields()
    {
        var desktop = factory.CreateDeviceClient(factory.DesktopToken);

        // Extra fields that look like a principal are ignored.
        var response = await desktop.PostAsJsonAsync(new Uri("/api/agents", UriKind.Relative), new { template = "assistant", id = "attributed", name = "Attributed", by = "agent:ea", principal = "ea" }, Ct);
        response.EnsureSuccessStatusCode();

        var entries = new List<AuditEntry>();
        await foreach (var entry in factory.Services.GetRequiredService<IAuditLog>().ReadAllAsync(Ct))
        {
            entries.Add(entry);
        }

        var created = Assert.Single(entries, e => e.Kind == "config_change" && e.Agent == "attributed");
        Assert.Contains($"by {GatewayFactory.Login} (desktop)", created.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("agent:ea", created.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engaging_the_kill_switch_is_allowed_from_a_phone()
    {
        var phone = factory.Api(factory.PhoneToken);

        await phone.EngageKillSwitchAsync("lost phone drill", Ct);
        Assert.True(factory.Services.GetRequiredService<KillSwitch>().IsEngaged);

        await factory.Api(factory.DesktopToken).ReleaseKillSwitchAsync(Ct);
        Assert.False(factory.Services.GetRequiredService<KillSwitch>().IsEngaged);
    }

    private static HttpRequestMessage Request(string method, string path, object? body) => new(new HttpMethod(method), new Uri(path, UriKind.Relative))
    {
        Content = body is null ? null : JsonContent.Create(body, body.GetType()),
    };
}
