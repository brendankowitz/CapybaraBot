using Capybara.Contracts.Agents;
using Capybara.Contracts.Connections;
using Capybara.Policy.Trifecta;
using Capybara.Testing;

namespace Capybara.Policy.Tests;

public sealed class TrifectaValidatorTests
{
    [Fact]
    public void All_three_legs_require_approval_on_every_external_tool()
    {
        var agent = TestAgents.Create("ea", a => a with
        {
            Tools = ["email_send"],
            ExternalActionTools = ["email_send"],
            Trifecta = new TrifectaProfile { UntrustedInput = true, PrivateData = true, ExternalAction = true },
        });

        Assert.Single(TrifectaValidator.Validate(agent));
        Assert.Empty(TrifectaValidator.Validate(agent with { ApprovalRequiredTools = ["email_send"] }));
    }

    [Fact]
    public void Grants_require_the_private_data_leg()
    {
        var agent = TestAgents.Create("research", a => a with
        {
            Grants = [new Grant { Connection = "gmail" }],
            Trifecta = new TrifectaProfile { UntrustedInput = true },
        });

        Assert.Contains(TrifectaValidator.Validate(agent), e => e.Contains("private-data", StringComparison.Ordinal));
    }

    [Fact]
    public void External_tools_must_be_in_the_allowlist()
    {
        var agent = TestAgents.Create("code", a => a with
        {
            Tools = [],
            ExternalActionTools = ["git_push"],
            Trifecta = new TrifectaProfile { ExternalAction = true },
        });

        Assert.Contains(TrifectaValidator.Validate(agent), e => e.Contains("git_push", StringComparison.Ordinal));
    }
}
