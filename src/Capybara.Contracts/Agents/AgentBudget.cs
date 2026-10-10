namespace Capybara.Contracts.Agents;

/// <summary>Credit budgets for one agent (FR-OBS-02). One GitHub AI Credit is $0.01.</summary>
public sealed record AgentBudget
{
    public decimal DailyCredits { get; init; } = 50m;

    public decimal MonthlyCredits { get; init; } = 1_000m;

    /// <summary>
    /// The most one turn may spend; the host aborts a turn that passes it. Also sent to the SDK as
    /// <c>SessionLimits.MaxAiCredits</c>, raised to the CLI's 30-credit minimum, as a soft backstop only. The ledger
    /// enforces the daily and monthly hard stop.
    /// </summary>
    public decimal SessionMaxCredits { get; init; } = 10m;
}
