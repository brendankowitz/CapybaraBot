using System.Text.Json;
using Capybara.Connections.Grants;
using Capybara.Contracts.Connections;

namespace Capybara.Connections.Tests;

public sealed class GrantEnforcerTests
{
    [Fact]
    public void Default_denies_when_no_grant_matches()
    {
        var enforcer = new GrantEnforcer(new InMemoryGrantProvider());
        using var args = JsonDocument.Parse("{}");

        var decision = enforcer.Decide("ea", "gmail", "send", args.RootElement);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Enforces_constraints_and_branch_globs()
    {
        var provider = new InMemoryGrantProvider().Set("code", [new Grant
        {
            Connection = "github",
            Operations = ["create_pr"],
            Constraints = new Dictionary<string, IReadOnlyList<string>>
            {
                ["repo"] = ["owner/repo"],
                ["branch"] = ["agent/*"],
            },
        }]);
        var enforcer = new GrantEnforcer(provider);
        using var allowed = JsonDocument.Parse("{\"repo\":\"owner/repo\",\"branch\":\"agent/fix\"}");
        using var denied = JsonDocument.Parse("{\"repo\":\"owner/repo\",\"branch\":\"main\"}");

        Assert.True(enforcer.Decide("code", "github", "create_pr", allowed.RootElement).Allowed);
        Assert.False(enforcer.Decide("code", "github", "create_pr", denied.RootElement).Allowed);
    }
}
