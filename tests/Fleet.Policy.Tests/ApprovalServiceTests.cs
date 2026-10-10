using Fleet.Contracts.Agents;
using Fleet.Contracts.Approvals;
using Fleet.Contracts.Messaging;
using Fleet.Policy.Approvals;

namespace Fleet.Policy.Tests;

public sealed class ApprovalServiceTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    [Fact]
    public void Only_the_user_can_decide()
    {
        using var f = new PolicyFixture();
        var request = f.Approvals.Request(Ea, "s1", "email_send", "hash", "preview", RiskLevel.Low, MessageOrigin.Agent);

        Assert.Equal(ApprovalOutcome.NotHuman, f.Approvals.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.Agent));
        Assert.Equal(ApprovalOutcome.InvalidNonce, f.Approvals.Decide(request.Id, "wrong", ApprovalDecision.AllowOnce, MessageOrigin.User));
        Assert.Equal(ApprovalOutcome.Accepted, f.Approvals.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User));
        Assert.Equal(ApprovalOutcome.AlreadyDecided, f.Approvals.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User));
    }

    [Fact]
    public void Unattended_approvals_expire_after_ten_minutes()
    {
        using var f = new PolicyFixture();
        var request = f.Approvals.Request(Ea, "s1", "email_send", "hash", "preview", RiskLevel.Low, MessageOrigin.Event);

        f.Time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(ApprovalOutcome.Expired, f.Approvals.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User));
        Assert.Empty(f.Approvals.Pending());
    }

    [Fact]
    public void Always_allow_is_not_offered_for_high_risk()
    {
        using var f = new PolicyFixture();
        var request = f.Approvals.Request(Ea, "s1", "email_send", "hash", "preview", RiskLevel.High, MessageOrigin.User);

        Assert.False(request.OffersAlwaysAllow);
        Assert.Equal(ApprovalOutcome.AlwaysNotPermitted, f.Approvals.Decide(request.Id, request.Nonce, ApprovalDecision.AlwaysAllow, MessageOrigin.User));
    }

    [Fact]
    public void Always_allow_adds_a_runtime_rule()
    {
        using var f = new PolicyFixture();
        var request = f.Approvals.Request(Ea, "s1", "calendar_read", "hash", "preview", RiskLevel.Low, MessageOrigin.User);

        f.Approvals.Decide(request.Id, request.Nonce, ApprovalDecision.AlwaysAllow, MessageOrigin.User);

        Assert.NotNull(f.Rules.Evaluate("ea", "calendar_read", null));
    }

    [Fact]
    public void Nonces_are_128_bit()
    {
        using var f = new PolicyFixture();
        var request = f.Approvals.Request(Ea, "s1", "x", "hash", "preview", RiskLevel.Low, MessageOrigin.User);
        Assert.Equal(32, request.Nonce.Length);
    }
}
