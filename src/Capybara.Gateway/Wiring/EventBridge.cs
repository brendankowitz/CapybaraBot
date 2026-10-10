using System.Collections.Concurrent;
using Capybara.Channels;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Proposals;
using Capybara.Gateway.Realtime;
using Capybara.Observability.Alerts;
using Capybara.Orchestration.Proposals;
using Capybara.Orchestration.Thread;
using Capybara.Policy.Approvals;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Inbox;

namespace Capybara.Gateway.Wiring;

/// <summary>
/// Turns domain events into what the user sees (FR-CLI-02, FR-CLI-03, FR-OBS-05): approvals, proposals, the fleet
/// thread and agent activity go to the PWA/TUI over SignalR; approvals and proposals also go to the phone; expiries,
/// compaction storms and egress spikes become alerts.
/// </summary>
/// <remarks>
/// Domain events are raised synchronously on the thread that changed state, so every bridge hands its I/O to the thread
/// pool immediately: an approval push is on its way within the same instant as the gate decision (NFR-04 ≤10 s;
/// low-risk approvals are batched by the router by design, FR-SAF-10). A failing bridge is logged and never reaches the
/// publisher. In-flight deliveries are awaited on shutdown.
/// </remarks>
public sealed partial class EventBridge(
    ApprovalService approvals,
    FleetThread thread,
    ProposalService proposals,
    RuntimeActivity activity,
    InboxPumpHostedService pumps,
    CopilotRoutineExecutor routines,
    ICapybaraEvents events,
    NotificationRouter notifications,
    AgentActivityTracker tracker,
    IAlertSink alerts,
    EgressSpikeDetector egress,
    CompactionStormDetector compaction,
    TimeProvider time,
    ILogger<EventBridge> logger) : IHostedService
{
    public const string ApprovalExpiredAlertKind = "approval.expired";

    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        approvals.Requested += OnApprovalRequested;
        approvals.Decided += OnApprovalDecided;
        approvals.Expired += OnApprovalExpired;
        thread.Appended += OnThreadAppended;
        proposals.Proposed += OnProposed;
        activity.TurnStarted += OnTurnStarted;
        activity.TurnEnded += OnTurnEnded;
        activity.Compacted += OnCompacted;
        activity.EgressDenied += OnEgressDenied;
        pumps.TurnCompleted += OnInboxTurnCompleted;
        routines.Output += OnRoutineOutput;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        approvals.Requested -= OnApprovalRequested;
        approvals.Decided -= OnApprovalDecided;
        approvals.Expired -= OnApprovalExpired;
        thread.Appended -= OnThreadAppended;
        proposals.Proposed -= OnProposed;
        activity.TurnStarted -= OnTurnStarted;
        activity.TurnEnded -= OnTurnEnded;
        activity.Compacted -= OnCompacted;
        activity.EgressDenied -= OnEgressDenied;
        pumps.TurnCompleted -= OnInboxTurnCompleted;
        routines.Output -= OnRoutineOutput;
        await Task.WhenAll(_inFlight.Keys).WaitAsync(cancellationToken);
    }

    private void OnApprovalRequested(object? sender, ApprovalRequest approval) =>
        Dispatch("approval requested", async () =>
        {
            // Phone first: it is the latency-bound path (NFR-04); the clients also refresh the list on reconnect.
            await notifications.RouteAsync(NotificationFactory.Approval(approval));
            await events.PublishApprovalRequestedAsync(approval);
            await tracker.ApprovalPendingAsync(approval.Agent.Value);
        });

    private void OnApprovalDecided(object? sender, ApprovalRequest approval) =>
        Dispatch("approval decided", () => events.PublishApprovalResolvedAsync(approval.Id));

    private void OnApprovalExpired(object? sender, ApprovalRequest approval) =>
        Dispatch("approval expired", async () =>
        {
            await alerts.PublishAsync(new Alert(
                ApprovalExpiredAlertKind,
                AlertSeverity.Warning,
                $"Approval for {approval.Agent} → {approval.Tool} expired unanswered and was denied.",
                time.GetUtcNow(),
                approval.Agent.Value));
            await events.PublishApprovalResolvedAsync(approval.Id);
        });

    private void OnThreadAppended(object? sender, FleetThreadEntry entry) =>
        Dispatch("thread entry", () => events.PublishThreadEntryAsync(entry));

    private void OnProposed(object? sender, ChangeProposal proposal) =>
        Dispatch("proposal", async () =>
        {
            await events.PublishProposalAsync(proposal);

            // Content-free: the diff is reviewed in the PWA, never on the phone (FR-AGT-06, NFR-06).
            await notifications.RouteAsync(new Notification(
                NotificationKind.Approval,
                proposal.Agent,
                $"proposal: {proposal.Agent} → {proposal.Kind}",
                string.Empty,
                NotificationPriority.Normal,
                "/proposals",
                IsSensitive: true));
        });

    private void OnTurnStarted(object? sender, TurnActivityEventArgs e) =>
        Dispatch("turn started", () => tracker.TurnStartedAsync(e.Agent.Value));

    private void OnTurnEnded(object? sender, TurnActivityEventArgs e) =>
        Dispatch("turn ended", () => tracker.TurnCompletedAsync(e.Agent.Value, needsAttention: e.Result?.Status is TurnStatus.Failed or TurnStatus.LimitExceeded));

    private void OnCompacted(object? sender, AgentId agent) =>
        Dispatch("compaction", async () => await compaction.RecordCompactionAsync(agent));

    private void OnEgressDenied(object? sender, AgentId agent) =>
        Dispatch("egress denial", async () => await egress.RecordDenialAsync(agent));

    /// <summary>
    /// A reply to a user DM that arrived on a phone channel. The reply text stays in the PWA (FR-CLI-03: no sensitive
    /// content over Telegram); the phone gets a content-free "finished" with a deep link.
    /// </summary>
    private void OnInboxTurnCompleted(object? sender, InboxTurnCompletedEventArgs e)
    {
        if (e.Message is { Origin: MessageOrigin.User, Channel: { Length: > 0 } } && e.Result.Success)
        {
            Dispatch("dm reply", async () => await notifications.RouteAsync(NotificationFactory.Finished(e.Agent)));
        }
    }

    private void OnRoutineOutput(object? sender, RoutineOutputEventArgs e)
    {
        if (string.Equals(e.Routine.DeliveryTarget, "push", StringComparison.OrdinalIgnoreCase))
        {
            Dispatch("routine output", async () => await notifications.RouteAsync(NotificationFactory.Finished(e.Routine.Owner)));
        }
    }

    private void Dispatch(string what, Func<Task> work)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await work();
            }
#pragma warning disable CA1031 // A failed bridge must never reach the publisher (which may be the policy gate).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogBridgeFailed(logger, ex, what);
            }
        });
        _inFlight.TryAdd(task, 0);
        _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Event bridge for {Event} failed")]
    private static partial void LogBridgeFailed(ILogger logger, Exception exception, string @event);
}
