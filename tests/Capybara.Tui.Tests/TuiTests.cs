using Capybara.Tui.Commands;

namespace Capybara.Tui.Tests;

public sealed class TuiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), "tui-test-artifacts", Guid.NewGuid().ToString("N"));

    public TuiTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Parser_handles_base_url_live_and_release_flags()
    {
        var command = ParsedCommand.Parse(["--base-url", "https://capybara.example", "kill", "--release", "--live"]);

        Assert.Equal("kill", command.Name);
        Assert.Equal(new Uri("https://capybara.example"), command.BaseUrl);
        Assert.True(command.Live);
        Assert.True(command.ReleaseKillSwitch);
    }

    [Fact]
    public void Parser_reads_ack_and_out_options()
    {
        var command = ParsedCommand.Parse(["routines", "enable", "daily", "--ack", "42.5", "--out", "x.zip"]);

        Assert.Equal("routines", command.Name);
        Assert.Equal(["enable", "daily"], command.Arguments);
        Assert.Equal(42.5m, command.AcknowledgedCredits);
        Assert.Equal("x.zip", command.OutputPath);
    }

    [Theory]
    [InlineData("--ack")]
    [InlineData("--out")]
    [InlineData("--base-url")]
    public void Parser_rejects_options_without_a_value(string option) =>
        Assert.Throws<ArgumentException>(() => ParsedCommand.Parse(["routines", option]));

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    public void Parser_rejects_a_bad_cost_acknowledgement(string value) =>
        Assert.Throws<ArgumentException>(() => ParsedCommand.Parse(["routines", "enable", "r", "--ack", value]));

    [Theory]
    [InlineData("proposals", new string[0], "list", null)]
    [InlineData("proposals", new[] { "show", "p1" }, "show", "p1")]
    [InlineData("proposals", new[] { "approve", "p1" }, "approve", "p1")]
    [InlineData("proposals", new[] { "reject", "p1" }, "reject", "p1")]
    [InlineData("memory", new string[0], "list", null)]
    [InlineData("memory", new[] { "list", "/agents" }, "list", "/agents")]
    [InlineData("memory", new[] { "show", "/a.md" }, "show", "/a.md")]
    [InlineData("memory", new[] { "pending" }, "pending", null)]
    [InlineData("memory", new[] { "apply", "c1" }, "apply", "c1")]
    [InlineData("memory", new[] { "export" }, "export", null)]
    [InlineData("routines", new string[0], "list", null)]
    [InlineData("routines", new[] { "enable", "r1" }, "enable", "r1")]
    [InlineData("routines", new[] { "disable", "r1" }, "disable", "r1")]
    [InlineData("routines", new[] { "dry-run", "r1" }, "dry-run", "r1")]
    public void Sub_commands_parse_verb_and_target(string command, string[] args, string verb, string? target)
    {
        var sub = SubCommand.Parse(command, args);

        Assert.Equal(verb, sub.Verb);
        Assert.Equal(target, sub.Target);
    }

    [Theory]
    [InlineData("proposals", new[] { "show" })]
    [InlineData("routines", new[] { "enable" })]
    [InlineData("memory", new[] { "show" })]
    [InlineData("memory", new[] { "nuke", "x" })]
    [InlineData("routines", new[] { "launch", "r1" })]
    public void Sub_commands_reject_unknown_verbs_and_missing_targets(string command, string[] args) =>
        Assert.Throws<ArgumentException>(() => SubCommand.Parse(command, args));

    [Theory]
    [InlineData("allow-once", "AllowOnce")]
    [InlineData("allowOnce", "AllowOnce")]
    [InlineData("always", "AlwaysAllow")]
    [InlineData("deny", "Deny")]
    public void Approval_choice_maps_to_wire_values(string text, string expected) =>
        Assert.Equal(expected, ApprovalChoice.Parse(text));

    [Fact]
    public void Approval_choice_rejects_unknown_words() =>
        Assert.Throws<ArgumentException>(() => ApprovalChoice.Parse("maybe"));

    [Fact]
    public void Clean_drops_terminal_escape_characters_from_untrusted_text() =>
        Assert.Equal("[31mred\nline", Capybara.Tui.Rendering.Tables.Clean("\u001b[31mred\nline\u0007"));

    [Fact]
    public void Token_file_refuses_world_readable_permissions()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix file modes are not available on this platform; runs on the Linux target host (FR-CLI-05).");
        var path = Path.Combine(_directory, "token");
        File.WriteAllText(path, "not-a-real-token");
#pragma warning disable CA1416 // Tests run on the Linux target host used by the TUI.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
#pragma warning restore CA1416

        Assert.Throws<UnauthorizedAccessException>(() => TokenFile.EnsurePrivate(path));
    }

    [Fact]
    public void Token_file_accepts_0600_permissions()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix file modes are not available on this platform; runs on the Linux target host (FR-CLI-05).");
        var path = Path.Combine(_directory, "token");
        File.WriteAllText(path, "not-a-real-token");
#pragma warning disable CA1416 // Tests run on the Linux target host used by the TUI.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416

        TokenFile.EnsurePrivate(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
