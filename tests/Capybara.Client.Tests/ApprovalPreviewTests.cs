namespace Capybara.Client.Tests;

public sealed class ApprovalPreviewTests
{
    private static ApprovalDto Approval(string tool, string preview) => new("a1", "n", "ea", tool, "hash", preview, "High", false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Email_html_body_becomes_inert_text_and_counts_blocked_content()
    {
        var html = "<p>Hi <b>Sam</b></p><img src=\\\"http://evil.test/p.gif\\\"><a href=\\\"http://evil.test\\\">click</a><script>steal()</script>&amp; bye";
        var preview = ApprovalPreview.Parse(Approval("email_send", $$"""ea → email_send {"body":"{{html}}","subject":"Hello","to":"sam@example.com"}"""));

        Assert.Equal("email", preview.Kind);
        var email = preview.Email!;
        Assert.Equal("sam@example.com", email.To);
        Assert.Equal("Hello", email.Subject);
        Assert.Equal(1, email.RemoteImagesBlocked);
        Assert.Equal(1, email.LinksStripped);
        Assert.DoesNotContain('<', email.BodyText);
        Assert.DoesNotContain("evil.test", email.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("steal", email.BodyText, StringComparison.Ordinal);
        Assert.Contains("Hi Sam", email.BodyText, StringComparison.Ordinal);
        Assert.Contains("& bye", email.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_email_with_a_less_than_sign_is_left_alone()
    {
        var preview = ApprovalPreview.Parse(Approval("email_send", """ea → email_send {"body":"if a < b then go","subject":"s","to":"x@y.z"}"""));

        Assert.Equal("if a < b then go", preview.Email!.BodyText);
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
    }

    [Fact]
    public void Truncated_preview_falls_back_to_the_raw_text()
    {
        var preview = ApprovalPreview.Parse(Approval("email_send", """ea → email_send {"body":"cut off…"""));

        Assert.True(preview.Truncated);
        Assert.Empty(preview.Args);
        Assert.Contains("cut off", preview.Raw, StringComparison.Ordinal);
    }
}
