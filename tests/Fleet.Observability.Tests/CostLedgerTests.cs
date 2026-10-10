using Fleet.Contracts.Agents;
using Fleet.Observability.Alerts;
using Fleet.Observability.Cost;
using Microsoft.Extensions.Time.Testing;

namespace Fleet.Observability.Tests;

public sealed class CostLedgerTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAlertSink _alerts = new();

    private CostLedger CreateLedger()
    {
        var ledger = new CostLedger(_alerts, _time);
        ledger.SetBudget(Ea, new AgentBudget { DailyCredits = 10, MonthlyCredits = 100 });
        return ledger;
    }

    [Fact]
    public async Task Daily_budget_is_a_hard_stop_that_resets_the_next_day()
    {
        var ledger = CreateLedger();
        await ledger.RecordAsync(new UsageRecord(Ea, "auto", 10m, _time.GetUtcNow()), TestContext.Current.CancellationToken);

        Assert.False(ledger.CanSpend(Ea));
        Assert.Contains(_alerts.Recent, a => a.Kind == "budget.daily");

        _time.Advance(TimeSpan.FromDays(1));
        Assert.True(ledger.CanSpend(Ea));
    }

    [Fact]
    public async Task Threshold_alerts_fire_once_each()
    {
        var ledger = CreateLedger();
        var ct = TestContext.Current.CancellationToken;
        for (var day = 0; day < 9; day++)
        {
            await ledger.RecordAsync(new UsageRecord(Ea, "auto", 9m, _time.GetUtcNow()), ct);
            _time.Advance(TimeSpan.FromDays(1));
        }

        var agentAlerts = _alerts.Recent.Where(a => a.Agent == "ea" && a.Kind == "budget.monthly").ToList();
        Assert.Equal(2, agentAlerts.Count);
    }

    [Fact]
    public async Task Fleet_ceiling_stops_every_agent()
    {
        var ledger = CreateLedger();
        ledger.FleetMonthlyLimit = 5m;
        await ledger.RecordAsync(new UsageRecord(AgentId.Parse("code"), "auto", 5m, _time.GetUtcNow()), TestContext.Current.CancellationToken);

        Assert.False(ledger.CanSpend(Ea));
    }

    [Fact]
    public async Task Spend_is_attributed_by_model_and_routine()
    {
        var ledger = CreateLedger();
        var ct = TestContext.Current.CancellationToken;
        await ledger.RecordAsync(new UsageRecord(Ea, "auto", 1m, _time.GetUtcNow(), RoutineId: "heartbeat"), ct);
        await ledger.RecordAsync(new UsageRecord(Ea, "gpt-x", 2m, _time.GetUtcNow()), ct);

        Assert.Equal(1m, ledger.ByModel()["auto"]);
        Assert.Equal(2m, ledger.ByModel()["gpt-x"]);
        Assert.Equal(1m, ledger.ByRoutine()["heartbeat"]);
    }
}
