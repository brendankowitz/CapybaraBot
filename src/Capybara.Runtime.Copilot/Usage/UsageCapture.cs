using Capybara.Contracts.Agents;
using Capybara.Observability.Cost;
using Capybara.Runtime.Copilot.Sessions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Usage;

/// <summary>
/// Captures <c>assistant.usage</c> events into the cost ledger so the hard stop sees spend as it happens
/// (FR-OBS-02, FR-OBS-03), and into the session's turn record for per-run credits and model attribution.
/// </summary>
/// <remarks>
/// A routine run's usage is not written to the ledger here: the executor returns the run's credits and model, and the
/// routine dispatcher records them exactly once (otherwise every routine credit would be counted twice). The run is
/// bounded meanwhile by its per-run budget, passed to the SDK as <c>SessionLimits.MaxAiCredits</c>.
/// </remarks>
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

    public UsageRecord? ToRecord(CapybaraSession session, SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ToRecord(session.Agent.Id, session.Model, session.SessionId, session.RoutineId, session.Envelope.ParentTaskId, sessionEvent);
    }

    /// <summary>The session's <c>OnEvent</c> callback. Synchronous by SDK contract, so recording is fire-and-log.</summary>
    public void OnEvent(CapybaraSession session, SessionEvent sessionEvent)
    {
        if (ToRecord(session, sessionEvent) is not { } record)
        {
            return;
        }

        session.Turn.RecordUsage(record.Model, record.Credits);
        if (session.RoutineId is null)
        {
            _ = RecordAsync(record);
        }
    }

    /// <summary>
    /// <c>OnEvent</c> for a reviewer session (FR-SAF-05): its spend is charged to the agent whose call was reviewed.
    /// </summary>
    public void OnReviewerEvent(AgentId reviewed, string model, string sessionId, SessionEvent sessionEvent)
    {
        if (ToRecord(reviewed, model, sessionId, null, null, sessionEvent) is { } record)
        {
            _ = RecordAsync(record);
        }
    }

    private UsageRecord? ToRecord(AgentId agent, string model, string sessionId, string? routineId, string? taskId, SessionEvent sessionEvent)
    {
        if (sessionEvent is not AssistantUsageEvent { Data: { } data })
        {
            return null;
        }

        return new UsageRecord(
            agent,
            data.Model ?? data.CopilotUsage?.Model ?? model,
            CreditsFrom(data),
            time.GetUtcNow(),
            sessionId,
            routineId,
            taskId,
            (long)(data.InputTokens ?? 0),
            (long)(data.OutputTokens ?? 0));
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
