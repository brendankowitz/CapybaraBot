using Capybara.Client;
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

    [Theory]
    [InlineData("\u001b[31mred\nline\u0007", "[31mred\nline")]
    [InlineData("a\u202Eb\u2066c\u200Bd\u2069e\u00ADf", "abcdef")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Clean_drops_escapes_bidi_and_zero_width_characters(string? input, string expected) =>
        Assert.Equal(expected, Capybara.Tui.Rendering.Tables.Clean(input));

    [Fact]
    public void Safe_strips_control_characters_and_escapes_markup() =>
        Assert.Equal("[[red]]x[[/]]", Capybara.Tui.Rendering.Tables.Safe("\u001b[red]x[/]\u202E"));

    [Fact]
    public void Plain_is_null_safe() =>
        Assert.NotNull(Capybara.Tui.Rendering.Tables.Plain(null));

    [Fact]
    public void Every_table_renders_hostile_cells_as_inert_text()
    {
        const string hostile = "\u001b]0;pwned\u0007[red]\u202Eevil[/]";
        var when = DateTimeOffset.UnixEpoch;
        var tables = new Spectre.Console.Rendering.IRenderable[]
        {
            Capybara.Tui.Rendering.Tables.Agents([new AgentStatusDto(hostile, hostile, hostile, hostile, false, false)]),
            Capybara.Tui.Rendering.Tables.Approvals([new ApprovalDto(hostile, "n", hostile, hostile, "h", hostile, hostile, false, when, when)]),
            Capybara.Tui.Rendering.Tables.Proposals([new ProposalDto(hostile, hostile, hostile, null, hostile, "d", hostile, hostile, when)]),
            Capybara.Tui.Rendering.Tables.MemoryPages([new MemoryPageInfoDto(hostile, 1)]),
            Capybara.Tui.Rendering.Tables.PendingMemory([new PendingMemoryChangeDto(hostile, hostile, hostile, null, "a", when, hostile, hostile)]),
            Capybara.Tui.Rendering.Tables.Routines([new RoutineDto(hostile, hostile, hostile, true, false, false, 1m, [])]),
            Capybara.Tui.Rendering.Tables.RoutineRuns(new RoutineDto("r", "o", "k", true, false, false, 1m, [new RoutineRunDto(when, hostile, 1m, hostile)])),
            Capybara.Tui.Rendering.Tables.Budgets(new BudgetsDto([new AgentBudgetDto(hostile, 1, 1, 1, 1, false)], new BudgetTotalDto(1, 1), new BudgetTotalDto(1, 1), [new BudgetLineDto(hostile, 1)], [new BudgetLineDto(hostile, 1)], null)),
        };

        foreach (var table in tables)
        {
            using var writer = new StringWriter();
            var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings { Out = new Spectre.Console.AnsiConsoleOutput(writer), Ansi = Spectre.Console.AnsiSupport.No, ColorSystem = Spectre.Console.ColorSystemSupport.NoColors });
            console.Profile.Width = 2_000;
            console.Profile.Width = 2_000;
            console.Write(table);
            var output = writer.ToString();

            Assert.DoesNotContain('\u001b', output);
            Assert.DoesNotContain('\u0007', output);
            Assert.DoesNotContain('\u202E', output);
            Assert.Contains("[red]", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Parser_reads_the_yes_flag()
    {
        Assert.True(ParsedCommand.Parse(["approvals", "decide", "a1", "allow-once", "--yes"]).Yes);
        Assert.True(ParsedCommand.Parse(["approvals", "decide", "a1", "allow-once", "-y"]).Yes);
        Assert.False(ParsedCommand.Parse(["approvals", "decide", "a1", "deny"]).Yes);
    }

    private static ApprovalDto Approval(string tool, string argsJson, string preview = "ea → x", bool offersAlways = true) =>
        new("a1", "n", "ea", tool, "hash", preview, "Medium", offersAlways, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ArgsJson: argsJson);

    private const string GoodEmail = """{"body":"hi","subject":"Hello","to":"sam@example.com"}""";

    [Fact]
    public void Allow_shows_the_whole_request_and_asks_for_confirmation()
    {
        var plan = ApprovalReview.Plan(Approval("email_send", GoodEmail), "allow-once", assumeYes: false);

        Assert.Null(plan.Refusal);
        Assert.Equal("AllowOnce", plan.Choice);
        Assert.True(plan.NeedsConfirmation);
        Assert.Contains("To: sam@example.com", plan.Review);
        Assert.Contains("Subject: Hello", plan.Review);
        Assert.Contains(GoodEmail, plan.Review);
    }

    [Fact]
    public void Yes_skips_the_confirmation_but_not_the_review_text()
    {
        var plan = ApprovalReview.Plan(Approval("email_send", GoodEmail), "allow-once", assumeYes: true);

        Assert.Null(plan.Refusal);
        Assert.False(plan.NeedsConfirmation);
        Assert.NotEmpty(plan.Review);
    }

    [Fact]
    public void Deny_is_always_possible_without_confirmation()
    {
        var plan = ApprovalReview.Plan(Approval("email_send", "{broken"), "deny", assumeYes: false);

        Assert.Null(plan.Refusal);
        Assert.Equal("Deny", plan.Choice);
        Assert.False(plan.NeedsConfirmation);
    }

    [Theory]
    [InlineData("allow-once")]
    [InlineData("always")]
    public void Allow_is_refused_when_the_preview_is_cut_off_even_with_yes(string choice)
    {
        var cut = Approval("email_send", argsJson: string.Empty, preview: """ea → email_send {"body":"long…""");

        var plan = ApprovalReview.Plan(cut with { ArgsJson = null }, choice, assumeYes: true);

        Assert.NotNull(plan.Refusal);
        Assert.Contains("cut off", plan.Refusal, StringComparison.Ordinal);
        Assert.Contains("deny", plan.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Full_args_from_the_server_make_a_cut_preview_allowable_and_show_the_recipient()
    {
        var approval = Approval("email_send", """{"body":"hi","subject":"s","to":"attacker@evil.test"}""", preview: """ea → email_send {"body":"hi…""");

        var plan = ApprovalReview.Plan(approval, "allow-once", assumeYes: false);

        Assert.Null(plan.Refusal);
        Assert.Contains("To: attacker@evil.test", plan.Review);
    }

    [Theory]
    [InlineData("""{"body":"b","subject":"s","to":"a@x.y\u202Egro.live"}""")]
    [InlineData("""{"To":"a@x.y","body":"b","subject":"s","to":"c@x.y"}""")]
    [InlineData("""{"body":"b","subject":"s","to":"a@x.y","t\u043E":"evil@x.y"}""")]
    [InlineData("not json")]
    public void Allow_is_refused_for_look_alike_ambiguous_or_unparseable_arguments(string argsJson)
    {
        var plan = ApprovalReview.Plan(Approval("email_send", argsJson), "allow-once", assumeYes: true);

        Assert.NotNull(plan.Refusal);
        Assert.DoesNotContain('\u202E', plan.Refusal);
        Assert.All(plan.Review, line => Assert.False(ApprovalPreview.ContainsInvisible(line), line));
    }

    [Fact]
    public void Always_is_refused_when_the_server_does_not_offer_it()
    {
        var plan = ApprovalReview.Plan(Approval("email_send", GoodEmail, offersAlways: false), "always", assumeYes: false);

        Assert.NotNull(plan.Refusal);
        Assert.Contains("high-risk", plan.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_text_reveals_invisible_characters_instead_of_dropping_them()
    {
        var plan = ApprovalReview.Plan(Approval("calendar_create", "{\"title\":\"pay\\u202Eenoyna\\u200B\"}"), "allow-once", assumeYes: false);

        Assert.Null(plan.Refusal);
        Assert.Contains(plan.Review, line => line.Contains("⟦U+202E⟧", StringComparison.Ordinal) && line.Contains("⟦U+200B⟧", StringComparison.Ordinal));
        Assert.Contains(plan.Review, line => line.Contains("invisible or control characters", StringComparison.Ordinal));
    }

    [Fact]
    public void The_approvals_table_reveals_invisible_characters_and_flags_deny_only_cards()
    {
        var hostile = Approval("email_send", """{"body":"b","subject":"s","to":"a@x.y\u202Egro.live"}""", preview: "ea → email_send {\"to\":\"a@x.y\u202Egro.live\"}");
        var fine = Approval("calendar_create", """{"title":"Sync"}""") with { Id = "a2" };

        using var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings { Out = new Spectre.Console.AnsiConsoleOutput(writer), Ansi = Spectre.Console.AnsiSupport.No, ColorSystem = Spectre.Console.ColorSystemSupport.NoColors });
        console.Profile.Width = 2_000;
        console.Write(Capybara.Tui.Rendering.Tables.Approvals([hostile, fine]));
        var output = writer.ToString();

        Assert.Contains("⟦U+202E⟧", output, StringComparison.Ordinal);
        Assert.DoesNotContain('\u202E', output);
        Assert.Contains("deny only", output, StringComparison.Ordinal);
        Assert.Contains("yes", output, StringComparison.Ordinal);
    }

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
