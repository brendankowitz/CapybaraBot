using System.Text.Json;
using Bunit;
using Capybara.Client;
using Capybara.Pwa.Pages;

namespace Capybara.Pwa.Tests;

public sealed class ApprovalsPageTests : PwaTestContext
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ApprovalDto Approval(string id, string tool, object args, string risk = "High") => new(
        id, "nonce-" + id, "ea", tool, "hash", $"ea → {tool} {JsonSerializer.Serialize(args)}", risk, risk != "High", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10), "escalate", "sends email externally");

    private void Serve(params ApprovalDto[] approvals) => Gateway.Respond("GET", "/api/approvals", JsonSerializer.Serialize(approvals, Web));

    [Fact]
    public void Email_preview_never_renders_remote_images_links_or_scripts()
    {
        var html = "<p>Hello <b>Sam</b></p><img src=\"http://tracker.evil.test/p.gif\"><a href=\"http://phish.evil.test/login\">your invoice</a><script>alert(1)</script>";
        Serve(Approval("a1", "email_send", new { to = "sam@example.com", subject = "Invoice", body = html }));

        var cut = Render<Approvals>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".email-preview")));
        Assert.Empty(cut.FindAll("img"));
        Assert.Empty(cut.FindAll("a"));
        Assert.Empty(cut.FindAll("script"));
        Assert.DoesNotContain("evil.test", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("sam@example.com", cut.Find(".email-preview dd").TextContent, StringComparison.Ordinal);
        Assert.Contains("Hello Sam", cut.Find(".email-body").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 remote image(s)", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Urls_in_plain_text_are_not_turned_into_links_or_images()
    {
        Serve(Approval("a1", "email_send", new { to = "sam@example.com", subject = "Hi", body = "see https://example.com/a.png and ![x](https://example.com/b.png)" }));

        var cut = Render<Approvals>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".email-body")));
        Assert.Empty(cut.FindAll("img"));
        Assert.Empty(cut.FindAll("a"));
        Assert.Contains("https://example.com/a.png", cut.Find(".email-body").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_approvals_render_a_diff_view_as_text_and_other_args_in_a_table()
    {
        Serve(Approval("a2", "github_commit_file", new { branch = "fix", patch = "--- a\n+++ b\n@@ -1 +1 @@\n-old <b>x</b>\n+new" }, risk: "Medium"));

        var cut = Render<Approvals>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("pre.diff")));
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

        var cut = Render<Approvals>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("table.args tbody tr").Count));
        Assert.DoesNotContain("Always", cut.FindAll("button").Select(b => b.TextContent));
        Assert.Contains("Reviewer:", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Deciding_posts_the_nonce_and_decision_then_reloads()
    {
        Serve(Approval("a4", "calendar_create", new { title = "Sync" }, risk: "Low"));
        Gateway.Respond("POST", "/api/approvals/a4", string.Empty, System.Net.HttpStatusCode.NoContent);
        var cut = Render<Approvals>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article.approval")));

        cut.FindAll("button").Single(b => b.TextContent == "Allow once").Click();

        cut.WaitForAssertion(() =>
        {
            var post = Assert.Single(Gateway.Requests, r => r.Method == "POST");
            using var body = JsonDocument.Parse(post.Body!);
            Assert.Equal("nonce-a4", body.RootElement.GetProperty("nonce").GetString());
            Assert.Equal("AllowOnce", body.RootElement.GetProperty("decision").GetString());
        });
    }

    [Fact]
    public void A_settled_approval_shows_the_gateway_error()
    {
        Serve(Approval("a5", "calendar_create", new { title = "Sync" }, risk: "Low"));
        Gateway.Respond("POST", "/api/approvals/a5", "approval is no longer pending", System.Net.HttpStatusCode.Conflict);
        var cut = Render<Approvals>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article.approval")));

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
