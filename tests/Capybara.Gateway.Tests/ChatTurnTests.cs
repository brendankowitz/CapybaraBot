using System.Net;
using System.Net.Http.Json;
using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Gateway.Realtime;
using Capybara.Orchestration.Inbox;
using Microsoft.Extensions.DependencyInjection;

namespace Capybara.Gateway.Tests;

/// <summary>WP11 acceptance 7: turns through the inbox, id-correlated chat history, incognito, model, unread.</summary>
public sealed class ChatTurnTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_turn_is_accepted_through_the_inbox_and_its_reply_arrives_in_the_history_under_its_id()
    {
        using var factory = new ReplyingFactory();
        var api = factory.Api(factory.PhoneToken);

        var accepted = await api.SendTurnAsync("ea", new TurnRequestDto("what is on today?"), Ct);

        Assert.True(accepted.IsAccepted);
        var reply = await api.AwaitReplyAsync("ea", accepted.MessageId, null, TimeSpan.FromMilliseconds(50), 200, Ct);
        var line = Assert.Single(reply);
        Assert.Equal(("assistant", "reply to: what is on today?", accepted.MessageId), (line.Role, line.Content, line.InReplyTo));

        // The turn ran as the user's, in the main session; the user line carries the same id.
        var turn = Assert.Single(factory.Turns, t => t.Prompt == "what is on today?");
        Assert.Equal(MessageOrigin.User, turn.Origin);
        var history = await api.GetChatHistoryAsync("ea", 100, Ct);
        Assert.Contains(history, e => e is { Role: "user", Incognito: false } && e.MessageId == accepted.MessageId);

        // Persisted outside the agents' sandboxes.
        Assert.True(File.Exists(Path.Combine(factory.StateRoot, "chat", "ea.jsonl")));
    }

    [Fact]
    public async Task An_incognito_turn_runs_in_its_own_session_and_is_never_written_to_disk()
    {
        using var factory = new ReplyingFactory();
        var api = factory.Api(factory.PhoneToken);

        var accepted = await api.SendTurnAsync("ea", new TurnRequestDto("secret plans", Incognito: true, ConversationId: "c-1"), Ct);

        var reply = Assert.Single(await api.AwaitReplyAsync("ea", accepted.MessageId, null, TimeSpan.FromMilliseconds(50), 200, Ct));
        Assert.True(reply.Incognito);
        var chatFile = Path.Combine(factory.StateRoot, "chat", "ea.jsonl");
        Assert.False(File.Exists(chatFile) && (await File.ReadAllTextAsync(chatFile, Ct)).Contains("secret plans", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Turns_are_queued_in_the_inbox_as_user_messages_with_the_returned_id()
    {
        using var factory = new ApiFactory();
        var api = factory.Api(factory.PhoneToken);

        var plain = await api.SendTurnAsync("ea", new TurnRequestDto("hi"), Ct);
        var incognito = await api.SendTurnAsync("ea", new TurnRequestDto("psst", Incognito: true, ConversationId: "conv-7"), Ct);

        var queued = await factory.Services.GetRequiredService<IInboxStore>().ListAsync(Ea, Ct);
        var first = Assert.Single(queued, m => m.Id == plain.MessageId);
        Assert.Equal((MessageOrigin.User, (string?)null, "hi"), (first.Origin, first.SessionId, first.Body));
        var second = Assert.Single(queued, m => m.Id == incognito.MessageId);
        Assert.Equal(SessionIds.Incognito(Ea, "conv-7"), second.SessionId);
    }

    [Theory]
    [InlineData("""{"prompt":""}""")]
    [InlineData("""{"prompt":"x","incognito":true,"conversationId":"../../etc"}""")]
    [InlineData("""{"incognito":true}""")]
    public async Task Invalid_turns_are_refused(string body)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateDeviceClient(factory.PhoneToken);

        var response = await client.PostAsync(new Uri("/api/agents/ea/turn", UriKind.Relative), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<IInboxStore>().ListAsync(Ea, Ct));
    }

    [Fact]
    public async Task Unknown_agents_are_404_on_every_agent_route()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateDeviceClient(factory.PhoneToken);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(new Uri("/api/agents/nobody/turn", UriKind.Relative), new TurnRequestDto("hi"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri("/api/agents/nobody/history", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(new Uri("/api/agents/nobody/read", UriKind.Relative), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(new Uri("/api/agents/nobody/model", UriKind.Relative), new SetModelDto("gpt-5"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(new Uri("/api/agents/ea/model", UriKind.Relative), new SetModelDto("bad model!"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Agent_state_and_unread_come_from_the_activity_tracker_and_reading_clears_them()
    {
        using var factory = new ApiFactory();
        var api = factory.Api(factory.PhoneToken);
        var tracker = factory.Services.GetRequiredService<AgentActivityTracker>();
        await tracker.ApprovalPendingAsync("ea", Ct);

        Assert.Contains(await api.GetAgentsAsync(Ct), a => a is { Id: "ea", State: "needsAttention", Unread: 1 });

        await api.MarkAgentReadAsync("ea", Ct);

        Assert.Contains(await api.GetAgentsAsync(Ct), a => a is { Id: "ea", State: "idle", Unread: 0 });
    }
}
