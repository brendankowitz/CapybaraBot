using Capybara.Contracts.Agents;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Observability.Tests;

public sealed class DurableLedgerTests : IDisposable
{
    private static readonly AgentId Ea = AgentId.Parse("ea");
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAlertSink _alerts = new();

    private string LedgerPath => Path.Combine(_dir.Path, "state", "cost-ledger.jsonl");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Spend_and_hard_stop_survive_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var store = new FileCostLedgerStore(LedgerPath))
        {
            var before = NewLedger(store);
            await before.RecordAsync(new UsageRecord(Ea, "auto", 6m, _time.GetUtcNow(), RoutineId: "heartbeat"), ct);
            await before.RecordAsync(new UsageRecord(Ea, "gpt-x", 4m, _time.GetUtcNow(), InputTokens: 10, OutputTokens: 5), ct);
            Assert.False(before.CanSpend(Ea));
        }

        using var reopenedStore = new FileCostLedgerStore(LedgerPath);
        var after = NewLedger(reopenedStore);

        Assert.False(after.CanSpend(Ea));
        Assert.Equal(10m, after.GetStatus(Ea).SpentToday);
        Assert.Equal(6m, after.ByModel()["auto"]);
        Assert.Equal(6m, after.ByRoutine()["heartbeat"]);
        Assert.Equal(15, after.Records.Sum(r => r.InputTokens + r.OutputTokens));

        _time.Advance(TimeSpan.FromDays(1));
        Assert.True(after.CanSpend(Ea));
    }

    [Fact]
    public async Task Fleet_ceiling_survives_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var store = new FileCostLedgerStore(LedgerPath))
        {
            var before = NewLedger(store);
            before.FleetMonthlyLimit = 5m;
            await before.RecordAsync(new UsageRecord(AgentId.Parse("code"), "auto", 5m, _time.GetUtcNow()), ct);
        }

        using var reopenedStore = new FileCostLedgerStore(LedgerPath);
        var after = NewLedger(reopenedStore);
        after.FleetMonthlyLimit = 5m;

        Assert.False(after.CanSpend(Ea));
    }

    [Fact]
    public async Task Alerts_already_sent_are_not_repeated_after_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var store = new FileCostLedgerStore(LedgerPath))
        {
            await NewLedger(store).RecordAsync(new UsageRecord(Ea, "auto", 9m, _time.GetUtcNow()), ct);
        }

        var firstRun = _alerts.Recent.Count(a => a.Kind == "budget.monthly");
        Assert.Equal(0, firstRun);

        _time.Advance(TimeSpan.FromDays(1));
        using (var store = new FileCostLedgerStore(LedgerPath))
        {
            var ledger = NewLedger(store);
            await ledger.RecordAsync(new UsageRecord(Ea, "auto", 45m, _time.GetUtcNow()), ct);
        }

        var crossed = _alerts.Recent.Count(a => a.Agent == "ea" && a.Kind == "budget.monthly");
        Assert.Equal(1, crossed); // 54% crossed 50% once

        using var again = new FileCostLedgerStore(LedgerPath);
        await NewLedger(again).RecordAsync(new UsageRecord(Ea, "auto", 1m, _time.GetUtcNow()), ct);

        Assert.Equal(crossed, _alerts.Recent.Count(a => a.Agent == "ea" && a.Kind == "budget.monthly"));
    }

    [Fact]
    public async Task A_torn_last_line_is_dropped_and_appends_continue_cleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var store = new FileCostLedgerStore(LedgerPath))
        {
            await NewLedger(store).RecordAsync(new UsageRecord(Ea, "auto", 2m, _time.GetUtcNow()), ct);
        }

        await File.AppendAllTextAsync(LedgerPath, "{\"agent\":\"ea\",\"model\":\"au", ct);

        using (var store = new FileCostLedgerStore(LedgerPath))
        {
            var recovered = NewLedger(store);
            Assert.Equal(2m, recovered.GetStatus(Ea).SpentToday);
            await recovered.RecordAsync(new UsageRecord(Ea, "auto", 3m, _time.GetUtcNow()), ct);
        }

        using var final = new FileCostLedgerStore(LedgerPath);
        Assert.Equal(5m, NewLedger(final).GetStatus(Ea).SpentToday);
    }

    [Fact]
    public async Task Corruption_before_the_last_line_fails_loudly()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var store = new FileCostLedgerStore(LedgerPath))
        {
            var ledger = NewLedger(store);
            await ledger.RecordAsync(new UsageRecord(Ea, "auto", 1m, _time.GetUtcNow()), ct);
            await ledger.RecordAsync(new UsageRecord(Ea, "auto", 1m, _time.GetUtcNow()), ct);
        }

        var lines = await File.ReadAllLinesAsync(LedgerPath, ct);
        lines[0] = "{ not json";
        await File.WriteAllLinesAsync(LedgerPath, lines, ct);

        using var reopened = new FileCostLedgerStore(LedgerPath);
        Assert.Throws<InvalidDataException>(() => NewLedger(reopened));
    }

    private CostLedger NewLedger(ICostLedgerStore store)
    {
        var ledger = new CostLedger(_alerts, _time, store);
        ledger.SetBudget(Ea, new AgentBudget { DailyCredits = 10, MonthlyCredits = 100 });
        return ledger;
    }
}
