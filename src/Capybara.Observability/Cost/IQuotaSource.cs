namespace Capybara.Observability.Cost;

/// <summary>
/// Source of the account-level credit quota (FR-OBS-02). The production adapter wraps the SDK's
/// <c>account.getQuota</c>; tests use a fake.
/// </summary>
public interface IQuotaSource
{
    Task<QuotaSnapshot> GetQuotaAsync(CancellationToken cancellationToken);
}

/// <summary>Credits consumed out of the plan's allowance for the current billing period.</summary>
/// <param name="Used">Credits consumed so far.</param>
/// <param name="Limit">The plan allowance; must be positive.</param>
/// <param name="ResetsAt">When the allowance renews. A change starts a new period and re-arms alerts.</param>
public sealed record QuotaSnapshot(decimal Used, decimal Limit, DateTimeOffset? ResetsAt = null)
{
    public decimal Fraction => Limit <= 0 ? 1 : Used / Limit;
}
