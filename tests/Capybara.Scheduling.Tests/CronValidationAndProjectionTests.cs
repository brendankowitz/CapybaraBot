using Capybara.Contracts.Agents;
using Capybara.Scheduling.Planning;
using Capybara.Scheduling.Validation;

namespace Capybara.Scheduling.Tests;

/// <summary>FR-RTN-03 (cron minimum interval) and FR-RTN-09 (cron cost projection).</summary>
public sealed class CronValidationAndProjectionTests
{
    private static readonly HashSet<AgentId> Known = [Kit.Ea];

    [Theory]
    [InlineData("0 */1 * * * ?")]
    [InlineData("0/30 * * * * ?")]
    [InlineData("0 0,2 * * * ?")]
    [InlineData("0 0/4 * * * ?")]
    public void Cron_expressions_that_fire_more_often_than_every_five_minutes_are_rejected(string expression)
    {
        var errors = RoutineValidator.Validate([Kit.Cron(expression)], Known);

        var error = Assert.Single(errors);
        Assert.Contains("minimum", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0 */5 * * * ?")]
    [InlineData("0 0 */2 * * ?")]
    [InlineData("0 30 8 ? * MON-FRI")]
    [InlineData("0 0 9 1 * ?")]
    public void Cron_expressions_at_or_above_five_minutes_are_accepted_without_an_interval(string expression) =>
        Assert.Empty(RoutineValidator.Validate([Kit.Cron(expression)], Known));

    [Fact]
    public void Invalid_cron_syntax_is_rejected_with_a_reason()
    {
        var error = Assert.Single(RoutineValidator.Validate([Kit.Cron("not a cron")], Known));

        Assert.Contains("invalid", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cron_routine_needs_a_cron_expression_or_an_interval()
    {
        var routine = Kit.Cron("0 0 * * * ?") with { Cron = null };

        Assert.NotEmpty(RoutineValidator.Validate([routine], Known));
    }

    [Fact]
    public void Cron_projection_equals_sampled_fires_per_thirty_days_times_budget()
    {
        var routine = Kit.Cron("0 0 */2 * * ?") with { BudgetCreditsPerRun = 2.5m };

        Assert.True(CronSchedule.TryParse(routine.Cron, TimeZoneInfo.Utc, out var cron, out _));
        var sampled = CronSchedule.FiresPerMonth(cron);

        Assert.Equal(12 * 30, sampled);
        Assert.Equal(sampled, CostProjection.RunsPerMonth(routine));
        Assert.Equal(sampled * 2.5m, CostProjection.MonthlyCredits(routine));
    }

    [Fact]
    public void Weekday_only_cron_projects_only_the_days_it_fires()
    {
        var weekdays = Kit.Cron("0 0 8 ? * MON-FRI");

        // 30 consecutive days contain between 20 and 22 weekdays.
        Assert.InRange(CostProjection.RunsPerMonth(weekdays), 20, 22);
    }

    [Fact]
    public void Event_routines_project_zero_and_interval_routines_are_unchanged()
    {
        Assert.Equal(0, CostProjection.MonthlyCredits(Kit.Event("github:issues.opened repo=a/b")));
        Assert.Equal(24 * 30, CostProjection.RunsPerMonth(Kit.Heartbeat(interval: TimeSpan.FromHours(1))));
    }
}
