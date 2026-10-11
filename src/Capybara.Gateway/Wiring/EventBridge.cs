using System.Collections.Concurrent;
using System.Threading.Channels;
using Capybara.Channels;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Proposals;
using Capybara.Gateway.Endpoints;
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
/// publisher. Agent activity (turn start/end, approval pending) goes through one ordered queue instead, so clients
/// never end on a stale state. In-flight deliveries are awaited on shutdown.
/// </remarks>
public sealed partial class EventBridge(
    ApprovalService approvals,
    FleetThread thread,
    ProposalService proposals,
    RuntimeActivity activity,
    InboxPumpHostedService pumps,
    CopilotRoutineExecutor routines,
    PhoneUserEscalation questions,
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

    // Activity updates (turn start/end, approval pending) are applied and broadcast strictly in the order the events were
    // raised: applied out of order, a turn's end could land before its start and leave the agent Working forever.
    private readonly Channel<(string What, Func<Task> Work)> _activity =
        Channel.CreateUnbounded<(string, Func<Task>)>(new UnboundedChannelOptions { SingleReader = true });

    private Task _activityLoop = Task.CompletedTask;

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
        questions.Asked += OnQuestionAsked;
        _activityLoop = Task.Run(RunActivityAsync, CancellationToken.None);
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
        questions.Asked -= OnQuestionAsked;
        _activity.Writer.TryComplete();
        await Task.WhenAll([.. _inFlight.Keys, _activityLoop]).WaitAsync(cancellationToken);
    }

    /// <summary>The phone push and the client broadcast are independent: neither waits on the other's I/O (NFR-04).</summary>
    private void OnApprovalRequested(object? sender, ApprovalRequest approval)
    {
        Dispatch("approval push", async () => await notifications.RouteAsync(NotificationFactory.Approval(approval)));
        Dispatch("approval broadcast", () => events.PublishApprovalRequestedAsync(approval.ToDto()));
        EnqueueActivity("approval pending", () => tracker.ApprovalPendingAsync(approval.Agent.Value));
    }

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
        Dispatch("thread entry", () => events.PublishThreadEntryAsync(entry.ToDto()));

    private void OnProposed(object? sender, ChangeProposal proposal)
    {
        Dispatch("proposal broadcast", () => events.PublishProposalAsync(proposal.ToDto()));

        // Content-free: the diff is reviewed in the PWA, never on the phone (FR-AGT-06, NFR-06).
        Dispatch("proposal push", async () => await notifications.RouteAsync(new Notification(
                NotificationKind.Approval,
                proposal.Agent,
                $"proposal: {proposal.Agent} → {proposal.Kind}",
                string.Empty,
                NotificationPriority.Normal,
                "/proposals",
                IsSensitive: true)));
    }

    /// <summary>The phone push for a question is sent by the escalation itself; the clients get it live here.</summary>
    private void OnQuestionAsked(object? sender, PendingQuestion question) =>
        Dispatch("question broadcast", () => events.PublishQuestionRequestedAsync(question.ToDto()));

    private void OnTurnStarted(object? sender, TurnActivityEventArgs e) =>
        EnqueueActivity("turn started", () => tracker.TurnStartedAsync(e.Agent.Value));

    private void OnTurnEnded(object? sender, TurnActivityEventArgs e) =>
        EnqueueActivity("turn ended", () => tracker.TurnCompletedAsync(e.Agent.Value, needsAttention: e.Result?.Status is TurnStatus.Failed or TurnStatus.LimitExceeded));

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

    private void EnqueueActivity(string what, Func<Task> work)
    {
        if (!_activity.Writer.TryWrite((what, work)))
        {
            LogBridgeFailed(logger, new InvalidOperationException("The event bridge has stopped."), what);
        }
    }

    private async Task RunActivityAsync()
    {
        await foreach (var (what, work) in _activity.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
#pragma warning disable CA1031 // One failed broadcast must not stop later activity updates.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogBridgeFailed(logger, ex, what);
            }
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
