namespace Fleet.Contracts.Agents;

/// <summary>Credit budgets for one agent (FR-OBS-02). One GitHub AI Credit is $0.01.</summary>
public sealed record AgentBudget
{
    public decimal DailyCredits { get; init; } = 50m;

    public decimal MonthlyCredits { get; init; } = 1_000m;

    /// <summary>Passed to <c>SessionLimits.MaxAiCredits</c>. Soft on the SDK side; the ledger enforces the hard stop.</summary>
    public decimal SessionMaxCredits { get; init; } = 10m;
}
