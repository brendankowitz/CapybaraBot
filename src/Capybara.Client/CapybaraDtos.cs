using System.Text.Json;

namespace Capybara.Client;

// Wire shapes for the gateway REST + SignalR surface. See README-contract.md for the route table.
// Enum-valued fields travel as strings (the gateway registers JsonStringEnumConverter); parsing is case-insensitive.

// ---- Agents -----------------------------------------------------------------------------------------------------

// State: working, needsAttention or idle (same wire values as the SignalR AgentStatusChanged event); Unread: messages the user has not seen.
public sealed record AgentStatusDto(string Id, string Name, string Job, string Model, bool Paused, bool BudgetExhausted, bool Hidden = false, string State = "idle", int Unread = 0);

/// <summary>A user turn. <see cref="Incognito"/> turns read and write no memory (FR-MEM-12).</summary>
public sealed record TurnRequestDto(string Prompt, bool Incognito = false, string? ConversationId = null);

// Status: Completed, Blocked, OverBudget, LimitExceeded, Throttled, Failed (the runtime's TurnStatus; HTTP is 200 for all of them)
// or Accepted (the turn was queued through the inbox; the reply arrives in the chat history, see MessageId). Content is the
// reply when Completed; Error says why it did not run or failed.
public sealed record TurnResultDto(string Status, string? Content = null, string? Error = null, string? MessageId = null, string? RoutineId = null, int UserMessagesJoined = 0)
{
    public const string CompletedStatus = "Completed";
    public const string AcceptedStatus = "Accepted";

    public bool IsCompleted => Status == CompletedStatus;

    public bool IsAccepted => Status == AcceptedStatus;

    /// <summary>What to show the user: the reply, or the status with its reason. Never empty, so a turn that did not run is not silent.</summary>
    public string Summary => IsCompleted
        ? (string.IsNullOrEmpty(Content) ? "(no reply)" : Content)
        : IsAccepted ? "Queued: the reply will appear in the chat history."
        : $"{Status}: {(string.IsNullOrEmpty(Error) ? "the turn did not complete." : Error)}";
}

public sealed record SetModelDto(string Model);

/// <summary>One line of an agent's per-agent chat history. <see cref="Role"/> is <c>user</c> or <c>assistant</c>.</summary>
// MessageId: on user entries, the inbox message id (the same value the turn response returns as MessageId). InReplyTo: on assistant
// entries, the MessageId of the user entry they answer; an agent-initiated message has none. A multi-part reply is several
// assistant entries with the same InReplyTo, appended together, in order.
public sealed record ChatEntryDto(DateTimeOffset At, string Role, string Content, bool Incognito = false, string? MessageId = null, string? InReplyTo = null);

public sealed record RuntimeStatusDto(string Id, string State, string? Reason = null);

// ---- Fleet thread (also pushed over SignalR "ThreadEntry") --------------------------------------------------------

public sealed record ThreadEntryDto(DateTimeOffset At, string From, string To, string Operation, string Summary, string TraceId, int Depth, string Taint, bool Rejected);

// ---- Approvals (FR-SAF-04) ----------------------------------------------------------------------------------------

public sealed record ApprovalDecisionDto(string Nonce, string Decision);

// Preview: server text "{agent} → {tool} {canonical args json}", at most 2000 chars and it may be cut mid-JSON (recipients can fall
// off the end). ArgsJson: the full, untruncated canonical args JSON (server must supply it). When it is missing and the preview is
// cut, clients can only Deny. Everything here is untrusted agent output.
public sealed record ApprovalDto(
    string Id,
    string Nonce,
    string Agent,
    string Tool,
    string ArgsHash,
    string Preview,
    string Risk,
    bool OffersAlwaysAllow,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? ReviewerVerdict = null,
    string? ReviewerRationale = null,
    string? Origin = null,
    string? ArgsJson = null);

/// <summary>The three approval choices. Wire values are the <c>ApprovalDecision</c> enum names (case-insensitive).</summary>
public static class ApprovalDecisions
{
    public const string AllowOnce = "AllowOnce";
    public const string AlwaysAllow = "AlwaysAllow";
    public const string Deny = "Deny";
}

// ---- Questions (ask_user escalation; push deep link /questions/{id}) ---------------------------------------------

public sealed record QuestionDto(string Id, string Agent, string Title, string Body, IReadOnlyList<string>? Choices = null, DateTimeOffset? ExpiresAt = null);

public sealed record QuestionAnswerDto(string Answer);

// ---- Proposals (FR-AGT-06) ----------------------------------------------------------------------------------------

// Diff: Unified diff. Plain text only: render in a text node, never as HTML or markdown.
// Status: Pending, Applied, Rejected, Stale or Invalid.
public sealed record ProposalDto(
    string Id,
    string Agent,
    string Kind,
    string? Name,
    string TargetPath,
    string Diff,
    string Rationale,
    string Status,
    DateTimeOffset CreatedAt,
    string? DecidedBy = null,
    DateTimeOffset? DecidedAt = null,
    string? Error = null);

public sealed record ProposalDecisionDto(bool Approve);

// ---- Memory (FR-MEM-12) -------------------------------------------------------------------------------------------

public sealed record MemoryPageInfoDto(string Path, long Size, DateTimeOffset? UpdatedAt = null);

public sealed record MemoryPageDto(string Path, string Content);

public sealed record SaveMemoryPageDto(string Path, string Content);

// Kind: Write, Delete or Archive.
public sealed record PendingMemoryChangeDto(string Id, string Path, string ProposedBy, string? Before, string After, DateTimeOffset At, string Kind = "Write", string? Reason = null);

public sealed record PendingChangeDecisionDto(bool Apply);

// ---- Routines (FR-RTN-04/08/09) -----------------------------------------------------------------------------------

// LastRuns: Up to the 20 most recent runs, newest first.
// ProjectedMonthlyCredits: Projected monthly credit cost the user must acknowledge to enable (FR-RTN-09).
// AutoPaused: Paused because the user has not interacted for too long; <c>confirm-resume</c> re-arms it.
public sealed record RoutineDto(string Id, string Owner, string Kind, bool Enabled, bool Paused, bool AutoPaused, decimal ProjectedMonthlyCredits, IReadOnlyList<RoutineRunDto> LastRuns);

public sealed record RoutineRunDto(DateTimeOffset At, string Status, decimal Credits, string? Model = null);

public sealed record EnableRoutineDto(decimal AcknowledgedMonthlyCredits);

/// <summary>Result of a dry run: approval-required tools were auto-denied and listed in <see cref="DeniedApprovals"/> (FR-RTN-08).</summary>
public sealed record DryRunResultDto(string Status, string? Output, IReadOnlyList<string> DeniedApprovals, decimal Credits);

// ---- Budgets (FR-OBS-02/03) ---------------------------------------------------------------------------------------

public sealed record BudgetsDto(
    IReadOnlyList<AgentBudgetDto> Agents,
    BudgetTotalDto Daily,
    BudgetTotalDto Monthly,
    IReadOnlyList<BudgetLineDto> ByModel,
    IReadOnlyList<BudgetLineDto> ByRoutine,
    QuotaDto? Quota);

public sealed record AgentBudgetDto(string Agent, decimal SpentToday, decimal DailyLimit, decimal SpentThisMonth, decimal MonthlyLimit, bool Exhausted);

public sealed record BudgetTotalDto(decimal Spent, decimal Limit);

public sealed record BudgetLineDto(string Key, decimal Credits);

/// <summary>Account-level credit quota; <see langword="null"/> in <see cref="BudgetsDto"/> when the quota source is unknown.</summary>
public sealed record QuotaDto(decimal Used, decimal Limit, DateTimeOffset? ResetsAt = null);

/// <summary>Legacy raw ledger view, kept for the existing <c>/api/ledger</c> route.</summary>
public sealed record LedgerDto(JsonElement Agents, JsonElement ByModel, JsonElement ByRoutine);

// ---- Audit / kill switch / alerts ---------------------------------------------------------------------------------

public sealed record AuditEntryDto(long Sequence, DateTimeOffset At, string Kind, string Decision, string? Agent = null, string? Tool = null, string? Detail = null, string? TraceId = null);

public sealed record AuditVerifyDto(bool Valid);

public sealed record KillSwitchDto(bool IsEngaged, string? Reason, DateTimeOffset? EngagedAt);

public sealed record KillSwitchRequestDto(string Reason);

public sealed record AlertDto(string Kind, string Severity, string Message, DateTimeOffset At, string? Agent = null);

// ---- Lifecycle (desktop only; FR-AGT-07) --------------------------------------------------------------------------

public sealed record CreateAgentDto(string Template, string Id, string Name);

public sealed record DuplicateAgentDto(string Id, string Name);

public sealed record SetHiddenDto(bool Hidden);

/// <summary>Outcome of a configuration change. <see cref="Errors"/> is empty when it was applied.</summary>
public sealed record LifecycleResultDto(bool Applied, IReadOnlyList<string> Errors, string? VersioningError = null);

// ---- Connections (FR-CON-07: edits desktop only) ------------------------------------------------------------------

// Health: Credential health from the broker; null when the broker has no data.
public sealed record ConnectionDto(string Id, string Kind, string DisplayName, bool Enabled, ConnectionHealthDto? Health = null);

// Status: ok, expiring, expired or unknown.
public sealed record ConnectionHealthDto(string Status, DateTimeOffset? ExpiresAt = null, string? Detail = null);

public sealed record SetConnectionEnabledDto(bool Enabled);

// ---- Push, presence, notification settings (FR-CLI-06/07) ---------------------------------------------------------

public sealed record VapidKeyDto(string PublicKey);

/// <summary>Mirrors <c>PushSubscription.toJSON()</c>. The server derives the device id from the authenticated device.</summary>
public sealed record PushSubscriptionDto(string Endpoint, PushKeysDto Keys);

public sealed record PushKeysDto(string P256dh, string Auth);

public sealed record PresenceDto(bool Focused);

public sealed record NotificationSettingsDto(IReadOnlyList<AgentNotificationToggleDto> Agents, QuietHoursDto QuietHours);

public sealed record AgentNotificationToggleDto(string Agent, bool Enabled);

// Start: Local time of day, <c>HH:mm</c>.
// End: Local time of day, <c>HH:mm</c>.
public sealed record QuietHoursDto(bool Enabled, string Start, string End);

// ---- Tasks --------------------------------------------------------------------------------------------------------

public sealed record TaskDto(string Id, string Agent, string Summary, string Status);
