using System.Net;
using System.Net.Http.Json;

namespace Fleet.Gateway.Tests;

public sealed class GatewayTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_is_reachable_without_credentials()
    {
        var response = await factory.CreateDeviceClient(token: null, login: null).GetAsync(new Uri("/healthz", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Api_requires_tailnet_login_and_device_token(bool withToken, bool withLogin)
    {
        var client = factory.CreateDeviceClient(withToken ? factory.PhoneToken : null, withLogin ? GatewayFactory.Login : null);

        var response = await client.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_login_and_unknown_token_are_rejected()
    {
        var wrongLogin = await factory.CreateDeviceClient(factory.PhoneToken, "someone@example.com").GetAsync(new Uri("/api/agents", UriKind.Relative), Ct);
        var unknownToken = await factory.CreateDeviceClient(new string('0', 64)).GetAsync(new Uri("/api/agents", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownToken.StatusCode);
    }

    [Fact]
    public async Task Foreign_origin_is_forbidden()
    {
        var response = await factory.CreateDeviceClient(factory.PhoneToken, origin: "https://evil.example").GetAsync(new Uri("/api/agents", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_device_lists_the_sample_agents()
    {
        var client = factory.CreateDeviceClient(factory.PhoneToken, origin: GatewayFactory.Origin);

        var agents = await client.GetFromJsonAsync<List<AgentSummary>>(new Uri("/api/agents", UriKind.Relative), Ct);

        Assert.NotNull(agents);
        Assert.Equal(["code", "ea", "research"], agents.Select(a => a.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Any_device_can_engage_the_kill_switch_but_only_desktop_releases_it()
    {
        var phone = factory.CreateDeviceClient(factory.PhoneToken);
        var desktop = factory.CreateDeviceClient(factory.DesktopToken);

        var engage = await phone.PostAsJsonAsync(new Uri("/api/killswitch", UriKind.Relative), new { reason = "test" }, Ct);
        Assert.True(engage.IsSuccessStatusCode, $"{engage.StatusCode}");

        var phoneRelease = await phone.DeleteAsync(new Uri("/api/killswitch", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, phoneRelease.StatusCode);

        var desktopRelease = await desktop.DeleteAsync(new Uri("/api/killswitch", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, desktopRelease.StatusCode);
    }

    [Fact]
    public async Task Approvals_and_audit_endpoints_respond()
    {
        var client = factory.CreateDeviceClient(factory.DesktopToken);

        var approvals = await client.GetAsync(new Uri("/api/approvals", UriKind.Relative), Ct);
        var audit = await client.GetAsync(new Uri("/api/audit/verify", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, approvals.StatusCode);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
    }

    private sealed record AgentSummary(string Id);
}
