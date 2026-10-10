using System.Net;
using Capybara.Gateway.Realtime;

namespace Capybara.Gateway.Tests;

public sealed class RealtimeAndPwaTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hub_negotiate_requires_device_token()
    {
        var response = await factory.CreateDeviceClient(token: null).PostAsync(new Uri("/hubs/capybara/negotiate?negotiateVersion=1", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hub_accepts_access_token_query_only_for_hub_path()
    {
        var hub = await factory.CreateDeviceClient(token: null).PostAsync(new Uri($"/hubs/capybara/negotiate?negotiateVersion=1&access_token={factory.PhoneToken}", UriKind.Relative), null, Ct);
        var api = await factory.CreateDeviceClient(token: null).GetAsync(new Uri($"/api/agents?access_token={factory.PhoneToken}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, hub.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }

    [Fact]
    public async Task Pwa_shell_is_allowed_with_identity_but_api_still_requires_token()
    {
        var client = factory.CreateDeviceClient(token: null);

        var shell = await client.GetAsync(new Uri("/index.html", UriKind.Relative), Ct);
        var api = await client.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }

    [Fact]
    public async Task Agent_activity_tracker_broadcasts_state_transitions()
    {
        var events = new RecordingEvents();
        var tracker = new AgentActivityTracker(events);

        await tracker.TurnStartedAsync("ea", Ct);
        await tracker.ApprovalPendingAsync("ea", Ct);
        await tracker.MarkReadAsync("ea", Ct);

        Assert.Equal(AgentActivityState.Working, events.Records[0].State);
        Assert.Equal(AgentActivityState.NeedsAttention, events.Records[1].State);
        Assert.Equal(1, events.Records[1].Unread);
        Assert.Equal(AgentActivityState.Idle, tracker.Get("ea").State);
        Assert.Equal(0, tracker.Get("ea").Unread);
    }

    private sealed class RecordingEvents : ICapybaraEvents
    {
        public List<AgentActivitySnapshot> Records { get; } = [];

        public Task PublishAgentStatusChangedAsync(string agentId, AgentActivityState state, int unread, CancellationToken cancellationToken = default)
        {
            Records.Add(new AgentActivitySnapshot(agentId, state, unread));
            return Task.CompletedTask;
        }

        public Task PublishThreadEntryAsync(object entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishApprovalRequestedAsync(object approval, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishApprovalResolvedAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishAlertAsync(object alert, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishKillSwitchChangedAsync(bool engaged, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
