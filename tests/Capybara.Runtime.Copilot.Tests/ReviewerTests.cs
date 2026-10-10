using System.Text.Json;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Policy.Gate;
using Capybara.Policy.Review;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Review;
using Capybara.Testing;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// FR-SAF-05: the reviewer uses the fixed rubric and parser, fails toward a human, never sees over-limit arguments,
/// runs in a tool-less session on the configured cheap model, and cannot override an ask-first rule.
/// </summary>
public sealed class ReviewerTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea", a => a with
    {
        Tools = ["calendar_read", "email_send"],
        ApprovalRequiredTools = ["email_send"],
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ToolCallContext Call(string tool = "calendar_read", string args = """{"day":"today"}""") =>
        new(Ea, "agent:ea:main", tool, JsonDocument.Parse(args).RootElement.Clone(), MessageOrigin.User);

    private static CopilotToolCallReviewer Reviewer(IReviewerModel model, TimeSpan? timeout = null) =>
        new(model, new CopilotRuntimeOptions { ReviewerTimeout = timeout ?? TimeSpan.FromSeconds(5) }, TimeProvider.System, NullLogger<CopilotToolCallReviewer>.Instance);

    [Theory]
    [InlineData("VERDICT: ALLOW\nRATIONALE: read-only", ReviewVerdict.Allow)]
    [InlineData("VERDICT: BLOCK\nRATIONALE: exfiltration", ReviewVerdict.Block)]
    [InlineData("I think it is fine", ReviewVerdict.Escalate)]
    public async Task The_model_text_is_parsed_with_the_fixed_parser(string text, ReviewVerdict expected)
    {
        var model = new FakeModel(text);

        var result = await Reviewer(model).ReviewAsync(Call(), Ct);

        Assert.Equal(expected, result.Verdict);
        Assert.Equal(ReviewerRubric.Build(Call()), Assert.Single(model.Prompts));
        Assert.Equal(Ea.Id, model.Reviewed);
    }

    [Fact]
    public async Task A_model_or_transport_failure_escalates()
    {
        var result = await Reviewer(new FakeModel(failure: new HttpRequestException("down"))).ReviewAsync(Call(), Ct);

        Assert.Equal(ReviewVerdict.Escalate, result.Verdict);
    }

    [Fact]
    public async Task A_reviewer_that_does_not_answer_in_time_escalates()
    {
        var result = await Reviewer(new FakeModel(hang: true), TimeSpan.FromMilliseconds(50)).ReviewAsync(Call(), Ct);

        Assert.Equal(ReviewVerdict.Escalate, result.Verdict);
    }

    [Fact]
    public async Task Over_limit_arguments_never_reach_the_model()
    {
        var model = new FakeModel("VERDICT: ALLOW\nRATIONALE: fine");
        var huge = JsonSerializer.Serialize(new { body = new string('x', ReviewerRubric.MaxArgsLength + 10) });

        var result = await Reviewer(model).ReviewAsync(Call(args: huge), Ct);

        Assert.Equal(ReviewerRubric.TruncatedResult, result);
        Assert.Empty(model.Prompts);
    }

    [Fact]
    public async Task The_reviewer_can_block_but_never_override_an_ask_first_tool()
    {
        var model = new FakeModel("VERDICT: ALLOW\nRATIONALE: looks fine");
        using var f = new RuntimeFixture();
        var gate = new PolicyGate(f.Rules, f.Taint, f.Approvals, f.KillSwitch, f.Audit, Reviewer(model));

        var askFirst = await gate.EvaluateAsync(Call("email_send"), Ct);
        var unresolved = await gate.EvaluateAsync(Call("calendar_read"), Ct);

        Assert.Equal(GateOutcome.PendingApproval, askFirst.Outcome);
        Assert.Equal(GateOutcome.Allow, unresolved.Outcome);
        Assert.Equal("reviewer", unresolved.Layer);
        Assert.Single(model.Prompts);
    }

    [Fact]
    public async Task The_reviewer_session_has_no_tools_and_runs_on_the_cheap_model()
    {
        using var f = new RuntimeFixture();

        var config = f.Configs.CreateReviewerConfig("reviewer:1", "gpt-5-mini", Ea.Id);

        Assert.Equal("gpt-5-mini", config.Model);
        Assert.Empty(config.AvailableTools!);
        Assert.Empty(config.Tools!);
        Assert.Empty(config.McpServers!);
        Assert.False(config.EnableSkills);
        Assert.False(config.EnableSessionStore);
        Assert.Equal("deny", (await config.Hooks!.OnPreToolUse!(new PreToolUseHookInput { ToolName = "bash" }, null!))!.PermissionDecision);
        var decision = await config.OnPermissionRequest!(new PermissionRequest(), null!);
        Assert.NotNull(decision);
        Assert.False(f.Allowances.TryConsume("reviewer:1"));
    }

    private sealed class FakeModel(string? text = null, Exception? failure = null, bool hang = false) : IReviewerModel
    {
        public List<string> Prompts { get; } = [];

        public AgentId? Reviewed { get; private set; }

        public async Task<string?> CompleteAsync(AgentId reviewed, string prompt, CancellationToken cancellationToken)
        {
            Reviewed = reviewed;
            Prompts.Add(prompt);
            if (hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return failure is null ? text : throw failure;
        }
    }
}
