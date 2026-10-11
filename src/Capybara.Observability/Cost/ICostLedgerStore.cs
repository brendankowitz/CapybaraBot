namespace Capybara.Observability.Cost;

/// <summary>
/// Durable backing for <see cref="CostLedger"/> so spend, and therefore the host-side hard stop, survives a restart
/// (FR-OBS-02, NFR-03).
/// </summary>
public interface ICostLedgerStore
{
    /// <summary>Every record persisted so far, oldest first. Called once at ledger construction.</summary>
    IReadOnlyList<UsageRecord> Load();

    ValueTask AppendAsync(UsageRecord usage, CancellationToken cancellationToken = default);
}
