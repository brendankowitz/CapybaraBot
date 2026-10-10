using System.Net;
using System.Net.Http.Headers;

namespace Fleet.Client.Tests;

public sealed class FleetApiClientTests
{
    [Fact]
    public async Task Agents_uses_expected_route_and_reads_camel_case_json()
    {
        var handler = new CaptureHandler("""[{"id":"ea","name":"EA","job":"assist","model":"auto","paused":false,"budgetExhausted":true}]""");
        var client = new FleetApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://fleet.example") });

        var agents = await client.GetAgentsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/api/agents", handler.RequestUri?.PathAndQuery);
        Assert.Single(agents);
        Assert.True(agents[0].BudgetExhausted);
    }

    [Fact]
    public async Task Planned_routes_return_empty_on_404()
    {
        var client = new FleetApiClient(new HttpClient(new CaptureHandler(string.Empty, HttpStatusCode.NotFound)) { BaseAddress = new Uri("https://fleet.example") });

        var routines = await client.GetRoutinesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(routines);
    }

    [Fact]
    public async Task Authentication_handler_adds_bearer_and_optional_login()
    {
        var capture = new CaptureHandler("{}");
        using var handler = new FleetAuthenticationHandler(new FleetClientOptions { DeviceToken = "device-token", TailscaleLoginForLocalTesting = "owner@example.com" }) { InnerHandler = capture };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fleet.example") };

        await http.GetAsync(new Uri("/api/agents", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(new AuthenticationHeaderValue("Bearer", "device-token"), capture.Authorization);
        Assert.Equal("owner@example.com", capture.Login);
    }

    private sealed class CaptureHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public string? Login { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Login = request.Headers.TryGetValues(FleetAuthenticationHandler.TailscaleLoginHeader, out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
        }
    }
}
