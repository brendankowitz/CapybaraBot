using System.Net;
using Bunit;
using Capybara.Client;
using Capybara.Pwa.Pages;

namespace Capybara.Pwa.Tests;

public sealed class AgentChatTests : PwaTestContext
{
    private const string History = "/api/agents/ea/history?limit=100";

    private IRenderedComponent<AgentChat> RenderChat(params ChatEntryDto[] history)
    {
        ServeJson(History, history);
        Gateway.Respond("POST", "/api/agents/ea/read", string.Empty, HttpStatusCode.NoContent);
        var cut = Render<AgentChat>(p => p.Add(x => x.Id, "ea").Add(x => x.PollInterval, TimeSpan.FromMilliseconds(10)).Add(x => x.MaxPolls, 50));
        cut.WaitForAssertion(() => Assert.Contains(Gateway.Requests, r => r.Path == "/api/agents/ea/read"));
        return cut;
    }

    private static void Send(IRenderedComponent<AgentChat> cut, string text)
    {
        cut.Find("textarea").Change(text);
        cut.FindAll("button").Single(b => b.TextContent == "Send").Click();
    }

    [Fact]
    public void Loads_history_as_text_and_marks_the_agent_read()
    {
        var cut = RenderChat(new ChatEntryDto(DateTimeOffset.UtcNow, "assistant", "<img src=x onerror=alert(1)>"));

        Assert.Empty(cut.FindAll("img"));
        Assert.Contains("<img src=x onerror=alert(1)>", cut.Find(".chat-line pre").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_completed_turn_shows_the_reply()
    {
        var cut = RenderChat();
        Gateway.Respond("POST", "/api/agents/ea/turn", """{"status":"Completed","content":"all done"}""");

        Send(cut, "do it");

        cut.WaitForAssertion(() => Assert.Contains("all done", cut.Markup, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Blocked", "the kill switch is engaged")]
    [InlineData("OverBudget", "daily budget reached")]
    [InlineData("Throttled", "too many sessions")]
    public void A_turn_that_did_not_run_shows_its_status_and_reason(string status, string error)
    {
        var cut = RenderChat();
        Gateway.Respond("POST", "/api/agents/ea/turn", $$"""{"status":"{{status}}","content":null,"error":"{{error}}"}""");

        Send(cut, "do it");

        cut.WaitForAssertion(() => Assert.Contains($"{status}: {error}", cut.Find(".chat-line.status pre").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void A_queued_turn_shows_only_the_reply_correlated_to_its_message_id()
    {
        var cut = RenderChat();
        Gateway.Respond("POST", "/api/agents/ea/turn", """{"status":"Accepted","messageId":"m2"}""");
        var now = DateTimeOffset.UtcNow;

        // Same prompt twice, an agent-initiated message in between, and a two-part reply to the second one.
        ServeJson(History, new[]
        {
            new ChatEntryDto(now, "user", "do it", MessageId: "m1"),
            new ChatEntryDto(now, "assistant", "reply to the FIRST", InReplyTo: "m1"),
            new ChatEntryDto(now, "user", "do it", MessageId: "m2"),
            new ChatEntryDto(now, "assistant", "unprompted noise"),
            new ChatEntryDto(now, "assistant", "part one", InReplyTo: "m2"),
            new ChatEntryDto(now, "assistant", "part two", InReplyTo: "m2"),
        });

        Send(cut, "do it");

        cut.WaitForAssertion(() => Assert.Contains("part two", cut.FindAll(".chat-line.assistant pre").Select(p => p.TextContent)));
        var replies = cut.FindAll(".chat-line.assistant pre").Select(p => p.TextContent).ToList();
        Assert.Equal(["part one", "part two"], replies);
    }

    [Fact]
    public void Without_a_message_id_the_reply_is_found_after_the_snapshot_taken_before_sending()
    {
        var old = new ChatEntryDto(DateTimeOffset.UnixEpoch, "assistant", "old reply");
        var oldUser = new ChatEntryDto(DateTimeOffset.UnixEpoch, "user", "do it");
        var asJson = (ChatEntryDto[] entries) => System.Text.Json.JsonSerializer.Serialize(entries, Web);
        ServeJson(History, new[] { oldUser, old });
        Gateway.Respond("POST", "/api/agents/ea/read", string.Empty, HttpStatusCode.NoContent);
        Gateway.Respond("POST", "/api/agents/ea/turn", """{"status":"Accepted"}""");
        var cut = Render<AgentChat>(p => p.Add(x => x.Id, "ea").Add(x => x.PollInterval, TimeSpan.FromMilliseconds(10)).Add(x => x.MaxPolls, 50));
        cut.WaitForAssertion(() => Assert.Contains(Gateway.Requests, r => r.Path == "/api/agents/ea/read"));

        // History reads from here: the pre-send snapshot, a poll with no reply yet, then the reply (identical prompt as before).
        var fresh = new ChatEntryDto(DateTimeOffset.UnixEpoch, "user", "do it (joined)");
        Gateway.RespondSequence("GET", History, asJson([oldUser, old]), asJson([oldUser, old, fresh]), asJson([oldUser, old, fresh, new ChatEntryDto(DateTimeOffset.UnixEpoch, "assistant", "new reply")]));

        Send(cut, "do it");

        cut.WaitForAssertion(() => Assert.Contains("new reply", cut.FindAll(".chat-line.assistant pre").Select(p => p.TextContent)));
        // "old reply" is only the line loaded with the history; the new turn added exactly one reply.
        Assert.Equal(["old reply", "new reply"], cut.FindAll(".chat-line.assistant pre").Select(p => p.TextContent));
    }

    [Fact]
    public void A_queued_turn_with_no_reply_says_so_instead_of_inventing_one()
    {
        var cut = RenderChat();
        Gateway.Respond("POST", "/api/agents/ea/turn", """{"status":"Accepted","messageId":"m1"}""");
        ServeJson(History, new[] { new ChatEntryDto(DateTimeOffset.UtcNow, "assistant", "unprompted") });

        Send(cut, "do it");

        cut.WaitForAssertion(() => Assert.Contains("No reply yet", cut.Find(".chat-line.status pre").TextContent, StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("unprompted", cut.FindAll(".chat-line.assistant pre").Select(p => p.TextContent));
    }

    [Fact]
    public void A_gateway_error_on_send_is_shown()
    {
        var cut = RenderChat();
        Gateway.Respond("POST", "/api/agents/ea/turn", "agent runtime unavailable", HttpStatusCode.ServiceUnavailable);

        Send(cut, "do it");

        cut.WaitForAssertion(() => Assert.Contains("503", cut.Find("p.error").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void Incognito_turns_carry_the_flag_and_a_conversation_id()
    {
        var cut = RenderChat();
        Gateway.Respond("POST", "/api/agents/ea/turn", """{"status":"Completed","content":"ok"}""");
        cut.Find("input[type=checkbox]").Change(true);

        Send(cut, "secret");

        cut.WaitForAssertion(() =>
        {
            var body = Gateway.Requests.Single(r => r.Path == "/api/agents/ea/turn").Body!;
            Assert.Contains("\"incognito\":true", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"conversationId\":null", body, StringComparison.Ordinal);
        });
    }
}
