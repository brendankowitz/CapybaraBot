# Capybara gateway client contract (WP12 → WP11)

`CapybaraApiClient` (`CapybaraApiClient.cs`) and the DTOs in `CapybaraDtos.cs` are the source of truth. WP11 implements the
server side to match exactly. Rules that apply to every route:

- Base address is the gateway (`127.0.0.1`, published through Tailscale Serve). All routes require an authenticated device
  (`CapybaraAuthenticationHandler` bearer token + tailnet login); "desktop only" means the device's `ControlSurface` is
  `Desktop`, otherwise **403**.
- JSON is camelCase (`JsonSerializerDefaults.Web`). Enums are written as **strings** (`JsonStringEnumConverter`, already
  registered for REST) and read case-insensitively. **The SignalR hub JSON protocol must register the same converter**
  (`AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))`), and must
  publish DTO-shaped payloads (below) so the typed client handlers deserialize them.
- `AgentId` serializes as a plain string.
- The client never maps 404 to "empty". Every non-2xx throws `HttpRequestException` (with `StatusCode` and the response
  body, first 500 chars). Servers should answer errors with a short plain-text or ProblemDetails body.
- Status codes: 204 for commands without a result; 200 + DTO where a DTO is listed; 404 unknown id; 409 stale/conflict;
  400 invalid body; 403 desktop-only violation.
- Routes marked **existing** are already served by `src\Capybara.Gateway\Endpoints\*.cs`. Where the DTO changed, the
  server must be updated to the new shape (marked **changed**).

Auth column: `any` = any authenticated device; `desktop` = desktop-surface devices only.

## Agents

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/agents` | – | `AgentStatusDto[]` | any | existing; **changed**: adds `hidden` (default false). Includes hidden agents. |
| POST | `/api/agents/{id}/pause` | – | 204 | any | existing |
| POST | `/api/agents/{id}/resume` | – | 204 | any | existing |
| POST | `/api/agents/{id}/turn` | `TurnRequestDto{prompt, incognito, conversationId}` | `TurnResultDto` | any | existing; **changed**: goes through the inbox as `MessageOrigin.User`. `incognito=true` enqueues with `SessionIds.Incognito(agent, conversationId)` (no memory read/write; session ended after the reply). |
| PUT | `/api/agents/{id}/model` | `SetModelDto{model}` | 204 | any | new; `pool.Get(agent).SetModelAsync(model)` |
| GET | `/api/agents/{id}/history?limit=100` | – | `ChatEntryDto[]` (oldest first) | any | new; user DMs and replies of the main session; incognito turns are not persisted. |
| GET | `/api/runtimes` | – | `RuntimeStatusDto[]` | any | existing |
| POST | `/api/runtimes/{id}/unquarantine` | – | 204 | any | existing |

## Lifecycle (FR-AGT-07; all desktop only)

| Method | Path | Request | Response | Notes |
|---|---|---|---|---|
| GET | `/api/lifecycle/templates` | – | `string[]` | `AgentLifecycleService.ListTemplates` |
| POST | `/api/agents` | `CreateAgentDto{template,id,name}` | `LifecycleResultDto` | 200 even when `applied=false` (errors listed); `ConfigPrincipal.Human(device login)` |
| POST | `/api/agents/{id}/duplicate` | `DuplicateAgentDto{id,name}` | `LifecycleResultDto` | profile, skills, routines; not memory or history |
| POST | `/api/agents/{id}/hide` | `SetHiddenDto{hidden}` | `LifecycleResultDto` | |
| POST | `/api/agents/{id}/archive` | – | `LifecycleResultDto` | |
| DELETE | `/api/agents/{id}` | – | `LifecycleResultDto` | runs the removal observers |

## Fleet thread, tasks, alerts, ledger

| Method | Path | Response | Auth | Notes |
|---|---|---|---|---|
| GET | `/api/thread` | `ThreadEntryDto[]` | any | existing (`FleetThreadEntry`; enums as strings) |
| GET | `/api/tasks` | `TaskDto[]` | any | |
| GET | `/api/alerts` | `AlertDto[]` | any | existing |
| GET | `/api/ledger` | `LedgerDto` | any | existing, raw JSON; superseded by `/api/budgets` for the PWA |

## Approvals (FR-SAF-04)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/approvals` | – | `ApprovalDto[]` | any | existing; **changed**: adds `reviewerVerdict`, `reviewerRationale`, `origin`. `preview` stays the server text `"{agent} → {tool} {canonical args json}"` (≤2000 chars); the client builds email/diff/args views from it (`ApprovalPreview`). |
| POST | `/api/approvals/{id}` | `ApprovalDecisionDto{nonce, decision}` | 204 / 404 / 409 | any | existing. `decision` ∈ `AllowOnce`, `AlwaysAllow`, `Deny` (`ApprovalDecisions`). |
| POST | `/api/approvals/{id}/act?nonce=&decision=` | – | 204 / 404 / 409 | any | new (WP6 push action). Same settlement as above; nonce is required. |

## Questions (`PhoneUserEscalation` deep link `/questions/{id}`)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/questions/{id}` | – | `QuestionDto` | any | 404 once answered or expired (the PWA shows "no longer pending"). |
| POST | `/api/questions/{id}` | `QuestionAnswerDto{answer}` | 204 / 404 | any | `IUserEscalation.SubmitAnswer` |

## Proposals (FR-AGT-06)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/proposals?status=` | – | `ProposalDto[]` | any | **changed** DTO (Agent, Kind, Name, TargetPath, Diff, Rationale, Status, …). No `status` = all, newest first. |
| GET | `/api/proposals/{id}` | – | `ProposalDto` | any | new |
| POST | `/api/proposals/{id}` | `ProposalDecisionDto{approve}` | `ProposalDto` (after decision) | any | `ProposalService.DecideAsync(…, ConfigPrincipal.Human(device login))`. Result status may be `Stale`/`Invalid` (still 200). 404 unknown id. |

## Memory (FR-MEM-12; all edits go through `MemoryAdmin` as `MemoryAdminPrincipal.Human`)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/memory/pages?prefix=` | – | `MemoryPageInfoDto[]` | any | |
| GET | `/api/memory/page?path=` | – | `MemoryPageDto` | any | |
| PUT | `/api/memory/page` | `SaveMemoryPageDto{path,content}` | 204 | any | |
| DELETE | `/api/memory/page?path=` | – | 204 / 404 | any | |
| GET | `/api/memory/pending` | – | `PendingMemoryChangeDto[]` | any | existing; **changed** DTO (`proposedBy`, `before`, `after`, `at`, `kind`, `reason`) = `PendingUserChange`. |
| POST | `/api/memory/pending/{id}` | `PendingChangeDecisionDto{apply}` | 204 / 404 / 409 | any | existing |
| GET | `/api/memory/export` | – | `application/zip` stream | any | `Content-Disposition: attachment; filename=capybara-memory.zip` |

## Routines (FR-RTN-04/08/09; mutating calls are human-only: `RoutinePrincipal.Human(device login)`)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/routines` | – | `RoutineDto[]` | any | `lastRuns` = newest 20; `projectedMonthlyCredits` = current projection. |
| POST | `/api/routines/{id}/enable` | `EnableRoutineDto{acknowledgedMonthlyCredits}` | 204 / 404 / **409** | any | 409 unless the acknowledgement equals the current projection; body names the current projection. |
| POST | `/api/routines/{id}/disable` | – | 204 / 404 | any | |
| POST | `/api/routines/{id}/dry-run` | – | `DryRunResultDto` | any | `RoutineDispatcher.DryRunAsync`; approval-required tools auto-denied and listed. |
| POST | `/api/routines/{id}/confirm-resume` | – | 204 / 404 | any | `RoutineAutoPauser.ConfirmResume` |

## Budgets, audit, kill switch

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/budgets` | – | `BudgetsDto` | any | **changed** (typed): per-agent rows from `CostLedger.GetStatus`, daily/monthly fleet totals (`FleetMonthlyLimit`), `ByModel`, `ByRoutine`, `quota` (`QuotaMonitor.Latest`, null when unknown). |
| GET | `/api/audit?limit=100` | – | `AuditEntryDto[]` (newest first) | any | new; `IAuditLog.ReadAllAsync` |
| GET | `/api/audit/verify` | – | `AuditVerifyDto` | any | existing |
| GET | `/api/killswitch` | – | `KillSwitchDto` | any | existing |
| POST | `/api/killswitch` | `KillSwitchRequestDto{reason}` | 204 | **any** | existing; engage from any device |
| DELETE | `/api/killswitch` | – | 204 / 403 | **desktop** | existing |

## Connections (FR-CON-07)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/connections` | – | `ConnectionDto[]` | any | `health` from the broker credential health (`ok` / `expiring` / `expired` / `unknown`). |
| PUT | `/api/connections/{id}/enabled` | `SetConnectionEnabledDto{enabled}` | 204 / 404 | **desktop** | The only connection edit the clients expose; adding/removing stays config-file/UI-human work. |

## Push, presence, notification settings (FR-CLI-06/07)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/push/vapid-key` | – | `VapidKeyDto{publicKey}` | any | base64url uncompressed P-256 public key for `pushManager.subscribe({applicationServerKey})` |
| POST | `/api/push/subscriptions` | `PushSubscriptionDto{endpoint, keys{p256dh,auth}}` | 204 | any | device id = authenticated device → `WebPushSubscription` |
| DELETE | `/api/push/subscriptions?endpoint=` | – | 204 | any | |
| POST | `/api/presence` | `PresenceDto{focused}` | 204 | any | existing intent; drives `IPresenceTracker` |
| GET | `/api/settings/notifications` | – | `NotificationSettingsDto` | any | per-agent toggles (default enabled) + quiet hours |
| PUT | `/api/settings/notifications` | `NotificationSettingsDto` | 204 | any | `ChannelOptions.AgentNotificationToggles` / `QuietHours` |

## SignalR hub `/hubs/capybara` (events the client subscribes to)

| Event | Payload | Typed handler |
|---|---|---|
| `AgentStatusChanged` | `(string agentId, string state, int unread)`; state ∈ `working`, `needsAttention`, `idle` | `OnAgentStatusChanged` |
| `ThreadEntry` | `ThreadEntryDto` | `OnThreadEntry` |
| `ApprovalRequested` | `ApprovalDto` | `OnApprovalRequested` |
| `ApprovalResolved` | `string approvalId` | `OnApprovalResolved` |
| `QuestionRequested` | `QuestionDto` | `OnQuestionRequested` |
| `Alert` | `AlertDto` | `OnAlert` |
| `KillSwitchChanged` | `bool engaged` | `OnKillSwitchChanged` |

The per-agent chat view uses `GET /api/agents/{id}/history` for backlog and `ThreadEntry` (entries whose `from`/`to` is the
agent) plus the `turn` response for live lines.
