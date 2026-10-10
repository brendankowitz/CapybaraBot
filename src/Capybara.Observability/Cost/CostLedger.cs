using System.Collections.Concurrent;
using Capybara.Contracts.Agents;
using Capybara.Observability.Alerts;

namespace Capybara.Observability.Cost;

/// <summary>
/// Cost ledger built from <c>assistant.usage</c>, attributed per agent, routine, task and model (FR-OBS-02).
/// <c>SessionLimits.MaxAiCredits</c> is soft, so this ledger is the host-side hard stop. Alerts fire once per
/// threshold (50%, 80%, 100%) per month. With an <see cref="ICostLedgerStore"/> the ledger replays its history at
/// construction, so spend and the hard stop survive a restart; threshold alerts already crossed before the restart are
/// not re-sent. If persisting a record fails, it still counts in memory (the hard stop must hold) and the failure
/// propagates to the caller. An alert whose sink throws is retried on a later record.
/// </summary>
public sealed class CostLedger
{
    private const string FleetScope = "fleet";
    private static readonly decimal[] Thresholds = [0.5m, 0.8m, 1.0m];

    private readonly ConcurrentDictionary<string, AgentBudget> _budgets = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<UsageRecord> _records = new();
    private readonly ConcurrentDictionary<string, byte> _firedThresholds = new(StringComparer.Ordinal);
    private readonly IAlertSink _alerts;
    private readonly TimeProvider _time;
    private readonly ICostLedgerStore? _store;
    private int _needsPriming;

    public CostLedger(IAlertSink alerts, TimeProvider time, ICostLedgerStore? store = null)
    {
        _alerts = alerts;
        _time = time;
        _store = store;
        if (store is not null)
        {
            foreach (var record in store.Load())
            {
                _records.Enqueue(record);
            }

            _needsPriming = _records.IsEmpty ? 0 : 1;
        }
    }

    public decimal FleetMonthlyLimit { get; set; } = 7_000m;

    public void SetBudget(AgentId agent, AgentBudget budget) => _budgets[agent.Value] = budget;

    public IReadOnlyList<UsageRecord> Records => [.. _records];

    public async ValueTask RecordAsync(UsageRecord usage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        PrimeFiredAlerts();
        _records.Enqueue(usage);
        if (_store is not null)
        {
            await _store.AppendAsync(usage, cancellationToken);
        }

        var status = GetStatus(usage.Agent);
        await RaiseThresholdAlertsAsync(usage.Agent.Value, status.MonthlyFraction, cancellationToken);

        var fleetFraction = FleetMonthlyLimit <= 0 ? 1 : SpentThisMonth(null) / FleetMonthlyLimit;
        await RaiseThresholdAlertsAsync(FleetScope, fleetFraction, cancellationToken);

        if (status.SpentToday >= status.DailyLimit)
        {
            await PublishOnceAsync(
                DailyKey(usage.Agent.Value),
                new Alert("budget.daily", AlertSeverity.Critical, $"{usage.Agent} hit its daily budget; hard stop until tomorrow.", _time.GetUtcNow(), usage.Agent),
                cancellationToken);
        }
    }

    /// <summary>Host-side hard stop: <see langword="false"/> once an agent or the fleet is out of budget.</summary>
    public bool CanSpend(AgentId agent) =>
        !GetStatus(agent).IsExhausted && SpentThisMonth(null) < FleetMonthlyLimit;

    public BudgetStatus GetStatus(AgentId agent)
    {
        var budget = _budgets.GetValueOrDefault(agent.Value) ?? new AgentBudget();
        return new BudgetStatus(agent.Value, SpentToday(agent.Value), budget.DailyCredits, SpentThisMonth(agent.Value), budget.MonthlyCredits);
    }

    /// <summary>Credits by model for the current month, so every credit is attributable (FR-OBS-03).</summary>
    public IReadOnlyDictionary<string, decimal> ByModel() =>
        _records.Where(r => IsThisMonth(r.At))
            .GroupBy(r => r.Model, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Credits), StringComparer.Ordinal);

    public IReadOnlyDictionary<string, decimal> ByRoutine() =>
        _records.Where(r => r.RoutineId is not null && IsThisMonth(r.At))
            .GroupBy(r => r.RoutineId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Credits), StringComparer.Ordinal);

    private DateOnly Today => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

    private decimal SpentToday(string agent) =>
        _records.Where(r => r.Agent.Value == agent && DateOnly.FromDateTime(r.At.UtcDateTime) == Today).Sum(r => r.Credits);

    private decimal SpentThisMonth(string? agent) =>
        _records.Where(r => (agent is null || r.Agent.Value == agent) && IsThisMonth(r.At)).Sum(r => r.Credits);

    private bool IsThisMonth(DateTimeOffset at)
    {
        var now = _time.GetUtcNow();
        return at.UtcDateTime.Year == now.UtcDateTime.Year && at.UtcDateTime.Month == now.UtcDateTime.Month;
    }

    private string DailyKey(string agent) => $"daily:{agent}:{Today:yyyyMMdd}";

    private string MonthlyKey(string scope, decimal threshold) => $"monthly:{scope}:{_time.GetUtcNow():yyyyMM}:{threshold}";

    /// <summary>
    /// After a restart, marks every alert the replayed history had already crossed as fired so it is not sent twice.
    /// Runs once, lazily, so budgets configured after construction are honoured.
    /// </summary>
    private void PrimeFiredAlerts()
    {
        if (Interlocked.Exchange(ref _needsPriming, 0) == 0)
        {
            return;
        }

        foreach (var agent in _records.Where(r => IsThisMonth(r.At)).Select(r => r.Agent).Distinct())
        {
            var status = GetStatus(agent);
            MarkCrossed(agent.Value, status.MonthlyFraction);
            if (status.SpentToday >= status.DailyLimit)
            {
                _firedThresholds.TryAdd(DailyKey(agent.Value), 0);
            }
        }

        MarkCrossed(FleetScope, FleetMonthlyLimit <= 0 ? 1 : SpentThisMonth(null) / FleetMonthlyLimit);
    }

    private void MarkCrossed(string scope, decimal fraction)
    {
        foreach (var threshold in Thresholds.Where(t => fraction >= t))
        {
            _firedThresholds.TryAdd(MonthlyKey(scope, threshold), 0);
        }
    }

    private async ValueTask RaiseThresholdAlertsAsync(string scope, decimal fraction, CancellationToken cancellationToken)
    {
        foreach (var threshold in Thresholds.Where(t => fraction >= t))
        {
            var severity = threshold >= 1.0m ? AlertSeverity.Critical : AlertSeverity.Warning;
            await PublishOnceAsync(
                MonthlyKey(scope, threshold),
                new Alert("budget.monthly", severity, $"{scope} reached {threshold:P0} of its monthly credit budget.", _time.GetUtcNow(), scope == FleetScope ? null : scope),
                cancellationToken);
        }
    }

    private async ValueTask PublishOnceAsync(string key, Alert alert, CancellationToken cancellationToken)
    {
        if (!_firedThresholds.TryAdd(key, 0))
        {
            return;
        }

        // Claimed before publishing so concurrent records send it once; released if the sink fails so the next
        // record retries instead of losing the alert.
        try
        {
            await _alerts.PublishAsync(alert, cancellationToken);
        }
        catch
        {
            _firedThresholds.TryRemove(key, out _);
            throw;
        }
    }
}
