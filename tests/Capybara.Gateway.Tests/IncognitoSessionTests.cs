using System.Net;
using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Gateway.Chat;
using Capybara.Orchestration.Inbox;
using Microsoft.Extensions.DependencyInjection;

namespace Capybara.Gateway.Tests;

/// <summary>Review fix I1: incognito CLI sessions (the transcripts) end with their conversation (FR-MEM-12).</summary>
public sealed class IncognitoSessionTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_one_shot_incognito_turn_without_a_conversation_id_ends_its_session_after_the_turn()
    {
        using var factory = new ReplyingFactory();
        var api = factory.Api(factory.PhoneToken);

        var accepted = await api.SendTurnAsync("ea", new TurnRequestDto("whisper", Incognito: true), Ct);

        Assert.Single(await api.AwaitReplyAsync("ea", accepted.MessageId, null, TimeSpan.FromMilliseconds(50), 200, Ct));
        var turn = Assert.Single(factory.Turns, t => t.Prompt == "whisper");
        var session = turn.Target.SessionIdFor(Ea);
        Assert.True(SessionIds.TryParseIncognito(session, out _, out _));
        await Eventually.TrueAsync(() => factory.Ended.Contains(session), "the one-shot session is deleted");
        Assert.Empty(factory.IncognitoSessions);
    }

    [Fact]
    public async Task A_named_conversation_lives_until_it_is_idle_or_ended_by_the_user()
    {
        using var factory = new ReplyingFactory();
        var api = factory.Api(factory.PhoneToken);
        var options = factory.Services.GetRequiredService<IncognitoOptions>();
        var session = SessionIds.Incognito(Ea, "conv-1");

        var first = await api.SendTurnAsync("ea", new TurnRequestDto("one", Incognito: true, ConversationId: "conv-1"), Ct);
        Assert.Single(await api.AwaitReplyAsync("ea", first.MessageId, null, TimeSpan.FromMilliseconds(50), 200, Ct));
        Assert.True(factory.IncognitoSessions.ContainsKey(session));

        // Kept while used; deleted by a sweep once idle for the timeout, and not before.
        var advanced = TimeSpan.Zero;
        await Eventually.TrueAsync(
            () =>
            {
                factory.Time.Advance(TimeSpan.FromMinutes(1));
                advanced += TimeSpan.FromMinutes(1);
                return !factory.IncognitoSessions.ContainsKey(session);
            },
            "the idle session is deleted");
        Assert.True(advanced >= options.IdleTimeout, $"deleted after {advanced}");

        // A user ends a conversation explicitly.
        var second = await api.SendTurnAsync("ea", new TurnRequestDto("two", Incognito: true, ConversationId: "conv-2"), Ct);
        Assert.Single(await api.AwaitReplyAsync("ea", second.MessageId, null, TimeSpan.FromMilliseconds(50), 200, Ct));
        await api.EndIncognitoConversationAsync("ea", "conv-2", Ct);
        await Eventually.TrueAsync(() => !factory.IncognitoSessions.ContainsKey(SessionIds.Incognito(Ea, "conv-2")), "the ended conversation is deleted");
    }

    [Fact]
    public async Task Sessions_left_from_before_the_start_are_deleted_unless_a_message_for_them_is_still_queued()
    {
        using var factory = new ReplyingFactory();
        var leftover = SessionIds.Incognito(Ea, "old");
        var waiting = SessionIds.Incognito(Ea, "waiting");
        factory.IncognitoSessions[leftover] = factory.Time.GetUtcNow().AddHours(-3);
        factory.IncognitoSessions[waiting] = factory.Time.GetUtcNow().AddHours(-3);

        // A message for "waiting" is still in the durable inbox, and its turn cannot finish yet.
        factory.TurnGate = new TaskCompletionSource();
        var inbox = new FileInboxStore(Path.Combine(factory.StateRoot, "inbox"), factory.Time);
        await inbox.EnqueueAsync(new FleetMessage { To = Ea, Origin = MessageOrigin.User, Body = "still here", SessionId = waiting, CreatedAt = factory.Time.GetUtcNow() }, Ct);

        _ = factory.CreateClient();

        await Eventually.TrueAsync(() => factory.Ended.Contains(leftover), "the leftover session is deleted at start");
        Assert.DoesNotContain(waiting, factory.Ended);
        factory.TurnGate.SetResult();
    }

    [Theory]
    [InlineData("nobody", "conv", HttpStatusCode.NotFound)]
    [InlineData("ea", "bad.id", HttpStatusCode.BadRequest)]
    public async Task Ending_a_conversation_validates_the_agent_and_id(string agent, string conversation, HttpStatusCode expected)
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateDeviceClient(factory.PhoneToken).DeleteAsync(new Uri($"/api/agents/{agent}/incognito/{conversation}", UriKind.Relative), Ct);

        Assert.Equal(expected, response.StatusCode);
    }
}
