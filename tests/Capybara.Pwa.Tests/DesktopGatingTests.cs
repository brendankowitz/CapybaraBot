using Bunit;
using Capybara.Client;
using Capybara.Pwa.Pages;

namespace Capybara.Pwa.Tests;

/// <summary>Desktop-only actions (FR-CON-07, FR-SAF-06, FR-AGT-07) are not offered on other devices; the server enforces them too.</summary>
public sealed class DesktopGatingTests : PwaTestContext
{
    private static readonly string[] Templates = ["assistant"];
    private static readonly AgentStatusDto Agent = new("ea", "EA", "assist", "auto", false, false);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Kill_switch_release_is_disabled_off_the_desktop(bool desktop, bool expectedDisabled)
    {
        SetDesktop(desktop);
        ServeJson("/api/killswitch", new KillSwitchDto(true, "manual", DateTimeOffset.UtcNow));

        var cut = Render<Kill>();

        cut.WaitForAssertion(() => Assert.Contains("Engaged", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(expectedDisabled, cut.FindAll("button").Single(b => b.TextContent.StartsWith("Release", StringComparison.Ordinal)).HasAttribute("disabled"));
        Assert.False(cut.FindAll("button").Single(b => b.TextContent.StartsWith("Engage", StringComparison.Ordinal)).HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lifecycle_management_only_appears_on_the_desktop(bool desktop)
    {
        SetDesktop(desktop);
        ServeJson("/api/agents", new[] { Agent });
        ServeJson("/api/lifecycle/templates", Templates);

        var cut = Render<Agents>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article.card")));
        Assert.Equal(desktop, cut.FindAll("details").Count > 0);
        Assert.Equal(desktop, cut.Markup.Contains("New agent", StringComparison.Ordinal));
        Assert.Equal(desktop, cut.Markup.Contains("Delete", StringComparison.Ordinal));
        if (!desktop)
        {
            Assert.DoesNotContain(Gateway.Requests, r => r.Path == "/api/lifecycle/templates");
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Connection_edits_are_disabled_off_the_desktop(bool desktop, bool expectedDisabled)
    {
        SetDesktop(desktop);
        ServeJson("/api/connections", new[] { new ConnectionDto("email", "google", "Mail", true, new ConnectionHealthDto("expiring", null, "refresh soon")) });

        var cut = Render<Connections>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("table.connections tbody tr")));
        Assert.Equal(expectedDisabled, cut.Find("table.connections button").HasAttribute("disabled"));
        Assert.Contains("expiring", cut.Find("td.warning").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_activity_comes_from_the_rest_api_on_load()
    {
        ServeJson("/api/agents", new[] { Agent with { State = "needsAttention", Unread = 2 } });

        var cut = Render<Agents>();

        cut.WaitForAssertion(() => Assert.Contains("Needs attention · 2", cut.Find(".badge").TextContent, StringComparison.Ordinal));
    }
}
