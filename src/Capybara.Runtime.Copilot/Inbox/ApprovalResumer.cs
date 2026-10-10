using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Observability.Alerts;
using Capybara.Orchestration.Inbox;
using Capybara.Orchestration.Tasks;
using Capybara.Policy.Approvals;
using Capybara.Runtime.Copilot.Hooks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Inbox;

/// <summary>
/// Resumes the turn an approval interrupted (FR-SAF-04): when the human decides, or the request expires, a notice is
/// queued in the agent's inbox for the session that asked, so it can retry the approved call (which the gate then
/// lets through once) or carry on without it.
/// </summary>
/// <remarks>
/// The notice is host-authored and <see cref="MessageOrigin.System"/>: it is never itself consent — only the approval
/// record is. <see cref="ApprovalService"/> raises its events synchronously, so the notice is enqueued synchronously
/// too (one small file write). If that fails, the user is alerted: the approval stands, but the agent will not resume
/// on its own.
/// </remarks>
public sealed partial class ApprovalResumer : IHostedService, IDisposable
{
    public const string AlertKind = "approval.resume_failed";

    private readonly ApprovalService _approvals;
    private readonly ApprovalTurnStore _turns;
    private readonly IInboxStore _inbox;
    private readonly IAlertSink _alerts;
    private readonly TimeProvider _time;
    private readonly ILogger<ApprovalResumer> _logger;

    public ApprovalResumer(ApprovalService approvals, ApprovalTurnStore turns, IInboxStore inbox, IAlertSink alerts, TimeProvider time, ILogger<ApprovalResumer> logger)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        _approvals = approvals;
        _turns = turns;
        _inbox = inbox;
        _alerts = alerts;
        _time = time;
        _logger = logger;
    }

    /// <summary>Subscribes to approval outcomes.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _approvals.Decided += OnDecided;
        _approvals.Expired += OnExpired;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _approvals.Decided -= OnDecided;
        _approvals.Expired -= OnExpired;
    }

    private void OnDecided(object? sender, ApprovalRequest request) => Enqueue(
        request,
        request.Status == ApprovalStatus.Approved
            ? $"[approval {request.Id}] The user approved {request.Tool}. Retry that call now with exactly the same arguments; the approval is single-use and expires at {request.ExpiresAt:u}."
            : $"[approval {request.Id}] The user denied {request.Tool}. Do not retry it. Continue without it, or report what is blocked.");

    private void OnExpired(object? sender, ApprovalRequest request) => Enqueue(
        request,
        $"[approval {request.Id}] The approval request for {request.Tool} expired without an answer. Do not retry it on your own; report what is blocked.");

    private void Enqueue(ApprovalRequest request, string body)
    {
        var session = ResumeSession(request);
        var turn = _turns.Take(request.Id);
        var envelope = turn?.Envelope ?? RestrictedEnvelope(request);
        var message = new FleetMessage
        {
            To = request.Agent,
            From = turn?.From,
            Origin = MessageOrigin.System,
            Body = body,
            Envelope = envelope,
            Taint = envelope?.Taint ?? Taint.Clean,
            CreatedAt = _time.GetUtcNow(),
            SessionId = session,
            TaskId = SessionIds.TryParseTask(session, out _, out var key) && FileTaskStore.IsTaskId(key) ? key : null,
        };

        try
        {
            _inbox.EnqueueAsync(message).AsTask().GetAwaiter().GetResult();
        }
#pragma warning disable CA1031 // The decision is committed; the user must learn that the agent will not resume.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEnqueueFailed(_logger, ex, request.Agent.Value, request.Id);
            _ = _alerts.PublishAsync(
                new Alert(AlertKind, AlertSeverity.Warning, $"{request.Agent} could not be told about approval {request.Id}; resume it manually.", _time.GetUtcNow(), request.Agent.Value),
                CancellationToken.None).AsTask();
        }
    }

    /// <summary>
    /// Without a recorded turn context (it could not be stored), a turn that a person or the host started resumes as a
    /// fresh root as before; any other resumes fail closed: at maximum depth (no agent-to-agent tools), tainted, and
    /// rooted in the original origin, so it can never reach the user's phone.
    /// </summary>
    private Envelope? RestrictedEnvelope(ApprovalRequest request)
    {
        if (request.Origin is MessageOrigin.User or MessageOrigin.System)
        {
            return null;
        }

        LogRestrictedResume(_logger, request.Agent.Value, request.Id);
        return new Envelope
        {
            TraceId = System.Diagnostics.ActivityTraceId.CreateRandom().ToHexString(),
            RootTaskId = Guid.NewGuid().ToString("N"),
            Depth = Envelope.MaxDepth,
            HopPath = [request.Agent],
            Budget = new Budget(decimal.MaxValue, int.MaxValue, TimeSpan.MaxValue),
            Deadline = _time.GetUtcNow() + TimeSpan.FromDays(1),
            OriginPrincipal = "system",
            Taint = Taint.Tainted,
            RootOrigin = request.Origin,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No turn context for approval {ApprovalId} of {Agent}; resuming restricted")]
    private static partial void LogRestrictedResume(ILogger logger, string agent, string approvalId);

    /// <summary>
    /// The approval is bound to the session that asked, so the retry must run there. Consult sessions no longer exist,
    /// so their outcome goes to the main session.
    /// </summary>
    private static string? ResumeSession(ApprovalRequest request) =>
        SessionIds.TryGetAgent(request.SessionId, out var agent) && agent == request.Agent && !SessionIds.IsConsult(request.SessionId)
            ? request.SessionId
            : null;

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not queue the outcome of approval {ApprovalId} for {Agent}")]
    private static partial void LogEnqueueFailed(ILogger logger, Exception exception, string agent, string approvalId);
}
