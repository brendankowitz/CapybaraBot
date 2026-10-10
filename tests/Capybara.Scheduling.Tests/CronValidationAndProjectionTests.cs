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

    [Theory]
    [InlineData("0 0 9 L * ?", 2)]
    [InlineData("0 0 9 1 7 ?", 1)]
    [InlineData("0 0 9 1 * ?", 2)]
    [InlineData("0 0 9 ? * MON#1", 2)]
    [InlineData("0 0 9 1 1 ? 2031", 1)]
    public void Rare_crons_never_project_zero_so_enabling_cannot_be_acknowledged_for_free(string expression, int worstMonthFires)
    {
        var routine = Kit.Cron(expression) with { BudgetCreditsPerRun = 3m };

        Assert.True(CostProjection.RunsPerMonth(routine) >= 1, "a cron that fires at all must cost something");
        Assert.True(CostProjection.RunsPerMonth(routine) <= worstMonthFires);
        Assert.NotEqual(0m, CostProjection.MonthlyCredits(routine));

        var registry = new Capybara.Scheduling.Lifecycle.RoutineRegistry();
        registry.AddOrUpdate(routine, Kit.User);
        Assert.Throws<InvalidOperationException>(() => registry.Enable(routine.Id, 0m, Kit.User));
        registry.Enable(routine.Id, registry.ProjectedMonthlyCredits(routine), Kit.User);
    }

    [Fact]
    public void Seasonal_crons_project_their_busiest_month_not_the_yearly_average()
    {
        var summerDaily = Kit.Cron("0 0 9 * JUN-AUG ?");

        Assert.Equal(30, CostProjection.RunsPerMonth(summerDaily));
    }

    [Fact]
    public void A_cron_that_never_fires_projects_zero()
    {
        Assert.Equal(0, CostProjection.RunsPerMonth(Kit.Cron("0 0 9 1 1 ? 2020")));
    }

    [Fact]
    public void An_interval_rarer_than_daily_projects_its_real_cost()
    {
        var weekly = Kit.Heartbeat(interval: TimeSpan.FromDays(7));

        Assert.Equal(5, CostProjection.RunsPerMonth(weekly));
    }

    [Fact]
    public void A_heartbeat_ignores_a_stray_cron_expression()
    {
        var heartbeat = Kit.Heartbeat(interval: TimeSpan.FromHours(2)) with { Cron = "0 0 9 1 1 ?" };

        Assert.Equal(12 * 30, CostProjection.RunsPerMonth(heartbeat));
    }

    [Fact]
    public void A_too_frequent_stretch_in_one_part_of_the_year_or_a_pinned_year_is_still_found()
    {
        Assert.True(CronSchedule.TryParse("0 0/2 * * 11 ?", TimeZoneInfo.Utc, out var everyTwoMinutesInNovember, out _));
        Assert.True(CronSchedule.TryParse("0 * * 1 1 ? 2035", TimeZoneInfo.Utc, out var everyMinuteInOneYear, out _));
        Assert.True(CronSchedule.TryParse("0 0/5 * * * ?", TimeZoneInfo.Utc, out var everyFiveMinutes, out _));

        Assert.Equal(TimeSpan.FromMinutes(2), CronSchedule.TooFrequentGap(everyTwoMinutesInNovember));
        Assert.Equal(TimeSpan.FromMinutes(1), CronSchedule.TooFrequentGap(everyMinuteInOneYear));
        Assert.Null(CronSchedule.TooFrequentGap(everyFiveMinutes));
        Assert.NotEmpty(RoutineValidator.Validate([Kit.Cron("0 0/2 * * 11 ?")], Known));
    }

    [Fact]
    public void Cron_checks_use_the_configured_time_zone()
    {
        var pacific = TimeZoneInfo.CreateCustomTimeZone("test-minus-7", TimeSpan.FromHours(-7), "test", "test");
        var routine = Kit.Cron("0 0 9 * * ?");
        var registry = new Capybara.Scheduling.Lifecycle.RoutineRegistry(pacific);

        registry.AddOrUpdate(routine, Kit.User);

        Assert.Same(pacific, registry.TimeZone);
        Assert.Equal(30, CostProjection.RunsPerMonth(routine, pacific));
        Assert.Empty(RoutineValidator.Validate([routine], Known, pacific));
    }
}
