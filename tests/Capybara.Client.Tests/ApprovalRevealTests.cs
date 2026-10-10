namespace Capybara.Client.Tests;

public sealed class ApprovalRevealTests
{
    private static ApprovalDto Approval(string tool, string argsJson) =>
        new("a1", "n", "ea", tool, "hash", "ea → " + tool, "High", false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ArgsJson: argsJson);

    private static string Args(params (string Key, string Value)[] pairs) =>
        "{" + string.Join(",", pairs.Select(p => $"\"{p.Key}\":\"{p.Value}\"")) + "}";

    [Theory]
    [InlineData("a\u202Eb", "a⟦U+202E⟧b")]
    [InlineData("a\u2066b\u2069", "a⟦U+2066⟧b⟦U+2069⟧")]
    [InlineData("a\u200Bb\u200Dc\uFEFFd", "a⟦U+200B⟧b⟦U+200D⟧c⟦U+FEFF⟧d")]
    [InlineData("x\u0007y\u001bz", "x⟦U+0007⟧y⟦U+001B⟧z")]
    [InlineData("a\u2028b\u2029c", "a⟦U+2028⟧b⟦U+2029⟧c")]
    [InlineData("a\u00A0b\u3000c", "a⟦U+00A0⟧b⟦U+3000⟧c")]
    [InlineData("a\uFE0Fb", "a⟦U+FE0F⟧b")]
    [InlineData("a\U000E0041b", "a⟦U+E0041⟧b")]
    [InlineData("keep\nnewline\tand tab", "keep\nnewline\tand tab")]
    [InlineData("crlf\r\nline", "crlf\nline")]
    [InlineData("lone\rcr", "lone⟦U+000D⟧cr")]
    [InlineData("plain ascii and é ü 日本", "plain ascii and é ü 日本")]
    [InlineData(null, "")]
    public void Reveal_replaces_every_invisible_character_with_a_visible_token(string? input, string expected) =>
        Assert.Equal(expected, ApprovalPreview.Reveal(input));

    [Fact]
    public void Reveal_shows_unpaired_surrogates()
    {
        // Built at run time: a lone surrogate cannot survive test-data serialization.
        var lone = "a" + (char)0xD800 + "b" + (char)0xDC00 + "c";
        var pair = "x\U0001F44Dy";

        Assert.Equal("a⟦U+D800⟧b⟦U+DC00⟧c", ApprovalPreview.Reveal(lone));
        Assert.Equal(pair, ApprovalPreview.Reveal(pair));
    }

    [Fact]
    public void Reveal_output_contains_no_invisible_characters_for_any_basic_plane_input()
    {
        var everything = string.Concat(Enumerable.Range(0, 0x10000).Select(i => (char)i));

        var revealed = ApprovalPreview.Reveal(everything);

        // Revealing is idempotent: nothing is left that Reveal would still tokenize.
        Assert.False(ApprovalPreview.ContainsInvisible(revealed));
        Assert.Equal(revealed, ApprovalPreview.Reveal(revealed));
    }

    [Fact]
    public void RevealNonAscii_shows_look_alike_letters_as_code_points()
    {
        // U+0456 is Cyrillic "і", identical to Latin "i" on screen.
        Assert.Equal("sam@example.com", ApprovalPreview.RevealNonAscii("sam@example.com"));
        Assert.Equal("sam@⟦U+0456⟧nternal.com", ApprovalPreview.RevealNonAscii("sam@\u0456nternal.com"));
    }

    [Fact]
    public void A_bidi_override_in_the_recipient_blocks_approval()
    {
        var preview = ApprovalPreview.Parse(Approval("email_send", Args(("to", "sam@example.com\u202Egro.live"), ("subject", "s"), ("body", "b"))));

        Assert.False(preview.CanApprove);
        Assert.Contains(preview.UnsafeFields, f => f.Contains("recipient (to)", StringComparison.Ordinal));
        Assert.DoesNotContain('\u202E', preview.BlockReason!);
    }

    [Fact]
    public void Invisible_characters_in_cc_block_approval()
    {
        var preview = ApprovalPreview.Parse(Approval("email_send", Args(("to", "a@x.y"), ("cc", "b\u200B@x.y"), ("subject", "s"), ("body", "b"))));

        Assert.Contains(preview.UnsafeFields, f => f.Contains("(cc)", StringComparison.Ordinal));
        Assert.False(preview.CanApprove);
    }

    [Fact]
    public void A_zero_width_key_collides_with_the_real_key_and_blocks()
    {
        var preview = ApprovalPreview.Parse(Approval("email_send", Args(("to", "good@example.com"), ("t\u200Bo", "evil@example.net"), ("subject", "s"), ("body", "b"))));

        Assert.False(preview.CanApprove);
        Assert.Equal(2, preview.ConflictingKeys.Count);
        Assert.Contains(preview.UnsafeFields, f => f.Contains("U+200B", StringComparison.Ordinal));
    }

    [Fact]
    public void A_fullwidth_key_is_folded_onto_the_real_key()
    {
        // NFKC maps fullwidth "ｔｏ" to "to".
        var preview = ApprovalPreview.Parse(Approval("email_send", Args(("to", "good@example.com"), ("\uFF54\uFF4F", "evil@example.net"), ("subject", "s"), ("body", "b"))));

        Assert.False(preview.CanApprove);
        Assert.Equal(2, preview.ConflictingKeys.Count);
    }

    [Fact]
    public void A_look_alike_key_blocks_even_though_no_folding_equates_it()
    {
        // Cyrillic "о" in "tо": not a compatibility variant of Latin "o", so only the non-ASCII-name rule catches it.
        var preview = ApprovalPreview.Parse(Approval("email_send", Args(("to", "good@example.com"), ("t\u043E", "evil@example.net"), ("subject", "s"), ("body", "b"))));

        Assert.False(preview.CanApprove);
        Assert.Contains(preview.UnsafeFields, f => f.Contains("t⟦U+043E⟧", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_ascii_recipient_is_allowed_but_warned_about()
    {
        var preview = ApprovalPreview.Parse(Approval("email_send", Args(("to", "sam@\u0456nternal.com"), ("subject", "s"), ("body", "b"))));

        Assert.True(preview.CanApprove);
        Assert.Contains(preview.Warnings, w => w.Contains("non-ASCII", StringComparison.Ordinal));
        Assert.Equal("sam@⟦U+0456⟧nternal.com", ApprovalPreview.RevealNonAscii(preview.Email!.To));
    }

    [Fact]
    public void Invisible_characters_in_the_body_or_a_value_only_warn()
    {
        var email = ApprovalPreview.Parse(Approval("email_send", Args(("to", "a@x.y"), ("subject", "s"), ("body", "pay\u202Eenoyna"))));
        var args = ApprovalPreview.Parse(Approval("calendar_create", Args(("title", "a\u200Bb"))));

        Assert.True(email.CanApprove);
        Assert.Single(email.Warnings);
        Assert.True(args.CanApprove);
        Assert.Single(args.Warnings);
    }

    [Fact]
    public void The_exact_text_is_kept_raw_so_pages_reveal_it_themselves()
    {
        var json = Args(("title", "a\u202Eb"));

        var preview = ApprovalPreview.Parse(Approval("calendar_create", json));

        Assert.Equal(json, preview.Exact);
        Assert.Contains("⟦U+202E⟧", ApprovalPreview.Reveal(preview.Exact), StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_approvals_have_no_warnings_and_stay_approvable()
    {
        var preview = ApprovalPreview.Parse(Approval("email_send", Args(("to", "sam@example.com"), ("subject", "Hello 👍"), ("body", "plain body"))));

        Assert.True(preview.CanApprove);
        Assert.Empty(preview.Warnings);
        Assert.Empty(preview.UnsafeFields);
    }
}
