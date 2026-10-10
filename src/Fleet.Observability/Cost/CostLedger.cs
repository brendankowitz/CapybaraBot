using System.Collections.Concurrent;
using Fleet.Contracts.Agents;
using Fleet.Observability.Alerts;

namespace Fleet.Observability.Cost;

/// <summary>
/// Cost ledger built from <c>assistant.usage</c>, attributed per agent, routine, task and model (FR-OBS-02).
/// <c>SessionLimits.MaxAiCredits</c> is soft, so this ledger is the host-side hard stop. Alerts fire once per
/// threshold (50%, 80%, 100%) per month.
/// </summary>
public sealed class CostLedger(IAlertSink alerts, TimeProvider time)
{
    private static readonly decimal[] Thresholds = [0.5m, 0.8m, 1.0m];

    private readonly ConcurrentDictionary<string, AgentBudget> _budgets = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<UsageRecord> _records = new();
    private readonly ConcurrentDictionary<string, byte> _firedThresholds = new(StringComparer.Ordinal);

    public decimal FleetMonthlyLimit { get; set; } = 7_000m;

    public void SetBudget(AgentId agent, AgentBudget budget) => _budgets[agent.Value] = budget;

    public IReadOnlyList<UsageRecord> Records => [.. _records];

    public async ValueTask RecordAsync(UsageRecord usage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        _records.Enqueue(usage);

        var status = GetStatus(usage.Agent);
        await RaiseThresholdAlertsAsync(usage.Agent.Value, status.MonthlyFraction, cancellationToken);

        var fleetFraction = FleetMonthlyLimit <= 0 ? 1 : SpentThisMonth(null) / FleetMonthlyLimit;
        await RaiseThresholdAlertsAsync("fleet", fleetFraction, cancellationToken);

        if (status.SpentToday >= status.DailyLimit)
        {
            await PublishOnceAsync(
                $"daily:{usage.Agent}:{Today:yyyyMMdd}",
                new Alert("budget.daily", AlertSeverity.Critical, $"{usage.Agent} hit its daily budget; hard stop until tomorrow.", time.GetUtcNow(), usage.Agent),
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

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private decimal SpentToday(string agent) =>
        _records.Where(r => r.Agent.Value == agent && DateOnly.FromDateTime(r.At.UtcDateTime) == Today).Sum(r => r.Credits);

    private decimal SpentThisMonth(string? agent) =>
        _records.Where(r => (agent is null || r.Agent.Value == agent) && IsThisMonth(r.At)).Sum(r => r.Credits);

    private bool IsThisMonth(DateTimeOffset at)
    {
        var now = time.GetUtcNow();
        return at.UtcDateTime.Year == now.UtcDateTime.Year && at.UtcDateTime.Month == now.UtcDateTime.Month;
    }

    private async ValueTask RaiseThresholdAlertsAsync(string scope, decimal fraction, CancellationToken cancellationToken)
    {
        foreach (var threshold in Thresholds.Where(t => fraction >= t))
        {
            var severity = threshold >= 1.0m ? AlertSeverity.Critical : AlertSeverity.Warning;
            await PublishOnceAsync(
                $"monthly:{scope}:{time.GetUtcNow():yyyyMM}:{threshold}",
                new Alert("budget.monthly", severity, $"{scope} reached {threshold:P0} of its monthly credit budget.", time.GetUtcNow(), scope == "fleet" ? null : scope),
                cancellationToken);
        }
    }

    private async ValueTask PublishOnceAsync(string key, Alert alert, CancellationToken cancellationToken)
    {
        if (_firedThresholds.TryAdd(key, 0))
        {
            await alerts.PublishAsync(alert, cancellationToken);
        }
    }
}
