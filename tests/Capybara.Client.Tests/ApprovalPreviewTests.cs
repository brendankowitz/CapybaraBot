using System.Diagnostics;

namespace Capybara.Client.Tests;

public sealed class ApprovalPreviewTests
{
    private static ApprovalDto Approval(string tool, string preview, string? argsJson = null) =>
        new("a1", "n", "ea", tool, "hash", preview, "High", false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ArgsJson: argsJson);

    private static string EmailArgs(string body, string to = "sam@example.com") =>
        System.Text.Json.JsonSerializer.Serialize(new SortedDictionary<string, string> { ["body"] = body, ["subject"] = "Hello", ["to"] = to });

    private static ApprovalPreview Email(string body) => ApprovalPreview.Parse(Approval("email_send", $"ea → email_send {EmailArgs(body)}"));

    [Fact]
    public void Email_html_becomes_text_with_every_link_and_image_target_listed()
    {
        var preview = Email("<p>Hi <b>Sam</b></p><img src=\"http://tracker.evil.test/p.gif\"><a href=\"http://phish.evil.test/login\">your invoice</a>&amp; bye");

        Assert.Equal("email", preview.Kind);
        var email = preview.Email!;
        Assert.Equal("sam@example.com", email.To);
        Assert.Equal(1, email.RemoteImagesBlocked);
        Assert.Equal(1, email.LinksStripped);
        Assert.DoesNotContain('<', email.BodyText);
        Assert.Contains("Hi Sam", email.BodyText, StringComparison.Ordinal);
        Assert.Contains("your invoice", email.BodyText, StringComparison.Ordinal);
        Assert.Contains("& bye", email.BodyText, StringComparison.Ordinal);
        Assert.Contains("img src: http://tracker.evil.test/p.gif", email.References);
        Assert.Contains("a href: http://phish.evil.test/login", email.References);
        Assert.True(preview.CanApprove);
    }

    [Fact]
    public void Script_style_and_comment_content_is_surfaced_not_dropped()
    {
        var email = Email("Visible<script>exfiltrate('x')</script><style>.a{background:url(http://evil.test/s.png)}</style><!-- secret note -->").Email!;

        Assert.Contains("exfiltrate('x')", email.HiddenContent);
        Assert.Contains(email.HiddenContent, h => h.Contains("evil.test", StringComparison.Ordinal));
        Assert.Contains("secret note", email.HiddenContent);
        Assert.Equal("Visible", email.BodyText.Trim());
    }

    [Fact]
    public void An_unterminated_tag_does_not_swallow_the_text_after_it()
    {
        var email = Email("hello <b world, wire the money to account 123").Email!;

        Assert.Contains("wire the money to account 123", email.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Attribute_and_css_urls_are_listed()
    {
        var email = Email("<div style=\"background:url('http://evil.test/bg.png')\"><a href='http://evil.test/a'>x</a><img src=http://evil.test/c.gif></div>").Email!;

        Assert.Contains(email.References, r => r.Contains("http://evil.test/bg.png", StringComparison.Ordinal));
        Assert.Contains("a href: http://evil.test/a", email.References);
        Assert.Contains("img src: http://evil.test/c.gif", email.References);
    }

    [Fact]
    public void Plain_text_email_with_a_less_than_sign_is_left_alone()
    {
        var preview = Email("if a < b then go");

        Assert.Equal("if a < b then go", preview.Email!.BodyText);
        Assert.Empty(preview.Email.References);
    }

    [Fact]
    public void Exact_arguments_are_always_kept_verbatim()
    {
        var args = EmailArgs("<b>x</b>");

        var preview = ApprovalPreview.Parse(Approval("email_send", "ea → email_send " + args, argsJson: args));

        Assert.Equal(args, preview.Exact);
    }

    [Fact]
    public void Diff_argument_is_surfaced_as_a_diff()
    {
        var preview = ApprovalPreview.Parse(Approval("github_commit_file", """ea → github_commit_file {"branch":"x","patch":"--- a\n+++ b\n@@ -1 +1 @@\n-a\n+b"}"""));

        Assert.Equal("diff", preview.Kind);
        Assert.StartsWith("--- a", preview.Diff, StringComparison.Ordinal);
        Assert.Equal([new KeyValuePair<string, string>("branch", "x")], preview.Args);
    }

    [Fact]
    public void Other_tools_get_an_args_table_with_non_string_values_as_json()
    {
        var preview = ApprovalPreview.Parse(Approval("calendar_create", """ea → calendar_create {"attendees":["a","b"],"title":"Sync"}"""));

        Assert.Equal("args", preview.Kind);
        Assert.Equal("Sync", preview.Args.Single(a => a.Key == "title").Value);
        Assert.Equal("""["a","b"]""", preview.Args.Single(a => a.Key == "attendees").Value);
        Assert.True(preview.CanApprove);
    }

    [Fact]
    public void A_preview_cut_mid_json_cannot_be_approved()
    {
        var preview = ApprovalPreview.Parse(Approval("email_send", """ea → email_send {"body":"cut off…"""));

        Assert.True(preview.Truncated);
        Assert.False(preview.CanApprove);
        Assert.NotNull(preview.BlockReason);
        Assert.Empty(preview.Args);
        Assert.Contains("cut off", preview.Exact, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_args_json_makes_a_cut_preview_approvable_and_shows_the_recipient()
    {
        var args = EmailArgs(new string('x', 3_000), to: "attacker@evil.test");
        var cut = "ea → email_send " + args[..1_990] + "…";

        var preview = ApprovalPreview.Parse(Approval("email_send", cut, argsJson: args));

        Assert.False(preview.Truncated);
        Assert.True(preview.CanApprove);
        Assert.Equal("attacker@evil.test", preview.Email!.To);
    }

    [Fact]
    public void Case_variant_duplicate_keys_do_not_throw_and_block_approval()
    {
        var preview = ApprovalPreview.Parse(Approval("email_send", """ea → email_send {"To":"attacker@evil.test","subject":"s","to":"sam@example.com"}"""));

        Assert.False(preview.CanApprove);
        Assert.Contains("to", preview.ConflictingKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ambiguous", preview.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exact_duplicate_json_keys_are_flagged_too()
    {
        var preview = ApprovalPreview.Parse(Approval("calendar_create", "x", argsJson: """{"title":"a","title":"b"}"""));

        Assert.False(preview.CanApprove);
        Assert.Equal(2, preview.Args.Count(a => a.Key == "title"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{broken")]
    public void Invalid_full_args_degrade_to_the_exact_text_and_block_approval(string argsJson)
    {
        var preview = ApprovalPreview.Parse(Approval("email_send", "ea → email_send {}", argsJson));

        Assert.True(preview.ParseFailed);
        Assert.False(preview.CanApprove);
        Assert.Equal(argsJson, preview.Exact);
    }

    [Fact]
    public void A_preview_without_arguments_cannot_be_approved()
    {
        var preview = ApprovalPreview.Parse(Approval("email_send", "ea → email_send"));

        Assert.False(preview.CanApprove);
    }

    [Fact]
    public void Hostile_markup_is_processed_quickly_and_never_throws()
    {
        var hostile = string.Concat(Enumerable.Repeat("<script <!-- <a href=\"", 400)) + new string('<', 5_000);
        var clock = Stopwatch.StartNew();

        var preview = ApprovalPreview.Parse(Approval("email_send", "x", argsJson: EmailArgs(hostile)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.NotNull(preview.Exact);
    }
}
