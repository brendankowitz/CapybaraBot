using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Gateway.Endpoints;
using Capybara.Policy.Approvals;
using Capybara.Policy.Safety;
using Microsoft.Extensions.DependencyInjection;

namespace Capybara.Gateway.Tests;

/// <summary>
/// The public webhook relay holds an <see cref="ControlSurface.EventRelay"/> token: it can post events and nothing
/// else, so a compromised relay cannot approve, decide, read memory or touch the kill switch (FR-RTN-07).
/// </summary>
public sealed class EventRelayScopeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, object?> HumanRoutes => new()
    {
        { "GET", "/api/approvals", null },
        { "POST", "/api/approvals/{approval}", null },
        { "POST", "/api/approvals/{approval}/act?nonce={nonce}&decision=allow-once", null },
        { "GET", "/api/proposals", null },
        { "POST", "/api/proposals/p1", new ProposalDecisionDto(true) },
        { "GET", "/api/memory/pages", null },
        { "GET", "/api/memory/export", null },
        { "PUT", "/api/memory/page", new SaveMemoryPageDto("/user/x.md", "x") },
        { "POST", "/api/killswitch", new KillSwitchRequestDto("relay") },
        { "DELETE", "/api/killswitch", null },
        { "GET", "/api/agents", null },
        { "POST", "/api/agents/ea/turn", new TurnRequestDto("hi") },
        { "POST", "/api/presence", new PresenceDto(true) },
        { "POST", "/hubs/capybara/negotiate?negotiateVersion=1", null },
        { "GET", "/", null },
        { "GET", "/api/nope", null },
    };

    [Theory]
    [MemberData(nameof(HumanRoutes))]
    public async Task A_relay_token_is_refused_everywhere_but_the_event_ingress(string method, string path, object? body)
    {
        var approval = factory.Services.GetRequiredService<ApprovalService>().Request(Ea, SessionIds.Main(Ea), "email_send", "h", "p", RiskLevel.Medium, MessageOrigin.Agent);
        path = path.Replace("{approval}", approval.Id, StringComparison.Ordinal).Replace("{nonce}", approval.Nonce, StringComparison.Ordinal);
        if (path == $"/api/approvals/{approval.Id}")
        {
            body = new ApprovalDecisionDto(approval.Nonce, ApprovalDecisions.AllowOnce);
        }

        var relay = factory.CreateDeviceClient(factory.RelayToken);
        var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body, body.GetType()),
        };

        var response = await relay.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApprovalStatus.Pending, factory.Services.GetRequiredService<ApprovalService>().Get(approval.Id)!.Status);
        Assert.False(factory.Services.GetRequiredService<KillSwitch>().IsEngaged);
    }

    [Fact]
    public async Task A_relay_token_posts_signed_events()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/events/{ApiFactory.EventSource}/push", UriKind.Relative)) { Content = new ByteArrayContent(body) };
        request.Headers.Add(EventIngressEndpoints.SignatureHeader, "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ApiFactory.EventSecret), body)));

        var response = await factory.CreateDeviceClient(factory.RelayToken).SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task A_relay_token_is_refused_on_the_hub_even_through_the_query_token()
    {
        var response = await factory.CreateDeviceClient(token: null).PostAsync(new Uri($"/hubs/capybara/negotiate?negotiateVersion=1&access_token={factory.RelayToken}", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
