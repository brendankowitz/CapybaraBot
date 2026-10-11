using Capybara.Contracts.Approvals;
using Capybara.Policy.Approvals;
using Capybara.Policy.Gate;
using Capybara.Policy.Rules;
using Capybara.Testing;

namespace Capybara.Policy.Tests;

/// <summary>FR-RTN-08: a routine dry run auto-denies whatever would need a human and never prompts anyone.</summary>
public sealed class PolicyGateDryRunTests
{
    private static readonly Contracts.Agents.AgentDefinition Ea = TestAgents.Create("ea", a => a with
    {
        Tools = ["email_read", "email_send"],
        ApprovalRequiredTools = ["email_send"],
    });

    [Fact]
    public async Task Approval_required_tool_is_denied_without_raising_an_approval()
    {
        using var f = new PolicyFixture();

        var verdict = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "email_send") with { DryRun = true }, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Deny, verdict.Outcome);
        Assert.Equal(PolicyGate.DryRunLayer, verdict.Layer);
        Assert.Empty(f.Approvals.Pending());
    }

    [Fact]
    public async Task Allowed_tools_still_run_in_a_dry_run()
    {
        using var f = new PolicyFixture([new PolicyRule { Effect = RuleEffect.Allow, Tool = "email_read" }]);

        var verdict = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "email_read") with { DryRun = true }, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Allow, verdict.Outcome);
    }

    [Fact]
    public async Task A_dry_run_never_spends_a_real_approval()
    {
        using var f = new PolicyFixture();
        var pending = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "email_send"), TestContext.Current.CancellationToken);
        Assert.Equal(ApprovalOutcome.Accepted, f.Approvals.Decide(pending.Approval!.Id, pending.Approval.Nonce, ApprovalDecision.AllowOnce, Contracts.Messaging.MessageOrigin.User));

        var dry = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "email_send") with { DryRun = true }, TestContext.Current.CancellationToken);
        var real = await f.Gate.EvaluateAsync(PolicyFixture.Call(Ea, "email_send"), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Deny, dry.Outcome);
        Assert.Equal(GateOutcome.Allow, real.Outcome);
    }
}
