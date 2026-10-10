using Fleet.Contracts.Approvals;
using Fleet.Contracts.Messaging;
using Fleet.Policy.Gate;
using Fleet.Policy.Review;
using Fleet.Policy.Rules;
using Fleet.Testing;

namespace Fleet.Policy.Tests;

public sealed class PolicyGateTests
{
    private static readonly Contracts.Agents.AgentDefinition Ea = TestAgents.Create("ea", a => a with
    {
        Tools = ["email_read", "email_send", "web_fetch", "ask_agent", "memory_*"],
        ExternalActionTools = ["email_send"],
        ApprovalRequiredTools = ["email_send"],
    });

    [Fact]
    public async Task Tools_outside_the_allowlist_are_denied_even_with_an_allow_rule()
    {
        using var f = new PolicyFixture([new PolicyRule { Effect = RuleEffect.Allow, Tool = "*" }]);

        var verdict = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "shell"), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Deny, verdict.Outcome);
        Assert.Equal("static", verdict.Layer);
    }

    [Fact]
    public async Task Kill_switch_blocks_everything()
    {
        using var f = new PolicyFixture([new PolicyRule { Effect = RuleEffect.Allow, Tool = "*" }]);
        f.KillSwitch.Engage("test");

        var verdict = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "email_read"), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Deny, verdict.Outcome);
        Assert.Equal("kill-switch", verdict.Layer);
    }

    [Fact]
    public async Task Deny_rule_beats_allow_rule()
    {
        using var f = new PolicyFixture(
        [
            new PolicyRule { Effect = RuleEffect.Allow, Tool = "email_read" },
            new PolicyRule { Effect = RuleEffect.Deny, Tool = "email_read", Reason = "nope" },
        ]);

        var verdict = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "email_read"), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Deny, verdict.Outcome);
    }

    [Fact]
    public async Task Approval_required_tool_asks_even_with_allow_rule_and_approval_is_single_use()
    {
        using var f = new PolicyFixture([new PolicyRule { Effect = RuleEffect.Allow, Tool = "email_send" }]);
        var call = PolicyFixture.Call(Ea, "email_send", """{"to":"bob@example.com","body":"hi"}""");
        var ct = TestContext.Current.CancellationToken;

        var first = await f.Gate.EvaluateAsync(call, ct);
        Assert.Equal(GateOutcome.PendingApproval, first.Outcome);
        var approval = Assert.IsType<ApprovalRequest>(first.Approval);
        Assert.Equal(RiskLevel.High, approval.Risk);

        Assert.Equal(Approvals.ApprovalOutcome.Accepted, f.Approvals.Decide(approval.Id, approval.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User));

        Assert.Equal(GateOutcome.Allow, (await f.Gate.EvaluateAsync(call, ct)).Outcome);
        Assert.Equal(GateOutcome.PendingApproval, (await f.Gate.EvaluateAsync(call, ct)).Outcome);
    }

    [Fact]
    public async Task Approval_is_bound_to_the_exact_arguments()
    {
        using var f = new PolicyFixture();
        var ct = TestContext.Current.CancellationToken;
        var first = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "email_send", """{"to":"bob@example.com"}"""), ct);
        f.Approvals.Decide(first.Approval!.Id, first.Approval.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User);

        var swapped = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "email_send", """{"to":"mallory@example.com"}"""), ct);

        Assert.Equal(GateOutcome.PendingApproval, swapped.Outcome);
    }

    [Fact]
    public async Task Allow_unless_tainted_asks_in_a_tainted_session_unless_destination_is_allowlisted()
    {
        using var f = new PolicyFixture(
        [
            new PolicyRule { Effect = RuleEffect.AllowUnlessTainted, Tool = "ask_agent", Argument = "agent", DestinationAllowlist = ["research"] },
        ]);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(GateOutcome.Allow, (await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "ask_agent", """{"agent":"code"}"""), ct)).Outcome);

        f.Taint.MarkTainted("s1");
        Assert.Equal(GateOutcome.PendingApproval, (await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "ask_agent", """{"agent":"code"}"""), ct)).Outcome);
        Assert.Equal(GateOutcome.Allow, (await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "ask_agent", """{"agent":"research"}"""), ct)).Outcome);
    }

    [Fact]
    public async Task Reviewer_cannot_allow_in_a_tainted_session()
    {
        using var f = new PolicyFixture(reviewer: new FixedReviewer(ReviewVerdict.Allow));
        f.Taint.MarkTainted("s1");

        var verdict = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "web_fetch"), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.PendingApproval, verdict.Outcome);
    }

    [Fact]
    public async Task Reviewer_errors_fail_closed()
    {
        using var f = new PolicyFixture(reviewer: new ThrowingReviewer());

        var verdict = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "web_fetch"), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Deny, verdict.Outcome);
        Assert.Equal("error", verdict.Layer);
    }

    [Fact]
    public async Task Every_decision_is_audited()
    {
        using var f = new PolicyFixture();
        await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "shell"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(f.Audit.Entries);
        Assert.Equal("shell", entry.Tool);
        Assert.Equal("ea", entry.Agent);
    }

    private sealed class FixedReviewer(ReviewVerdict verdict) : IToolCallReviewer
    {
        public Task<ReviewResult> ReviewAsync(ToolCallContext toolCall, CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewResult(verdict, "fixed"));
    }

    private sealed class ThrowingReviewer : IToolCallReviewer
    {
        public Task<ReviewResult> ReviewAsync(ToolCallContext toolCall, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("reviewer down");
    }
}
