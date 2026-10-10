namespace Fleet.Observability.Cost;

/// <summary>Spend against one agent's budgets for the current day and month.</summary>
public sealed record BudgetStatus(
    string Agent,
    decimal SpentToday,
    decimal DailyLimit,
    decimal SpentThisMonth,
    decimal MonthlyLimit)
{
    public bool IsExhausted => SpentToday >= DailyLimit || SpentThisMonth >= MonthlyLimit;

    public decimal MonthlyFraction => MonthlyLimit <= 0 ? 1 : SpentThisMonth / MonthlyLimit;
}
