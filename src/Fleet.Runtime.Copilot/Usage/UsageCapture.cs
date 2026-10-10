using Fleet.Observability.Cost;
using Fleet.Runtime.Copilot.Sessions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;

namespace Fleet.Runtime.Copilot.Usage;

/// <summary>
/// Captures <c>assistant.usage</c> events into the cost ledger so the hard stop sees spend as it happens
/// (FR-OBS-02, FR-OBS-03).
/// </summary>
public sealed partial class UsageCapture(CostLedger ledger, TimeProvider time, ILogger<UsageCapture> logger)
{
    /// <summary>
    /// Assumption: one AI credit is 10^9 nano-AIU, the unit reported in <c>copilotUsage.totalNanoAiu</c>.
    /// When that field is absent the reported <c>cost</c> is used as-is.
    /// </summary>
    public const decimal NanoAiuPerCredit = 1_000_000_000m;

    public static decimal CreditsFrom(AssistantUsageData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.CopilotUsage is { TotalNanoAiu: > 0 } usage)
        {
            return (decimal)usage.TotalNanoAiu / NanoAiuPerCredit;
        }

        return data.Cost is { } cost and > 0 ? (decimal)cost : 0m;
    }

    public UsageRecord? ToRecord(FleetSession session, SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (sessionEvent is not AssistantUsageEvent { Data: { } data })
        {
            return null;
        }

        return new UsageRecord(
            session.Agent.Id,
            data.Model ?? data.CopilotUsage?.Model ?? session.Agent.Model,
            CreditsFrom(data),
            time.GetUtcNow(),
            session.SessionId,
            session.RoutineId,
            session.Envelope.ParentTaskId,
            (long)(data.InputTokens ?? 0),
            (long)(data.OutputTokens ?? 0));
    }

    /// <summary>The session's <c>OnEvent</c> callback. Synchronous by SDK contract, so recording is fire-and-log.</summary>
    public void OnEvent(FleetSession session, SessionEvent sessionEvent)
    {
        if (ToRecord(session, sessionEvent) is not { } record)
        {
            return;
        }

        _ = RecordAsync(record);
    }

    private async Task RecordAsync(UsageRecord record)
    {
        try
        {
            await ledger.RecordAsync(record);
        }
#pragma warning disable CA1031 // Never let ledger failures crash the SDK event loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRecordFailed(logger, ex, record.Agent.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to record usage for agent {Agent}")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, string agent);
}
