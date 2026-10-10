using System.Text.Json;
using Capybara.Contracts.Agents;
using Capybara.Policy.Rules;
using Capybara.Testing;
using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Tests;

public sealed class SessionHandlersTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea", a => a with
    {
        Tools = ["web_fetch", "email_send"],
        ApprovalRequiredTools = ["email_send"],
    });

    private static PreToolUseHookInput Input(string tool) => new()
    {
        ToolName = tool,
        ToolArgs = JsonDocument.Parse("""{"url":"https://example.com"}""").RootElement.Clone(),
    };

    [Fact]
    public async Task Allowed_call_approves_exactly_one_permission_request_and_taints_untrusted_reads()
    {
        using var f = new RuntimeFixture([new PolicyRule { Effect = RuleEffect.Allow, Tool = "web_fetch" }]);
        var session = f.Session(Ea);

        var output = await f.Handlers.OnPreToolUseAsync(session, Input("web_fetch"));

        Assert.Equal("allow", output.PermissionDecision);
        Assert.True(f.Taint.IsTainted(session.SessionId));
        Assert.True(f.Allowances.TryConsume(session.SessionId));
        Assert.False(f.Allowances.TryConsume(session.SessionId));
    }

    [Fact]
    public async Task Approval_required_tool_is_denied_pending_approval()
    {
        using var f = new RuntimeFixture();
        var session = f.Session(Ea);

        var output = await f.Handlers.OnPreToolUseAsync(session, Input("email_send"));

        Assert.Equal("deny", output.PermissionDecision);
        Assert.Contains("approval", output.PermissionDecisionReason, StringComparison.Ordinal);
        Assert.Single(f.Approvals.Pending());
    }

    [Fact]
    public async Task Permission_requests_without_a_gate_allowance_are_rejected()
    {
        using var f = new RuntimeFixture();
        var session = f.Session(Ea);

        var decision = await f.Handlers.OnPermissionRequestAsync(session, new PermissionRequest());

        Assert.NotNull(decision);
        Assert.False(f.Allowances.TryConsume(session.SessionId));
    }

    [Fact]
    public async Task Ask_user_when_unattended_tells_the_agent_to_proceed_without_input()
    {
        using var f = new RuntimeFixture();

        var response = await f.Handlers.OnUserInputRequestAsync(f.Session(Ea), new UserInputRequest { Question = "Which?" });

        Assert.Equal(Hooks.SessionHandlers.UserUnavailableAnswer, response.Answer);
    }
}
