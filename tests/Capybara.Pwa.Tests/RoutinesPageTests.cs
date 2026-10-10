using System.Net;
using System.Text.Json;
using Bunit;
using Capybara.Client;
using Capybara.Pwa.Pages;

namespace Capybara.Pwa.Tests;

public sealed class RoutinesPageTests : PwaTestContext
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private void Serve(decimal projection = 42.5m, int runs = 25, bool enabled = false, bool autoPaused = false)
    {
        var history = Enumerable.Range(0, runs).Select(i => new RoutineRunDto(DateTimeOffset.UtcNow.AddHours(-i), "Completed", 1.5m, "auto")).ToList();
        Gateway.Respond("GET", "/api/routines", JsonSerializer.Serialize(new[] { new RoutineDto("daily-digest", "ea", "cron", enabled, false, autoPaused, projection, history) }, Web));
    }

    [Fact]
    public void Shows_at_most_the_last_20_runs_and_the_projected_cost()
    {
        Serve(runs: 25);

        var cut = Render<Routines>();

        cut.WaitForAssertion(() => Assert.Equal(20, cut.FindAll("table.runs tbody tr").Count));
        Assert.Equal("42.5 credits", cut.Find(".projection").TextContent);
    }

    [Fact]
    public void Enable_requires_the_cost_acknowledgement_and_sends_the_projection()
    {
        Serve();
        Gateway.Respond("POST", "/api/routines/daily-digest/enable", string.Empty, HttpStatusCode.NoContent);
        var cut = Render<Routines>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article.routine")));

        cut.FindAll("button").Single(b => b.TextContent == "Enable…").Click();

        var enable = cut.FindAll("button").Single(b => b.TextContent == "Enable routine");
        Assert.True(enable.HasAttribute("disabled"));
        Assert.Contains("42.5 credits per month", cut.Find(".ack").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(Gateway.Requests, r => r.Path.EndsWith("/enable", StringComparison.Ordinal));

        cut.Find(".ack input[type=checkbox]").Change(true);
        cut.FindAll("button").Single(b => b.TextContent == "Enable routine").Click();

        cut.WaitForAssertion(() =>
        {
            var post = Assert.Single(Gateway.Requests, r => r.Path == "/api/routines/daily-digest/enable");
            using var body = JsonDocument.Parse(post.Body!);
            Assert.Equal(42.5m, body.RootElement.GetProperty("acknowledgedMonthlyCredits").GetDecimal());
        });
    }

    [Fact]
    public void A_moved_projection_is_rejected_and_the_acknowledgement_reset()
    {
        Serve();
        Gateway.Respond("POST", "/api/routines/daily-digest/enable", "projection is now 50", HttpStatusCode.Conflict);
        var cut = Render<Routines>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article.routine")));
        cut.FindAll("button").Single(b => b.TextContent == "Enable…").Click();
        cut.Find(".ack input[type=checkbox]").Change(true);

        cut.FindAll("button").Single(b => b.TextContent == "Enable routine").Click();

        cut.WaitForAssertion(() => Assert.Contains("projection is now 50", cut.Find("p.error").TextContent, StringComparison.Ordinal));
        Assert.True(cut.FindAll("button").Single(b => b.TextContent == "Enable routine").HasAttribute("disabled"));
    }

    [Fact]
    public void Dry_run_lists_the_auto_denied_approvals()
    {
        Serve();
        Gateway.Respond("POST", "/api/routines/daily-digest/dry-run", """{"status":"Completed","output":"done","deniedApprovals":["email_send"],"credits":0.5}""");
        var cut = Render<Routines>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article.routine")));

        cut.FindAll("button").Single(b => b.TextContent == "Dry run").Click();

        cut.WaitForAssertion(() => Assert.Contains("email_send", cut.Find(".dry-run").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void Auto_paused_routines_offer_a_confirm_resume()
    {
        Serve(enabled: true, autoPaused: true);
        Gateway.Respond("POST", "/api/routines/daily-digest/confirm-resume", string.Empty, HttpStatusCode.NoContent);
        var cut = Render<Routines>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article.routine")));

        cut.FindAll("button").Single(b => b.TextContent == "Confirm resume").Click();

        cut.WaitForAssertion(() => Assert.Contains(Gateway.Requests, r => r.Path == "/api/routines/daily-digest/confirm-resume"));
    }
}
