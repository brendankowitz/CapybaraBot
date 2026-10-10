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
    public void A_queued_turn_waits_for_the_reply_to_appear_in_the_history()
    {
        var cut = RenderChat();
        Gateway.Respond("POST", "/api/agents/ea/turn", """{"status":"Accepted","messageId":"m1"}""");
        var now = DateTimeOffset.UtcNow;
        ServeJson(History, new[] { new ChatEntryDto(now, "user", "do it"), new ChatEntryDto(now, "assistant", "queued reply") });

        Send(cut, "do it");

        cut.WaitForAssertion(() => Assert.Contains("queued reply", cut.FindAll(".chat-line.assistant pre").Select(p => p.TextContent)));
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
