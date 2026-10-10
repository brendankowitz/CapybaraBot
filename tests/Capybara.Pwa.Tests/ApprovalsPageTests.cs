using System.Text.Json;
using Bunit;
using Capybara.Client;
using Capybara.Pwa.Pages;

namespace Capybara.Pwa.Tests;

public sealed class ApprovalsPageTests : PwaTestContext
{
    private static ApprovalDto Approval(string id, string tool, object args, string risk = "High", string? rationale = "sends email externally", string? argsJson = null, string? preview = null) => new(
        id, "nonce-" + id, "ea", tool, "hash", preview ?? $"ea → {tool} {JsonSerializer.Serialize(args)}", risk, risk != "High", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10), "escalate", rationale, ArgsJson: argsJson);

    private void Serve(params ApprovalDto[] approvals) => ServeJson("/api/approvals", approvals);

    private IRenderedComponent<Approvals> RenderLoaded()
    {
        var cut = Render<Approvals>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article.approval")));
        return cut;
    }

    private static bool IsDisabled(IRenderedComponent<Approvals> cut, string label) => cut.FindAll("button").Single(b => b.TextContent == label).HasAttribute("disabled");

    [Fact]
    public void Email_preview_renders_no_remote_content_but_lists_every_url_as_text()
    {
        var html = "<p>Hello <b>Sam</b></p><img src=\"http://tracker.evil.test/p.gif\"><a href=\"http://phish.evil.test/login\">your invoice</a><script>alert(1)</script>";
        Serve(Approval("a1", "email_send", new { to = "sam@example.com", subject = "Invoice", body = html }));

        var cut = RenderLoaded();

        Assert.Empty(cut.FindAll("img"));
        Assert.Empty(cut.FindAll("a"));
        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("sam@example.com", cut.Find(".email-preview dd").TextContent, StringComparison.Ordinal);
        Assert.Contains("Hello Sam", cut.Find(".email-body").TextContent, StringComparison.Ordinal);
        var references = cut.Find(".email-references").TextContent;
        Assert.Contains("http://tracker.evil.test/p.gif", references, StringComparison.Ordinal);
        Assert.Contains("http://phish.evil.test/login", references, StringComparison.Ordinal);
        Assert.Contains("alert(1)", cut.Find(".hidden-content-text").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("p.hidden-content"));
    }

    [Fact]
    public void Urls_in_plain_text_are_not_turned_into_links_or_images()
    {
        Serve(Approval("a1", "email_send", new { to = "sam@example.com", subject = "Hi", body = "see https://example.com/a.png and ![x](https://example.com/b.png)" }));

        var cut = RenderLoaded();

        Assert.Empty(cut.FindAll("img"));
        Assert.Empty(cut.FindAll("a"));
        Assert.Contains("https://example.com/a.png", cut.Find(".email-body").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Bidi_and_zero_width_characters_are_shown_as_tokens_everywhere_on_the_card()
    {
        Serve(Approval("v1", "calendar_create", new { title = "pay\u202Eenoyna", note = "a\u200Bb" }, rationale: "ok\u2066?", risk: "Low"));

        var cut = RenderLoaded();

        var text = cut.Find("article").TextContent;
        Assert.DoesNotContain('\u202E', text);
        Assert.DoesNotContain('\u200B', text);
        Assert.DoesNotContain('\u2066', text);
        Assert.Contains("pay⟦U+202E⟧enoyna", cut.FindAll("table.args td code")[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("a⟦U+200B⟧b", cut.FindAll("table.args td code")[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("ok⟦U+2066⟧?", cut.Find("p.reviewer").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("p.notice"));
        Assert.False(IsDisabled(cut, "Allow once"));
    }

    [Fact]
    public void The_exact_arguments_block_is_revealed_when_the_server_sends_raw_characters()
    {
        var raw = "{\"note\":\"a\u202Eb\u200Bc\"}";
        Serve(Approval("v6", "calendar_create", new { }, risk: "Low", argsJson: raw, preview: "ea → calendar_create"));

        var cut = RenderLoaded();

        var exact = cut.Find("pre.exact-args").TextContent;
        Assert.Equal("{\"note\":\"a⟦U+202E⟧b⟦U+200B⟧c\"}", exact);
    }

    [Fact]
    public void A_bidi_override_in_the_recipient_is_visible_and_blocks_allow()
    {
        Serve(Approval("v2", "email_send", new { to = "sam@example.com\u202Egro.live", subject = "Su\u202Ebject", body = "b" }, risk: "Low"));

        var cut = RenderLoaded();

        Assert.Equal("sam@example.com⟦U+202E⟧gro.live", cut.Find("dd.recipient").TextContent);
        Assert.Contains("Su⟦U+202E⟧bject", cut.FindAll(".email-preview dd")[1].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain('\u202E', cut.Find("article").TextContent);
        Assert.True(IsDisabled(cut, "Allow once"));
        Assert.False(IsDisabled(cut, "Deny"));
        Assert.Contains("recipient (to)", cut.Find("p.block-reason").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Look_alike_letters_in_a_recipient_show_their_code_points()
    {
        Serve(Approval("v3", "email_send", new { to = "sam@\u0456nternal.com", subject = "s", body = "b" }, risk: "Low"));

        var cut = RenderLoaded();

        Assert.Equal("sam@⟦U+0456⟧nternal.com", cut.Find("dd.recipient").TextContent);
        Assert.Contains("non-ASCII", cut.Find("p.notice").TextContent, StringComparison.Ordinal);
        Assert.False(IsDisabled(cut, "Allow once"));
    }

    [Fact]
    public void A_zero_width_key_that_shadows_the_recipient_is_flagged_and_blocks_allow()
    {
        Serve(Approval("v4", "email_send", new { }, risk: "Low", preview: "ea → email_send {\"to\":\"good@example.com\",\"t\\u200Bo\":\"evil@example.net\",\"subject\":\"s\",\"body\":\"b\"}"));

        var cut = RenderLoaded();

        Assert.True(IsDisabled(cut, "Allow once"));
        Assert.Contains("ambiguous", cut.Find("p.block-reason").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\u200B', cut.Find("article").TextContent);
        Assert.Contains("U+200B", cut.Find("article").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_text_is_revealed_too()
    {
        Serve(Approval("v5", "github_commit_file", new { patch = "--- a\n+++ b\n@@ -1 +1 @@\n-x\n+pay\u202Eenoyna" }, risk: "Medium"));

        var cut = RenderLoaded();

        Assert.DoesNotContain('\u202E', cut.Find("pre.diff").TextContent);
        Assert.Contains("pay⟦U+202E⟧enoyna", cut.Find(".diff-add").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_arguments_are_always_available()
    {
        Serve(Approval("a1", "email_send", new { to = "sam@example.com", subject = "Hi", body = "<b>x</b>" }));

        var cut = RenderLoaded();

        Assert.Contains("sam@example.com", cut.Find("details pre.exact-args").TextContent, StringComparison.Ordinal);
        Assert.Contains("\\u003Cb\\u003Ex", cut.Find("details pre.exact-args").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_approvals_render_a_diff_view_as_text_and_other_args_in_a_table()
    {
        Serve(Approval("a2", "github_commit_file", new { branch = "fix", patch = "--- a\n+++ b\n@@ -1 +1 @@\n-old <b>x</b>\n+new" }, risk: "Medium"));

        var cut = RenderLoaded();

        Assert.Single(cut.FindAll(".diff-add"));
        Assert.Single(cut.FindAll(".diff-del"));
        Assert.Empty(cut.FindAll("pre.diff b"));
        Assert.Contains("-old <b>x</b>", cut.Find(".diff-del").TextContent, StringComparison.Ordinal);
        Assert.Equal("branch", cut.Find("table.args tbody th").TextContent);
        Assert.Contains("Always", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_tools_show_an_args_table_and_high_risk_hides_always()
    {
        Serve(Approval("a3", "calendar_create", new { title = "Sync", attendees = "a@x.y,b@x.y" }));

        var cut = RenderLoaded();

        Assert.Equal(2, cut.FindAll("table.args tbody tr").Count);
        Assert.DoesNotContain("Always", cut.FindAll("button").Select(b => b.TextContent));
        Assert.Contains("Reviewer:", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_in_an_argument_value_or_reviewer_rationale_is_text()
    {
        Serve(Approval("a4", "calendar_create", new { title = "<img src=x onerror=alert(1)>", note = "<a href='http://evil.test'>x</a>" }, rationale: "<b onmouseover=x>looks fine</b><img src=http://evil.test/r.gif>"));

        var cut = RenderLoaded();

        Assert.Empty(cut.FindAll("img"));
        Assert.Empty(cut.FindAll("a"));
        Assert.Single(cut.FindAll("article b")); // only the reviewer verdict label, not the injected <b>
        Assert.Equal("<img src=x onerror=alert(1)>", cut.FindAll("table.args td code")[0].TextContent);
        Assert.Contains("<b onmouseover=x>looks fine</b>", cut.Find("p.reviewer").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cut_off_preview_can_only_be_denied()
    {
        Serve(Approval("a5", "email_send", new { }, risk: "Low", preview: """ea → email_send {"body":"a very long body that was cut…"""));

        var cut = RenderLoaded();

        Assert.True(IsDisabled(cut, "Allow once"));
        Assert.True(IsDisabled(cut, "Always"));
        Assert.False(IsDisabled(cut, "Deny"));
        Assert.Contains("cut off", cut.Find("p.block-reason").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("details").HasAttribute("open"));
    }

    [Fact]
    public void Full_args_from_the_server_make_a_cut_preview_approvable()
    {
        var args = JsonSerializer.Serialize(new { body = new string('x', 3_000), subject = "s", to = "attacker@evil.test" });
        Serve(Approval("a6", "email_send", new { }, risk: "Low", preview: "ea → email_send {\"body\":\"xxxx…", argsJson: args));

        var cut = RenderLoaded();

        Assert.False(IsDisabled(cut, "Allow once"));
        Assert.Equal("attacker@evil.test", cut.Find(".email-preview dd").TextContent);
    }

    [Fact]
    public void Case_variant_duplicate_keys_do_not_crash_the_page_and_block_allow()
    {
        Serve(
            Approval("ok", "calendar_create", new { title = "Sync" }, risk: "Low"),
            Approval("dup", "calendar_create", new { }, risk: "Low", preview: """ea → calendar_create {"To":"a@x.y","to":"b@x.y"}"""));

        var cut = Render<Approvals>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("article.approval").Count));
        var dup = cut.FindAll("article.approval")[1];
        Assert.Equal(2, dup.QuerySelectorAll("tr.conflict").Length);
        Assert.True(dup.QuerySelectorAll("button").Single(b => b.TextContent == "Allow once").HasAttribute("disabled"));
        Assert.False(cut.FindAll("article.approval")[0].QuerySelectorAll("button").Single(b => b.TextContent == "Allow once").HasAttribute("disabled"));
    }

    [Fact]
    public void Deciding_posts_the_nonce_and_decision_then_reloads()
    {
        Serve(Approval("a7", "calendar_create", new { title = "Sync" }, risk: "Low"));
        Gateway.Respond("POST", "/api/approvals/a7", string.Empty, System.Net.HttpStatusCode.NoContent);
        var cut = RenderLoaded();

        cut.FindAll("button").Single(b => b.TextContent == "Allow once").Click();

        cut.WaitForAssertion(() =>
        {
            var post = Assert.Single(Gateway.Requests, r => r.Method == "POST");
            using var body = JsonDocument.Parse(post.Body!);
            Assert.Equal("nonce-a7", body.RootElement.GetProperty("nonce").GetString());
            Assert.Equal("AllowOnce", body.RootElement.GetProperty("decision").GetString());
        });
    }

    [Fact]
    public void A_settled_approval_shows_the_gateway_error()
    {
        Serve(Approval("a8", "calendar_create", new { title = "Sync" }, risk: "Low"));
        Gateway.Respond("POST", "/api/approvals/a8", "approval is no longer pending", System.Net.HttpStatusCode.Conflict);
        var cut = RenderLoaded();

        cut.FindAll("button").Single(b => b.TextContent == "Deny").Click();

        cut.WaitForAssertion(() => Assert.Contains("no longer pending", cut.Find("p.error").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void Gateway_errors_are_shown_not_swallowed()
    {
        // No route registered: the fake answers 404, which must not look like an empty inbox.
        var cut = Render<Approvals>();

        cut.WaitForAssertion(() => Assert.Contains("404", cut.Find("p.error").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("Nothing is waiting", cut.Markup, StringComparison.Ordinal);
    }
}
