using Capybara.Contracts.Agents;
using Capybara.Contracts.Routines;
using Capybara.Observability.Alerts;
using Capybara.Scheduling.History;
using Capybara.Scheduling.Planning;
using Capybara.Scheduling.Validation;

namespace Capybara.Scheduling.Tests;

public sealed class RoutineTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static RoutineSpec Heartbeat(TimeSpan interval) => new()
    {
        Id = "hb",
        Owner = Ea,
        Kind = TriggerKind.Heartbeat,
        Interval = interval,
        Prompt = "check",
        ActiveFrom = new TimeOnly(7, 0),
        ActiveTo = new TimeOnly(21, 0),
    };

    [Fact]
    public void Intervals_below_five_minutes_are_rejected()
    {
        var errors = RoutineValidator.Validate([Heartbeat(TimeSpan.FromMinutes(1))], new HashSet<AgentId> { Ea });
        Assert.NotEmpty(errors);
        Assert.Empty(RoutineValidator.Validate([Heartbeat(TimeSpan.FromMinutes(5))], new HashSet<AgentId> { Ea }));
    }

    [Fact]
    public void Unknown_owner_is_rejected() =>
        Assert.NotEmpty(RoutineValidator.Validate([Heartbeat(TimeSpan.FromHours(1))], new HashSet<AgentId>()));

    [Fact]
    public void At_most_fifty_routines_per_agent()
    {
        var routines = Enumerable.Range(0, RoutineLimits.MaxRoutinesPerAgent + 1)
            .Select(i => Heartbeat(TimeSpan.FromHours(1)) with { Id = $"r{i}" })
            .ToList();

        Assert.NotEmpty(RoutineValidator.Validate(routines, new HashSet<AgentId> { Ea }));
    }

    [Fact]
    public void Next_fire_is_jittered_and_within_active_hours()
    {
        var planner = new RoutinePlanner(new Random(42));
        var routine = Heartbeat(TimeSpan.FromHours(2));
        var after = new DateTimeOffset(2026, 10, 10, 20, 30, 0, TimeSpan.Zero);

        var next = planner.NextFire(routine, after);

        Assert.NotNull(next);
        Assert.True(RoutinePlanner.IsWithinActiveHours(routine, next.Value));
        Assert.True(next.Value > after);
    }

    [Fact]
    public void Event_routines_have_no_schedule()
    {
        var planner = new RoutinePlanner(new Random(1));
        var routine = new RoutineSpec { Id = "e", Owner = Ea, Kind = TriggerKind.Event, EventFilter = "github:issue.opened repo=owner/name", Prompt = "triage" };
        Assert.Null(planner.NextFire(routine, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Heartbeat_every_two_hours_in_a_fourteen_hour_window_projects_seven_runs_a_day()
    {
        var runsPerMonth = CostProjection.RunsPerMonth(Heartbeat(TimeSpan.FromHours(2)));
        Assert.InRange(runsPerMonth, 7 * 30 - 1, 7 * 31 + 1);
    }

    [Fact]
    public async Task Failed_runs_raise_alerts_and_history_is_bounded()
    {
        var alerts = new InMemoryAlertSink();
        var history = new RoutineRunHistory(alerts);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < RoutineLimits.RunHistoryDepth + 5; i++)
        {
            await history.RecordAsync(new RoutineRun("hb", DateTimeOffset.UnixEpoch.AddMinutes(i), RoutineRunOutcome.Succeeded), ct);
        }

        await history.RecordAsync(new RoutineRun("hb", DateTimeOffset.UnixEpoch, RoutineRunOutcome.Failed, "boom"), ct);

        Assert.Equal(RoutineLimits.RunHistoryDepth, history.Get("hb").Count);
        Assert.Single(alerts.Recent);
    }
}
