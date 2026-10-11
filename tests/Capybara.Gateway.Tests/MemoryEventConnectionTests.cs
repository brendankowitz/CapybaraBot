using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capybara.Client;
using Capybara.Gateway.Endpoints;
using Capybara.Observability.Alerts;
using Capybara.Policy.Rules;
using Capybara.Scheduling.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Capybara.Gateway.Tests;

/// <summary>WP11 acceptance 6, 9 and 10: memory administration, webhook ingress and connection switches.</summary>
public sealed class MemoryEventConnectionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/../etc/passwd")]
    [InlineData("/elsewhere/x.md")]
    [InlineData("")]
    public async Task Memory_paths_outside_the_wiki_are_refused(string path)
    {
        var client = factory.CreateDeviceClient(factory.PhoneToken);

        var read = await client.GetAsync(new Uri($"/api/memory/page?path={Uri.EscapeDataString(path)}", UriKind.Relative), Ct);
        var write = await client.PutAsJsonAsync(new Uri("/api/memory/page", UriKind.Relative), new SaveMemoryPageDto(path, "x"), Ct);
        var delete = await client.DeleteAsync(new Uri($"/api/memory/page?path={Uri.EscapeDataString(path)}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, write.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
    }

    [Fact]
    public async Task A_human_edit_keeps_the_page_description_and_missing_pages_are_404()
    {
        var api = factory.Api(factory.PhoneToken);
        var client = factory.CreateDeviceClient(factory.PhoneToken);

        await api.SaveMemoryPageAsync("/agents/ea/runbook.md", "step one\n", Ct);
        await api.SaveMemoryPageAsync("/agents/ea/runbook.md", "step two\n", Ct);

        Assert.Equal("step two\n", (await api.GetMemoryPageAsync("/agents/ea/runbook.md", Ct)).Content);
        var raw = await File.ReadAllTextAsync(Path.Combine(factory.StateRoot, "memory", "agents", "ea", "runbook.md"), Ct);
        Assert.Contains("description: runbook", raw, StringComparison.Ordinal);
        Assert.Contains("source: user:pwa", raw, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri("/api/memory/page?path=/user/none.md", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(new Uri("/api/memory/page?path=/user/none.md", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(new Uri("/api/memory/pending/none", UriKind.Relative), new PendingChangeDecisionDto(true), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_signed_webhook_is_accepted_and_routed_in_the_background_as_tainted_input()
    {
        var client = factory.CreateDeviceClient(factory.RelayToken);
        var alerts = factory.Services.GetRequiredService<FileAlertSink>();

        // Not JSON, so the router rejects it with an alert: proof that it was routed after the 202.
        var response = await client.SendAsync(Signed("github", "push", "not json", ApiFactory.EventSecret), Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await Eventually.TrueAsync(() => alerts.Recent.Any(a => a.Kind == "routine.event-rejected" && a.Message.Contains("github:push", StringComparison.Ordinal)), "the event is routed");
    }

    [Fact]
    public async Task Webhooks_need_a_known_source_a_valid_signature_a_device_and_a_bounded_body()
    {
        var client = factory.CreateDeviceClient(factory.RelayToken);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Signed("github", "push", "{}", "wrong-secret"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri("/api/events/github/push", UriKind.Relative)) { Content = new StringContent("{}") }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Signed("gitlab", "push", "{}", ApiFactory.EventSecret), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateDeviceClient(token: null).SendAsync(Signed("github", "push", "{}", ApiFactory.EventSecret), Ct)).StatusCode);

        // People's phones do not post events; the desktop may.
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateDeviceClient(factory.PhoneToken).SendAsync(Signed("github", "push", "{}", ApiFactory.EventSecret), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await factory.CreateDeviceClient(factory.DesktopToken).SendAsync(Signed("github", "push", "{}", ApiFactory.EventSecret), Ct)).StatusCode);

        var huge = new string('x', WebhookEventSource.MaxPayloadBytes + 1);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(Signed("github", "push", huge, ApiFactory.EventSecret), Ct)).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_connection_denies_its_tools_at_the_policy_gate_until_it_is_enabled_again()
    {
        var desktop = factory.Api(factory.DesktopToken);
        var rules = factory.Services.GetRequiredService<RulesEngine>();
        using var args = JsonDocument.Parse("{}");

        await desktop.SetConnectionEnabledAsync(ApiFactory.Connection, false, Ct);

        Assert.False(Assert.Single(await desktop.GetConnectionsAsync(Ct)).Enabled);
        Assert.Equal(RuleEffect.Deny, rules.Evaluate("ea", "email_send", args.RootElement)?.Rule.Effect);
        Assert.Equal(RuleEffect.Deny, rules.Evaluate("research", "email_read", args.RootElement)?.Rule.Effect);
        Assert.NotEqual(RuleEffect.Deny, rules.Evaluate("ea", "calendar_read", args.RootElement)?.Rule.Effect);
        Assert.Contains("email_*", await File.ReadAllTextAsync(Path.Combine(factory.StateRoot, "policy", "learned-rules.json"), Ct), StringComparison.Ordinal);

        await desktop.SetConnectionEnabledAsync(ApiFactory.Connection, true, Ct);

        Assert.True(Assert.Single(await desktop.GetConnectionsAsync(Ct)).Enabled);
        Assert.NotEqual(RuleEffect.Deny, rules.Evaluate("ea", "email_send", args.RootElement)?.Rule.Effect);

        var unknown = await factory.CreateDeviceClient(factory.DesktopToken).PutAsJsonAsync(new Uri("/api/connections/nope/enabled", UriKind.Relative), new SetConnectionEnabledDto(false), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static HttpRequestMessage Signed(string source, string type, string body, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes));
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/events/{source}/{type}", UriKind.Relative)) { Content = new ByteArrayContent(bytes) };
        request.Headers.Add(EventIngressEndpoints.SignatureHeader, signature);
        return request;
    }
}
