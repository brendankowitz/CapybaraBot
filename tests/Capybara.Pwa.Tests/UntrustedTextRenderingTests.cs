using System.Net;
using Bunit;
using Capybara.Client;
using Capybara.Pwa.Pages;

namespace Capybara.Pwa.Tests;

/// <summary>Agent- and memory-supplied text must reach the DOM as text nodes, never as markup (FR-SAF-07).</summary>
public sealed class UntrustedTextRenderingTests : PwaTestContext
{
    private const string Hostile = "<img src=\"http://evil.test/x.gif\"><a href=\"http://evil.test\">go</a><script>alert(1)</script>";

    private static void AssertInert<T>(IRenderedComponent<T> cut)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        Assert.Empty(cut.FindAll("img"));
        Assert.Empty(cut.FindAll("a"));
        Assert.Empty(cut.FindAll("script"));
    }

    [Fact]
    public void Proposal_diff_and_rationale_are_plain_text()
    {
        var proposal = new ProposalDto("p1", "ea", "Soul", null, "agents/ea/SOUL.md", "--- a\n+++ b\n@@ -1 +1 @@\n-" + Hostile + "\n+new", "why " + Hostile, "Pending", DateTimeOffset.UtcNow);
        ServeJson("/api/proposals", new[] { proposal });

        var cut = Render<Proposals>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("pre.diff")));
        AssertInert(cut);
        Assert.Contains("<script>alert(1)</script>", cut.Find(".diff-del").TextContent, StringComparison.Ordinal);
        Assert.Contains("why <img", cut.Find("article").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Proposal_decision_outcomes_are_visible_after_reload()
    {
        var pending = new ProposalDto("p1", "ea", "Soul", null, "x", "d", "r", "Pending", DateTimeOffset.UtcNow);
        ServeJson("/api/proposals", new[] { pending });
        Gateway.Respond("POST", "/api/proposals/p1", """{"id":"p1","agent":"ea","kind":"Soul","targetPath":"x","diff":"d","rationale":"r","status":"Stale","createdAt":"2026-01-01T00:00:00Z"}""");
        var cut = Render<Proposals>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article")));
        ServeJson("/api/proposals", new[] { pending with { Status = "Stale", Error = "target changed" } });

        cut.FindAll("button").Single(b => b.TextContent == "Approve").Click();

        cut.WaitForAssertion(() => Assert.Contains("target changed", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void Memory_pending_changes_and_pages_are_plain_text()
    {
        ServeJson("/api/memory/pages?prefix=", new[] { new MemoryPageInfoDto("/agents/ea/" + Hostile + ".md", 12) });
        ServeJson("/api/memory/pending", new[] { new PendingMemoryChangeDto("c1", "/a.md", "ea " + Hostile, "before " + Hostile, "after " + Hostile, DateTimeOffset.UtcNow, "Write", "why " + Hostile) });

        var cut = Render<Memory>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article")));
        AssertInert(cut);
        Assert.Contains("after <img", cut.FindAll("article pre")[1].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_a_memory_page_puts_its_content_in_the_editor_as_text()
    {
        ServeJson("/api/memory/pages?prefix=", new[] { new MemoryPageInfoDto("/a.md", 1) });
        ServeJson("/api/memory/pending", Array.Empty<PendingMemoryChangeDto>());
        ServeJson("/api/memory/page?path=%2Fa.md", new MemoryPageDto("/a.md", Hostile));
        var cut = Render<Memory>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("li button")));

        cut.Find("li button").Click();

        cut.WaitForAssertion(() => Assert.Contains("evil.test", cut.Find("textarea").GetAttribute("value") ?? cut.Find("textarea").TextContent, StringComparison.Ordinal));
        AssertInert(cut);
    }

    [Fact]
    public void Question_body_and_choices_are_plain_text()
    {
        ServeJson("/api/questions/q1", new QuestionDto("q1", "ea", "Pick " + Hostile, "Body " + Hostile, ["yes " + Hostile, "no"]));

        var cut = Render<Questions>(p => p.Add(x => x.Id, "q1"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article")));
        AssertInert(cut);
        Assert.Contains("Body <img", cut.Find("article pre").TextContent, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll("article button").Count);
    }

    [Fact]
    public void A_gone_question_says_it_is_no_longer_pending()
    {
        Gateway.Respond("GET", "/api/questions/q9", "gone", HttpStatusCode.NotFound);

        var cut = Render<Questions>(p => p.Add(x => x.Id, "q9"));

        cut.WaitForAssertion(() => Assert.Contains("no longer pending", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("p.error"));
    }

    [Fact]
    public void Answering_a_question_posts_the_choice()
    {
        ServeJson("/api/questions/q1", new QuestionDto("q1", "ea", "t", "b", ["yes", "no"]));
        Gateway.Respond("POST", "/api/questions/q1", string.Empty, HttpStatusCode.NoContent);
        var cut = Render<Questions>(p => p.Add(x => x.Id, "q1"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article button")));

        cut.FindAll("article button").Single(b => b.TextContent == "yes").Click();

        cut.WaitForAssertion(() => Assert.Contains("Answer sent", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("\"answer\":\"yes\"", Gateway.Requests.Single(r => r.Method == "POST").Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Fleet_thread_summaries_are_plain_text()
    {
        ServeJson("/api/thread", new[] { new ThreadEntryDto(DateTimeOffset.UtcNow, "ea", "dev", "Delegate", Hostile, "t", 1, "Tainted", false) });

        var cut = Render<Pages.Thread>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article")));
        AssertInert(cut);
        Assert.Contains("<script>alert(1)</script>", cut.Find("article pre").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_details_are_plain_text()
    {
        ServeJson("/api/audit?limit=100", new[] { new AuditEntryDto(1, DateTimeOffset.UtcNow, "approval", "allow", "ea", "email_send", Hostile) });
        ServeJson("/api/audit/verify", new AuditVerifyDto(true));

        var cut = Render<Audit>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("table.audit")));
        AssertInert(cut);
    }
}
