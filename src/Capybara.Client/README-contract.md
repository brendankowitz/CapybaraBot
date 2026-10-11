# Capybara gateway client contract (WP12 → WP11)

`CapybaraApiClient` (`CapybaraApiClient.cs`) and the DTOs in `CapybaraDtos.cs` are the source of truth. WP11 implements the
server side to match exactly. Rules that apply to every route:

- Base address is the gateway (`127.0.0.1`, published through Tailscale Serve). All `/api/*` routes and the hub require an
  authenticated device (`CapybaraAuthenticationHandler` bearer token + tailnet login); "desktop only" means the device's
  `ControlSurface` is `Desktop`, otherwise **403**. A device whose surface is `EventRelay` (the public webhook relay) gets
  **403** everywhere (every `/api` route, the hub, the shell) except `POST /api/events/{source}/{type}`; see "Event ingress".
- JSON is camelCase (`JsonSerializerDefaults.Web`). Enums are written as **strings** (`JsonStringEnumConverter`, already
  registered for REST) and read case-insensitively. **The SignalR hub JSON protocol must register the same converter**
  (`AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))`), and must
  publish DTO-shaped payloads (below) so the typed client handlers deserialize them.
- `AgentId` serializes as a plain string.
- The client never maps 404 to "empty". Every non-2xx throws `HttpRequestException` (with `StatusCode` and the response
  body, first 500 chars). Servers should answer errors with a short plain-text or ProblemDetails body.
- **Success responses with a DTO must be `application/json` (or `+json`).** The client throws on any other content type or on
  malformed JSON ("unexpected non-JSON response"), so a route the gateway does not serve can never be read as data.
- Status codes: 204 for commands without a result; 200 + DTO where a DTO is listed; 404 unknown id; 409 stale/conflict;
  400 invalid body; 403 desktop-only violation.
- Marking: **existing** = served today by `src\Capybara.Gateway\Endpoints\*.cs`; **changed** = served today but the shape
  differs from what the client expects, so the server must be updated; **NEW** = not served today.

Auth column: `any` = any authenticated device; `desktop` = desktop-surface devices only.

## SPA shell, fallback and headers (WP11)

- **Deep links.** Notification clicks open `/questions/{id}` and `/approvals`; the browser has no token on that first GET.
  `PwaStaticAssetPolicy.AllowsTokenlessGet` currently allows only `/`, `/index.html`, the manifest/service worker/favicon and
  the `/_framework/`, `/css/`, `/js/`, `/icons/` prefixes. It must **serve the SPA shell tokenless for every GET that is not
  under `/api/` and not the hub (`/hubs/`)**, so client-side routes load. The shell holds no data.
- **`/api/*` must never fall through to the SPA fallback** (`MapFallbackToFile` or the embedded shell). An unknown `/api/…`
  path answers 404 (JSON or plain text), never `200 text/html`.
- **Headers.** Send as response headers on the shell what `index.html` also sets as `<meta>` (defence in depth; a header
  also covers `frame-ancestors`, which a `<meta>` CSP cannot):
  `Content-Security-Policy: default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws: wss:; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'self'; form-action 'none'; frame-ancestors 'none'`,
  `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff`.

## Agents

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/agents` | – | `AgentStatusDto[]` | any | existing; **changed**: adds `hidden` (default false), `state` (`working` / `needsAttention` / `idle`, same values as the hub) and `unread` (int) so the list is right on load; the hub only updates it afterwards. Includes hidden agents. |
| POST | `/api/agents/{id}/pause` | – | 204 | any | existing |
| POST | `/api/agents/{id}/resume` | – | 204 | any | existing |
| POST | `/api/agents/{id}/read` | – | 204 | any | **NEW**; clears the agent's unread count (called when the chat page opens). |
| POST | `/api/agents/{id}/turn` | `TurnRequestDto{prompt, incognito, conversationId}` | `TurnResultDto` | any | existing; **changed** (see "Turn response"). |
| PUT | `/api/agents/{id}/model` | `SetModelDto{model}` | 204 | any | **NEW**; `pool.Get(agent).SetModelAsync(model)` |
| GET | `/api/agents/{id}/history?limit=100` | – | `ChatEntryDto[]` (oldest first) | any | **NEW**; user DMs and replies of the main session; user entries carry `messageId`, assistant entries `inReplyTo` (and `alsoInReplyTo`, see "Turn response"); incognito turns are not persisted. |
| DELETE | `/api/agents/{id}/incognito/{conversationId}` | – | 204 / 400 / 404 | any | **NEW** (review round 1); ends an incognito conversation: its CLI session (the transcript) is deleted once a turn in flight on it finishes. |
| GET | `/api/runtimes` | – | `RuntimeStatusDto[]` | any | existing |
| POST | `/api/runtimes/{id}/unquarantine` | – | 204 | any | existing |

### Turn response

`TurnResultDto { status, content?, error?, messageId?, routineId?, userMessagesJoined }`. `status` is the runtime's
`TurnStatus` (`Completed`, `Blocked`, `OverBudget`, `LimitExceeded`, `Throttled`, `Failed`), with HTTP **200 for all of
them**: the existing endpoint serialises `TurnResult` (extra fields such as `evidence` are ignored by the client). `content`
is the reply when `Completed`; `error` says why it did not run or failed. The pages always show status and error, so a turn
that did not run is never an empty line.

When the turn goes through the inbox (WP10/WP11), it may not be finished when the request returns. The server then answers
200 with `status: "Accepted"` and the inbox **`messageId` (required)**. **The reply is delivered through the chat history,
correlated by id, never by text:**

- `ChatEntryDto.messageId` on the **user** entry is that inbox message id (the same value the turn response returned).
- `ChatEntryDto.inReplyTo` on every **assistant** entry that answers it is that id. An agent-initiated message (heartbeat,
  proactive note, a routine's output) has **no** `inReplyTo`; a reply to another message has that message's id.
- A reply may have several parts: several assistant entries with the same `inReplyTo`, in order, **appended together** (one
  history write) so a client that sees the first sees them all.
- When user messages sent while a turn ran were joined into it, that turn's reply is stored **once**: `inReplyTo` is the first
  message's id and `alsoInReplyTo` (string array, absent otherwise) lists the others. An entry answers id X when `inReplyTo`
  is X or `alsoInReplyTo` contains X (`ChatEntryDto.Answers`, used by `ChatReplies.To`).
- The server may store a normalized/joined prompt as the user entry's content; ids make that irrelevant.

Clients poll `GET /api/agents/{id}/history` every ~2 s for up to ~3 minutes and show exactly the assistant entries whose
`inReplyTo` equals the `messageId`; nothing else is ever shown as the reply. Only if a server returns **no** `messageId`
does the client fall back: it snapshots the last history entry **before** sending, ignores everything up to it, takes the
first user entry after it as its own message (upgrading to id matching if that entry has a `messageId`) and the assistant
entries right after it as the reply. That fallback cannot tell an agent message interleaved after the user entry from the
reply, which is why ids are required. The server may instead wait for the reply (bounded, e.g. 60 s) and answer
`Completed`; both are valid.

## Lifecycle (FR-AGT-07; all desktop only)

| Method | Path | Request | Response | Notes |
|---|---|---|---|---|
| GET | `/api/lifecycle/templates` | – | `string[]` | **NEW**; `AgentLifecycleService.ListTemplates` |
| POST | `/api/agents` | `CreateAgentDto{template,id,name}` | `LifecycleResultDto` | **NEW**; 200 even when `applied=false` (errors listed); `ConfigPrincipal.Human(device login)` |
| POST | `/api/agents/{id}/duplicate` | `DuplicateAgentDto{id,name}` | `LifecycleResultDto` | **NEW**; profile, skills, routines; not memory or history |
| POST | `/api/agents/{id}/hide` | `SetHiddenDto{hidden}` | `LifecycleResultDto` | **NEW** |
| POST | `/api/agents/{id}/archive` | – | `LifecycleResultDto` | **NEW** |
| DELETE | `/api/agents/{id}` | – | `LifecycleResultDto` | **NEW**; runs the removal observers |

## Fleet thread, tasks, alerts, ledger

| Method | Path | Response | Auth | Notes |
|---|---|---|---|---|
| GET | `/api/thread` | `ThreadEntryDto[]` | any | existing (`FleetThreadEntry`; enums as strings) |
| GET | `/api/tasks` | `TaskDto[]` | any | **NEW** (not served today) |
| GET | `/api/alerts` | `AlertDto[]` | any | existing |
| GET | `/api/ledger` | `LedgerDto` | any | existing, raw JSON; superseded by `/api/budgets` for the PWA |

## Approvals (FR-SAF-04)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/approvals` | – | `ApprovalDto[]` | any | existing; **changed**: adds `reviewerVerdict`, `reviewerRationale`, `origin` and **`argsJson`** (below). |
| POST | `/api/approvals/{id}` | `ApprovalDecisionDto{nonce, decision}` | 204 / 404 / 409 | any | existing. `decision` ∈ `AllowOnce`, `AlwaysAllow`, `Deny` (`ApprovalDecisions`). |
| POST | `/api/approvals/{id}/act?nonce=&decision=` | – | 204 / 404 / 409 | any | **NEW** (WP6 push action). Same settlement as above; nonce is required. |

**`preview` vs `argsJson` (important).** `preview` is the server text `"{agent} → {tool} {canonical args json}"`, **at most
2000 characters, and it may be cut mid-JSON**. `PolicyGate` sorts the canonical JSON by key, so for email the `body`
sorts before `cc`/`subject`/`to` and a long body cuts the recipient off the end. The server **must** therefore also send
**`argsJson`: the full, untruncated canonical args JSON** (the same JSON the `argsHash` is computed over).

Client behaviour (so WP11 knows what is guaranteed): when `argsJson` is present the email/diff/args views are built from it
and the card can be approved. The card shows **Deny only** (Allow once/Always disabled; the TUI refuses `allow`, even with
`--yes`) when: `preview` is cut and no `argsJson`; the arguments cannot be parsed; two argument names collide after NFKC,
case folding and removal of invisible characters (e.g. `to` / `To` / `t<ZWSP>o` / fullwidth `ｔｏ`); **any argument name is
non-ASCII or contains an invisible character** (tool argument names are ASCII identifiers, so a look-alike name is an
attack); or the **recipient (`to`/`cc`) contains an invisible, bidi or control character**. Non-ASCII letters in a
recipient are allowed with a warning and shown as code points (`sam@⟦U+0456⟧nternal.com`).

**Every agent-supplied string on a card is revealed, never dropped:** each control character (except newline/tab), format
character (bidi controls U+202A–202E / U+2066–2069, zero-width, tag characters), line/paragraph separator, non-ASCII space,
variation selector, private-use/unassigned code point and unpaired surrogate is replaced by a visible token such as
`⟦U+202E⟧` (`ApprovalPreview.Reveal`). The exact arguments text is always shown in a `<details>` (revealed). Email bodies are
shown as plain text with every link/image target and every script/style/comment content listed as text; nothing is
rendered as HTML. The TUI shows the same revealed view and asks for confirmation before it sends an allow (`--yes` skips
only the question).

## Questions (`PhoneUserEscalation` deep link `/questions/{id}`)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/questions/{id}` | – | `QuestionDto` | any | **NEW**; 404 once answered or expired (the PWA shows "no longer pending"). |
| POST | `/api/questions/{id}` | `QuestionAnswerDto{answer}` | 204 / 404 | any | **NEW**; `IUserEscalation.SubmitAnswer` |

## Proposals (FR-AGT-06)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/proposals?status=` | – | `ProposalDto[]` | any | **NEW** (not served today). No `status` = all, newest first. |
| GET | `/api/proposals/{id}` | – | `ProposalDto` | any | **NEW** |
| POST | `/api/proposals/{id}` | `ProposalDecisionDto{approve}` | `ProposalDto` (after decision) | any | **NEW**; `ProposalService.DecideAsync(…, ConfigPrincipal.Human(device login))`. Result status may be `Stale`/`Invalid` (still 200). 404 unknown id. |

## Memory (FR-MEM-12; all edits go through `MemoryAdmin` as `MemoryAdminPrincipal.Human`)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/memory/pages?prefix=` | – | `MemoryPageInfoDto[]` | any | **NEW** |
| GET | `/api/memory/page?path=` | – | `MemoryPageDto` | any | **NEW** |
| PUT | `/api/memory/page` | `SaveMemoryPageDto{path,content}` | 204 | any | **NEW** |
| DELETE | `/api/memory/page?path=` | – | 204 / 404 | any | **NEW** |
| GET | `/api/memory/pending` | – | `PendingMemoryChangeDto[]` | any | existing; **changed** DTO (`proposedBy`, `before`, `after`, `at`, `kind`, `reason`) = `PendingUserChange`. |
| POST | `/api/memory/pending/{id}` | `PendingChangeDecisionDto{apply}` | 204 / 404 / 409 | any | existing |
| GET | `/api/memory/export` | – | `application/zip` stream | any | **NEW**; `Content-Disposition: attachment; filename=capybara-memory.zip` |

## Routines (FR-RTN-04/08/09; mutating calls are human-only: `RoutinePrincipal.Human(device login)`)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/routines` | – | `RoutineDto[]` | any | **NEW**; `lastRuns` = newest 20; `projectedMonthlyCredits` = current projection. |
| POST | `/api/routines/{id}/enable` | `EnableRoutineDto{acknowledgedMonthlyCredits}` | 204 / 404 / **409** | any | **NEW**; 409 unless the acknowledgement equals the current projection; body names the current projection. |
| POST | `/api/routines/{id}/disable` | – | 204 / 404 | any | **NEW** |
| POST | `/api/routines/{id}/dry-run` | – | `DryRunResultDto` | any | **NEW**; `RoutineDispatcher.DryRunAsync`; approval-required tools auto-denied and listed. |
| POST | `/api/routines/{id}/confirm-resume` | – | 204 / 404 | **see note** | **NEW**; `RoutineAutoPauser.ConfirmResume`. |

**Resume authority (FR-SAF-06).** A routine that was paused by the **kill switch** can only be resumed from a **desktop**
device (403 otherwise), the same as releasing the kill switch. A routine that was merely auto-paused (no user interaction
for N days) or paused by the user can be resumed/confirmed from **any** device.

## Budgets, audit, kill switch

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/budgets` | – | `BudgetsDto` | any | **NEW** (not served today): per-agent rows from `CostLedger.GetStatus`, daily/monthly fleet totals (`FleetMonthlyLimit`), `ByModel`, `ByRoutine`, `quota` (`QuotaMonitor.Latest`, null when unknown). |
| GET | `/api/audit?limit=100` | – | `AuditEntryDto[]` (newest first) | any | **NEW**; `IAuditLog.ReadAllAsync` |
| GET | `/api/audit/verify` | – | `AuditVerifyDto` | any | existing |
| GET | `/api/killswitch` | – | `KillSwitchDto` | any | existing |
| POST | `/api/killswitch` | `KillSwitchRequestDto{reason}` | 204 | **any** | existing; engage from any device |
| DELETE | `/api/killswitch` | – | 204 / 403 | **desktop** | existing |

## Connections (FR-CON-07)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/connections` | – | `ConnectionDto[]` | any | **NEW**; `health` from the broker credential health (`ok` / `expiring` / `expired` / `unknown`). |
| PUT | `/api/connections/{id}/enabled` | `SetConnectionEnabledDto{enabled}` | 204 / 404 | **desktop** | **NEW**; the only connection edit the clients expose; adding/removing stays config-file/UI-human work. |

## Push, presence, notification settings (FR-CLI-06/07)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| GET | `/api/push/vapid-key` | – | `VapidKeyDto{publicKey}` | any | **NEW**; base64url uncompressed P-256 public key for `pushManager.subscribe({applicationServerKey})` |
| POST | `/api/push/subscriptions` | `PushSubscriptionDto{endpoint, keys{p256dh,auth}}` | 204 | any | **NEW**; device id = authenticated device → `WebPushSubscription` |
| DELETE | `/api/push/subscriptions?endpoint=` | – | 204 | any | **NEW** |
| POST | `/api/presence` | `PresenceDto{focused}` | 204 | any | **NEW** (not served today); drives `IPresenceTracker`. |
| GET | `/api/settings/notifications` | – | `NotificationSettingsDto` | any | **NEW**; per-agent toggles (default enabled) + quiet hours |
| PUT | `/api/settings/notifications` | `NotificationSettingsDto` | 204 | any | **NEW**; `ChannelOptions.AgentNotificationToggles` / `QuietHours` |

**Presence expiry.** The PWA sends `focused: true` immediately on focus and then **every 30 s while visible and focused**, and
`focused: false` on blur, `visibilitychange` to hidden and `pagehide` (a `fetch` with `keepalive`, which carries the bearer
token; `sendBeacon` cannot). The server must **treat `focused` as expired ~60 s after the last refresh** (the existing
`ChannelOptions.PresenceFocusTtl` is 45 s; either is fine as long as it exceeds the 30 s heartbeat), so a crashed or
backgrounded tab cannot suppress push indefinitely. Approvals bypass focus suppression regardless.

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
agent) for live lines; a turn's reply arrives in the `turn` response or, for `Accepted`, in the history (see "Turn response").

## Service worker

`wwwroot/service-worker.js` only displays push and routes clicks; it has **no `fetch` handler** (API responses are never
cached) and never calls `/api/*` (acting on an approval needs the device token, which only the page holds). Push payloads
should keep `url` as a same-origin path (e.g. `/approvals`, `/questions/{id}`); anything else is replaced by `/`. If a payload
carries `actions` (`[{action, title}]`, max 2) a click on one just opens the approval in the PWA. The nonce-bound
`/api/approvals/{id}/act` route is for ntfy action buttons, not for the browser.

## Incognito conversations (FR-MEM-12)

`TurnRequestDto{incognito: true, conversationId}` runs in the session `agent_{id}_incognito_{conversationId}`, which reads and
writes no memory and is never in the persisted history. With no `conversationId` the turn is **one-shot**: the server picks an
id it never returns and deletes the session right after the turn. A named conversation's session is deleted when the client
ends it (`DELETE /api/agents/{id}/incognito/{conversationId}`), after `Capybara:Incognito:IdleTimeout` (default 30 min) without
use, or at the next capybarad start (conversations do not survive a restart).

## Event ingress (FR-RTN-07)

| Method | Path | Request | Response | Auth | Notes |
|---|---|---|---|---|---|
| POST | `/api/events/{source}/{type}` | raw body, at most 256 KiB, header `X-Hub-Signature-256: sha256={hex HMAC-SHA256 of the body}` | 202 / 403 / 404 / 413 / 503 | **EventRelay or desktop** | `source` must be configured (`Capybara:Events:Sources:{source}`) with a secret, else 404; a bad signature is 403; a phone or TUI device is 403. The payload is always tainted and routed to event routines after the 202 (503 when the queue is full: retry). |

The relay that forwards public webhooks onto the tailnet holds a device token with `Surface: EventRelay`, which opens this
route and nothing else.