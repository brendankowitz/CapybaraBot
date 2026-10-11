using Capybara.Observability.Cost;
using Capybara.Runtime.Copilot.Hosting;

namespace Capybara.Runtime.Copilot.Usage;

/// <summary>
/// The account's plan quota from the SDK's <c>account.getQuota</c>, read through whichever agent CLI is running
/// (FR-OBS-02). Reports premium-interaction requests; an unlimited entitlement never crosses a threshold.
/// </summary>
/// <remarks>
/// Throws when no runtime is running or the account reports no premium quota, which <see cref="QuotaMonitor"/> turns
/// into one <c>quota.poll_failed</c> warning per failure streak: the quota is then unknown, never assumed fine.
/// </remarks>
public sealed class CopilotQuotaSource(AgentRuntimePool runtimes) : IQuotaSource
{
    /// <summary>The quota bucket that AI credits draw from.</summary>
    public const string PremiumQuota = "premium_interactions";

    public async Task<QuotaSnapshot> GetQuotaAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in runtimes.All.OfType<AgentRuntime>())
        {
            if (await runtime.GetAccountQuotaAsync(cancellationToken) is { } snapshot)
            {
                return snapshot;
            }
        }

        throw new InvalidOperationException("No agent runtime is running, so the quota is unknown.");
    }
}
