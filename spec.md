# Build a hardened personal agent fleet on Copilot

Grok Bot's capabilities are worth copying, but its architecture is not. Its official docs describe **one cloud computer per user, shared by every Bot**, with shared sign-ins and files. xAI says outright that **"Separate Bots are not a security boundary"** ([Grok docs: Computer and apps](https://docs.x.ai/grok-bot/computer-and-apps); [Grok docs: Approvals](https://docs.x.ai/grok-bot/approvals-security-and-privacy)). The platform specified here keeps Grok Bot's user-facing model:

- named persistent agents with standing rules
- asynchronous handoffs between agents
- routines on a schedule or an event
- a model-based reviewer plus approval cards
- phone access

It reverses Grok Bot's isolation choices. Each agent gets its own Copilot runtime with its own `BaseDirectory`, its own tool allowlist and its own grants on connections. Agents get capabilities from a credential broker, not shared cookies. A deterministic policy layer sits in front of an LLM reviewer, which is never the only gate.

The GitHub Copilot SDK fits as the runtime. It went GA on 2026-06-02, the .NET package is at 1.0.19, and it supports the multi-client isolation pattern above ([GA changelog](https://github.blog/changelog/2026-06-02-copilot-sdk-is-now-generally-available/); [multi-tenancy.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/setup/multi-tenancy.md)). It comes with three constraints that shape the spec:

- **Token billing.** Since 2026-06-01 every agent-loop iteration is billed in GitHub AI Credits (Pro+ = 7,000 credits/month at $0.01 each), so an always-on fleet needs hard budgets from the first day ([GitHub billing docs](https://docs.github.com/en/enterprise-cloud@latest/copilot/concepts/billing/usage-based-billing-for-individuals)).
- **No supervision.** The SDK does not restart crashed runtimes and does not lock sessions.
- **Experimental surface.** Many of the advanced APIs are still marked experimental.

OpenClaw's 2026 incidents set the security baseline:

- tens of thousands of gateways exposed to the internet
- one-click RCE (CVE-2026-25253)
- a poisoned skill marketplace
- a zero-click injection that rewrote the agent's persona file and scheduled its own re-infection

Together they lead to four Must requirements: bind to loopback only, allow only humans to change configuration, add no marketplace, and gate external actions with the "Rule of Two".

The MVP is three agents (executive assistant, coding, research) on a Linux mini-PC under systemd, reached through a PWA over Tailscale plus ntfy push. A pluggable `IAgentHost` lets the same agents move later to Azure Container Apps Sandboxes.

## Part I — Research context

### Grok Bot is many personas on one shared computer

Grok Bot comes from "SpaceXAI", the combination of xAI and Cursor, and runs on Cursor's infrastructure. The beta launched on **11 Aug 2026**. It is bundled with paid Cursor and SuperGrok plans rather than sold on its own ([Constellation Research](https://www.constellationr.com/insights/news/spacexai-launches-grok-bot-work-assistant); [Grok docs FAQ](https://docs.x.ai/grok-bot/faq)). Its user-facing model:

- **Bots.** A Bot is a named "AI teammate" with a name, a job, an avatar and a description. The description holds lasting rules such as "Never send external messages without approval". The docs recommend a separate Bot for each distinct goal, tool set, approval boundary or schedule ([Grok docs: Bots](https://docs.x.ai/grok-bot/bots)).
- **Messaging between Bots.** A Bot can send another Bot an **asynchronous message that wakes the recipient**. Group chats of **2–6 Bots** let the Bots "decide who responds" unless you @-mention one, and "a coordinator Bot can assign work" ([Grok docs: Message and collaborate](https://docs.x.ai/grok-bot/chat-and-collaboration)).
- **Routines.** A routine binds one Bot to a schedule (at least **5 minutes** apart) or to a Slack or GitHub event. Limits are **50 routines per Bot** and the **20 most recent run records**. Unattended routines pause after a long absence ([Grok docs: Skills, routines](https://docs.x.ai/grok-bot/skills-routines-and-automations)).
- **Auto Review.** A model-based reviewer checks each tool call or computer action before it runs. "Ask first" rules always beat "Allow automatically" rules. Approval cards offer Allow once, Always allow or Deny. Approvals that come from routines, triggers or other Bots **expire after about 10 minutes** ([Grok docs: Approvals](https://docs.x.ai/grok-bot/approvals-security-and-privacy)).

Several things that secondary sources report about Grok Bot are wrong or unverified, and the spec should not inherit them:

| Claim | Official docs | Conflicting or unverified source | Spec stance |
|---|---|---|---|
| Computer per agent | One computer per user, shared by all Bots; each Bot gets its own "screen", but "Screens are separate work surfaces, not security boundaries" ([docs](https://docs.x.ai/grok-bot/computer-and-apps)) | MindStudio says "each [agent has its] own cloud computer" ([MindStudio](https://www.mindstudio.ai/blog/grok-bot-ai-agent-fleet/)); Analytics Vidhya says the same ([AV](https://www.analyticsvidhya.com/?p=257374)) | Trust the docs. Treat Grok's sharing as an anti-pattern. |
| Three memory layers (user / agent / project) | October docs describe only per-Bot memory ("stable working preferences, important facts, and summaries"), which "is not an authoritative source" ([docs](https://docs.x.ai/grok-bot/bots)) | From @mattyp's launch post ([repost](https://youmind.com/landing/x-viral-articles/grok-bot-ai-computer-agent)); "Projects" are absent from current docs | Adopt the three layers as our own design, not as a Grok fact. |
| Routing tasks to the agent whose description fits | Not documented; docs cover @-mentions, self-selection in groups, and coordinators | MindStudio only | Provide a `list_agents` directory so this can work, but don't rely on it. |
| Models | "No model picker because Cursor manages model selection" ([docs](https://docs.x.ai/grok-bot/settings-and-notifications)) | Forum users saw `claude-opus-5-thinking-low` ([Cursor forum](https://forum.cursor.com/t/grok-bot-draining-cursor-credit-pool/169982)); Vellum says "locked to xAI models" | Route models per agent, and show which model incurred each cost. |
| Pricing | Bundled; no standalone plan | Vellum says "$200/month standalone" | Not relevant to a self-built system. |

The documented failures are what this build should fix:

- **Opaque routing drained credits.** One user lost about $100 of credits overnight, and another saw "52M of Claude Opus tokens" consumed at 38% weekly usage ([Cursor forum](https://forum.cursor.com/t/grok-bot-draining-cursor-credit-pool/169982)).
- **Routines silently failed to schedule** because they were "the only part of Grok Bot that checks the Cursor plan" ([Cursor forum](https://forum.cursor.com/t/bot-routines-refuse-to-fire/171173)).
- **Memory can't be inspected or exported.** Vellum, a competitor, reports this ([Vellum](https://www.vellum.ai/md/blog/official-grok-bot-breakdown)).

### Comparable systems converge on one daemon, markdown memory and SKILL.md

Every serious open system has the same core. One long-running local gateway owns the sessions, channel adapters, scheduler, markdown memory and SKILL.md skills. The systems differ mainly in how they isolate agents.

**The two Copilot-SDK-native projects are the closest templates.** Burke Holland's **Max** runs one persistent orchestrator session plus at most **5 concurrent worker sessions**, each destroyed when its task completes. Its pieces:

- a single serialized message queue tagged by channel
- an HTTP/SSE API on `127.0.0.1:7777` with a bearer token stored in a 0600 file
- a Telegram adapter that accepts one user ID and silently ignores everyone else ([Max architecture](https://mintlify.wiki/burkeholland/max/concepts/architecture))

Max's memory is a markdown wiki. Every message carries a **ranked table of contents, not page bodies**, and daily summaries are written asynchronously ([Max README](https://github.com/burkeholland/max)). **Octopal** adds a git-synced Obsidian vault in PARA layout and resolves skills in three tiers: bundled, vault, local ([Octopal](https://github.com/RyanHecht/octopal)). Neither project documents scheduling, permissions or isolation, so those are this spec's original work.

**OpenClaw's Copilot plugin shows where to split the work.** The Copilot CLI owns the agent loop, native tools, compaction and thread state. The host owns channels, approvals, the visible transcript, routing and bridged tools. `copilotHome` defaults to one directory **per agent** ([OpenClaw Copilot plugin](https://docs.openclaw.ai/plugins/copilot)).

OpenClaw also separates two kinds of scheduled work:

- **Heartbeat:** a scheduled turn in the agent's main session, every **30 minutes** by default, limited to active hours and delivered to the owner's DM.
- **Cron jobs:** each runs in its own isolated session ([OpenClaw heartbeat](https://docs.openclaw.ai/gateway/heartbeat)).

OpenClaw sub-agents get **no session or messaging tools by default** ([OpenClaw sub-agents](https://docs.openclaw.ai/tools/subagents)).

**Hermes has the most mature defaults to copy:**

- Always-injected `MEMORY.md` and `USER.md` are capped at about **2,200 and 1,375 characters**, and the full archive is searched with SQLite FTS5 rather than injected ([Hermes Atlas](https://hermesatlas.com/guide/memory/)).
- Agent-written skills can be staged for approval under `pending/` ([Hermes skills](https://hermes-agent.nousresearch.com/docs/user-guide/features/skills)).
- Gateway auth is deny-by-default, unanswered approvals fail closed, and context files are scanned for injection ([Hermes security](https://hermes-agent.nousresearch.com/docs/user-guide/security)).

**NanoClaw shows the isolation end state:** a container per session, mounts only where explicitly granted, a credential vault so "agents never hold raw API keys", and locked-down egress ([nanoclaw.dev](https://nanoclaw.dev/)).

The multi-agent evidence argues against building more agents than needed:

- **Token cost.** Anthropic measured multi-agent systems at about **15× the tokens of a chat** ([Anthropic Engineering](https://www.anthropic.com/engineering/multi-agent-research-system)).
- **Failure modes.** The MAST taxonomy finds the most common failures are **step repetition (15.7%)**, **reasoning–action mismatch (13.2%)** and **unawareness of termination conditions (12.4%)**. Giving one role the final say on termination raised success by **+9.4%**, and adding an objective verification step raised it by **+15.6%** ([arXiv 2503.13657](https://arxiv.org/html/2503.13657)).
- **Hard limits in practice.** Claude Code's agent teams forbid nested teams. Messages from other agents "can't grant permission or consent on the user's behalf" ([Claude Code docs](https://code.claude.com/docs/en/agent-teams)).

These findings drive the depth limit, the envelope, single ownership of termination, and the "agent messages are data" rule below.

### Copilot SDK facts that constrain the design

The SDK is a thin JSON-RPC client. The agent loop runs in a separate Copilot CLI runtime process: by default a bundled child process over stdio, pinned to CLI `1.0.96-0` on main. The SDK "does not control the loop" ([agent-loop.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/agent-loop.md); [dotnet README](https://raw.githubusercontent.com/github/copilot-sdk/main/dotnet/README.md)). The facts that matter for the spec:

- **Isolation per agent.** `CopilotClientOptions.BaseDirectory` sets `COPILOT_HOME`, and the docs say to "give each instance a distinct directory" ([multi-tenancy.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/setup/multi-tenancy.md)).
- **Empty mode.** `CopilotClientMode.Empty` requires `BaseDirectory`, requires `AvailableTools` on every session (no tools by default), turns off built-in skills and memory, and stores credentials under `COPILOT_HOME` instead of the keychain. The default `CopilotCli` mode carries a source comment: "Do not use this mode for server-based multi-user applications" ([Types.cs](https://github.com/github/copilot-sdk/blob/main/dotnet/src/Types.cs)).
- **Persistence and resume.** Resumable sessions require caller-supplied session IDs. Callback-style config (hooks, handlers, `SkillProvider`, `ManagedSettings`, `GitHubTokenProvider`) **is not persisted and silently drops on resume** unless you re-supply it ([session-persistence.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/session-persistence.md); [dotnet README](https://raw.githubusercontent.com/github/copilot-sdk/main/dotnet/README.md)).
- **What the host must build.** The SDK has "no built-in session locking", "no idle timeout by default", and "no built-in auth between SDK and CLI" over TCP ([scaling.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/setup/scaling.md)). `autoRestart` was removed in v0.2.0, so the host must supervise runtimes ([CHANGELOG](https://raw.githubusercontent.com/github/copilot-sdk/main/CHANGELOG.md)).
- **Control points.**
  - `OnPreToolUse` can allow, deny, ask or modify arguments.
  - `OnPermissionRequest` returns `ApproveOnce`, `Reject`, `UserNotAvailable` and other decisions.
  - `ManagedSettings` (v1.0.13) injects deny/ask rules such as `shell(rm*)`.
  - `OnUserInputRequest` and elicitation route `ask_user`.

  The docs **conflict on what happens when no permission handler is set**: the .NET README says requests stay pending, while the compatibility doc says deny-by-default. The spec always sets an explicit handler ([dotnet README](https://raw.githubusercontent.com/github/copilot-sdk/main/dotnet/README.md); [compatibility.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/troubleshooting/compatibility.md)).
- **Cost controls.** `assistant.usage` fires once per model call, sub-agents included, and "there are no hidden calls" ([agent-loop.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/agent-loop.md)). `SessionLimits.MaxAiCredits` is a **soft** cap checked after model calls return, so a single response can overshoot it ([session-limits.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/session-limits.md)).
- **Billing.** GitHub AI Credits replaced premium requests on 2026-06-01, at **1 credit = $0.01**. Monthly allowances are **Pro 1,500, Pro+ 7,000, Max 20,000**. Auto model selection gets a **10% discount**, and nothing rolls over ([GitHub billing docs](https://docs.github.com/en/enterprise-cloud@latest/copilot/concepts/billing/usage-based-billing-for-individuals); [GitHub blog](https://github.blog/2026-05-01-github-copilot-is-moving-to-usage-based-billing/)). Under the old model, tool calls inside a turn were free. Now every loop iteration costs tokens, so heartbeats and tool-heavy routines are real spend. BYOK usage does not count against Copilot quotas ([byok.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/auth/byok.md)).
- **Terms of service.** No GitHub term found explicitly forbids a personal daemon on the official SDK, and the SDK docs endorse "background worker" patterns ([backend-services.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/setup/backend-services.md)). The binding constraints:
  - "Your login may only be used by one person" ([GitHub ToS](https://docs.github.com/en/site-policy/github-terms/github-terms-of-service)).
  - The Acceptable Use Policy bans "excessive automated bulk activity" ([GitHub AUP](https://docs.github.com/en/site-policy/acceptable-use-policies/github-acceptable-use-policies)).
  - Rate limits are unpublished ([rate limits](https://docs.github.com/en/copilot/concepts/rate-limits)).
- **Headless auth.** Use a fine-grained PAT owned by the personal account, with the **Copilot Requests** permission. Classic `ghp_` tokens are not supported ([authenticate.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/auth/authenticate.md); [GitHub CLI auth](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/authenticate-copilot-cli)).
- **Known risks.**
  - In issue #251, custom agents were silently ignored when hooks or user-input callbacks were enabled. Whether it is closed could not be confirmed ([#251](https://github.com/github/copilot-sdk/issues/251)).
  - The docs tell you to "pin both the SDK and the Copilot CLI runtime" if you use experimental RPCs ([usage-and-billing.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/usage-and-billing.md)).
- **Microsoft Agent Framework (MAF).** `Microsoft.Agents.AI.GitHub.Copilot` wraps a `CopilotClient` as a MAF `AIAgent`. It was declared stable on 2026-08-04, but defaults to a **60-second** `GITHUB_COPILOT_TIMEOUT` ([MAF devblog](https://devblogs.microsoft.com/agent-framework/build-production-ready-agents-with-the-github-copilot-harness-and-agent-framework/)). MAF 1.0 has been GA since 2026-04-03, with checkpointed workflows and handoff, group chat and Magentic orchestrations ([MAF 1.0](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/)).

### OpenClaw's 2026 incidents define the security baseline

**Exposure.** Default `0.0.0.0` binding left **21,639 to 42,665 gateways** reachable from the internet in scans between Jan 31 and Feb 9. JFrog found **93.4%** of reachable instances bypassable, because behind a reverse proxy every request looks like localhost ([clawdocs known vulnerabilities](https://clawdocs.org/security/known-vulnerabilities)).

**RCE.** CVE-2026-25253 (CVSS 8.8) was a one-click RCE through an unvalidated WebSocket origin, and it worked even against localhost-only instances.

**Skill supply chain.** ClawHavoc found **341 of 2,857 skills** malicious. Snyk found **36%** of 3,984 skills flawed ([clawdocs](https://clawdocs.org/security/known-vulnerabilities)).

**Persistence without a bug.** Zenity's "OpenDoor" attack needed no software bug. A Google Doc carried an indirect injection, and the agent then:

1. added a Telegram integration that used the attacker's bot
2. rewrote SOUL.md, which is injected into every turn
3. created a scheduled task that re-fetched instructions every 2 minutes
4. deployed a C2 implant

([Zenity Labs](https://labs.zenity.io/p/openclaw-or-opendoor-indirect-prompt-injection-makes-openclaw-vulnerable-to-backdoors-and-much-more)). The incident figures come from a community site and vendors; they are indicative, not official post-mortems.

**Detection doesn't work.** Prompt-injection detection is not a reliable defense. In "The Attacker Moves Second", adaptive attacks bypassed all **12** published defenses, most at **>90%** success ([Willison summary](https://simonwillison.net/2025/Nov/2/new-prompt-injection-papers/)).

**The structural answer** is Meta's **Agents Rule of Two**, which restates Willison's **lethal trifecta**. A session should hold at most two of these three:

- untrusted input
- private data or sensitive systems
- the ability to change state or communicate externally

If it needs all three, a human must approve ([Willison: lethal trifecta](https://simonwillison.net/2025/Jun/16/the-lethal-trifecta/); [Willison: Rule of Two](https://simonwillison.net/2025/Nov/2/new-prompt-injection-papers/)). CaMeL-style provenance tracking and plan-then-execute patterns turn this into policies a system can enforce ([Willison: CaMeL](https://simonwillison.net/2025/Apr/11/camel/); [design patterns](https://simonwillison.net/2025/Jun/13/prompt-injection-design-patterns/)).

## Part II — Functional specification

Requirement IDs use the form `FR-<area>-NN`. **Priority** is Must, Should or Could. **Basis** is either **R**, a research-driven requirement with a cited source, or **D**, a design recommendation with no direct source. A citation backs the fact that motivates a requirement; the requirement itself is always a design choice.

### 1. Goals and non-goals

**Goals:**

- **G1.** Run a small fleet (3 agents at MVP, about 8 eventually) of persistent, always-on personal agents. Each has a persona, its own memory and its own schedules.
- **G2.** Let agents consult and delegate to each other with bounded cost and depth, while the human stays the only source of consent.
- **G3.** Hold all credentials in one broker and grant each agent only what it needs.
- **G4.** Gate every consequential action with deterministic rules, taint awareness, a reviewer agent and phone approval.
- **G5.** Be usable from phone and desktop with nothing exposed to the public internet.
- **G6.** Make cost visible and capped for each agent, routine and model.
- **G7.** Host on a Linux mini-PC first, then move to Azure through a host abstraction rather than a rewrite.

**Non-goals:**

- multi-user or family serving, which would conflict with GitHub's one-person-per-login term ([GitHub ToS](https://docs.github.com/en/site-policy/github-terms/github-terms-of-service))
- a skill or plugin marketplace
- a general computer-use desktop per agent at MVP
- public endpoints or third-party inbound webhooks without a relay
- Windows hosting for agents, because Windows sandbox primitives are experimental or packaging-heavy ([Learn: CreateProcessInSandbox](https://learn.microsoft.com/windows/win32/secauthz/createprocessinsandbox))
- commercialization

### 2. Personas and core scenarios

There is one human principal, **Brendan**. He uses the system from a phone (approvals, quick asks, push notifications) and from a desktop (deep work, memory editing, routine authoring, terminal UI).

The MVP agents and their **trifecta profiles** are the central safety design decision:

| Agent (id) | Job | Untrusted input | Private data | External action | How the Rule of Two holds |
|---|---|---|---|---|---|
| Executive assistant (`ea`) — default front door | Inbox/calendar triage, briefs, follow-ups, routing to other agents | Yes (email bodies, invites) | Yes (mail, calendar, `/user` memory) | Draft only. Send, accept, RSVP and forward require approval. **No `web_fetch`.** | The third leg is always human-gated. |
| Coding agent (`code`) | Repo work, PRs, CI triage | Yes (issues, repo content, docs) | Private repos | Push only to `agent/*` branches on allowlisted repos. PR, comment, merge and release require approval. | Sandboxed shell with egress allowlist. Pushes are scoped to allowlisted repos. |
| Research agent (`research`) | Web research, comparisons, reading lists | Yes (open web) | **None.** No connections, no `/user` memory beyond non-sensitive preferences | Unrestricted GET and fetch | Has no private-data leg, so it can fetch freely. Everything it returns is tainted. |

Removing `web_fetch` from the EA is not a stylistic choice: a GET with query parameters is an exfiltration channel. For the same reason, an EA→research consult must carry only a minimal, de-identified question. Otherwise the research agent gains a private-data leg (inference from the [lethal trifecta](https://simonwillison.net/2025/Jun/16/the-lethal-trifecta/)).

**Core scenarios:**

- **S1. Morning brief.** A cron routine on `ea` summarizes the calendar and flagged mail, consults `research` for a de-identified question, and pushes a digest to the phone.
- **S2. Email triage.** A new-mail event triggers `ea`. It drafts replies, and any send shows up as a phone approval card.
- **S3. Coding delegation.** Brendan tells `ea` to "fix the flaky test in repo X". `ea` delegates a task to `code`, which works on an `agent/*` branch in a sandbox and asks for approval to open a PR.
- **S4. Clarification.** A consulted agent needs input. The question bubbles up to `ea`, which answers from memory or escalates `ask_user` to the phone.
- **S5. Runaway control.** A routine loops. The budget breaker trips, a push alert fires, and the kill switch is one tap away.
- **S6. Memory hygiene.** A nightly consolidation proposes merges and archives, and Brendan reviews the diff in the PWA.

### 3. Concepts and glossary

| Term | Definition |
|---|---|
| **Agent** | A persistent logical identity with a stable id (`ea`), a persona (name, job, description/standing rules, avatar), model tier, tool allowlist, connection grants, memory scopes, budget and trifecta profile. It is backed by **one `CopilotClient` with its own `BaseDirectory`**. |
| **Persona files** | `SOUL.md` (identity and tone) and `AGENTS.md` (operating rules), owned and versioned by the user. **Agents cannot write them.** |
| **Main session** | The agent's long-lived Copilot session (`agent_{id}_main`), resumed across restarts, with infinite-session compaction. Heartbeats and user DMs run here. |
| **Consult session** | An **ephemeral** session on the *target* agent's client (`agent_{id}_consult_{uuid}`), created for one `ask_agent` call. It has read-only access to the target's memory and returns at most about 2k tokens plus artifact references. |
| **Task session** | An isolated session for a delegated task or cron run (`agent_{id}_task_{taskId}`). It is retained for audit and then deleted. |
| **Inbox** | A per-agent durable FIFO of user messages, agent messages, task results and events. It is the single serialization point for each agent. |
| **Task** | A durable unit of delegated work using A2A-shaped states: `SUBMITTED`, `WORKING`, `INPUT_REQUIRED`, `COMPLETED`, `FAILED`, `CANCELED`, `REJECTED` ([A2A spec](https://a2a-protocol.org/latest/specification/)). |
| **Envelope** | Metadata on every agent-to-agent call: trace, depth, hop path, budget, deadline, origin principal, taint. |
| **Connection** | An account-level authorized link to an external service (Gmail, Graph, GitHub). It is held only by the broker. |
| **Grant** | A per-agent capability on a connection: connection × operations × constraints, e.g. `gmail: read(label:triage), draft; no send`. |
| **Skill** | A SKILL.md folder of reusable procedure. Skills grant **no permissions** by themselves ([skills.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/skills.md)). |
| **Routine** | A trigger bound to one owner agent plus a prompt or skill, input source, output and delivery target, approval boundary and budget. Kinds are **heartbeat**, **cron** and **event**. |
| **Approval** | A single-use, expiring human decision bound by a nonce to a canonical hash of one tool call's arguments. |
| **Rule** | A deterministic policy entry (deny / ask-first / allow) matched on agent, tool, args, path, domain or recipient. |
| **Reviewer** | A separate low-privilege agent that scores the calls rules leave unresolved: allow, escalate or block. It is never the sole gate. |
| **Taint** | A provenance label on content and sessions that ingested untrusted input. |
| **Fleet thread** | A user-visible log of all agent-to-agent traffic. |
| **Agent host** | The `IAgentHost` implementation that runs an agent's runtime and sandbox: systemd, Podman or ACA Sandbox. |

### 4. Functional requirements

#### 4.1 Agent lifecycle and personas (FR-AGT)

| ID | Requirement | Pri | Basis |
|---|---|---|---|
| FR-AGT-01 | Agents are declared in user-owned, git-versioned config (`agents/{id}/agent.yaml`, `SOUL.md`, `AGENTS.md`) with a **stable logical id** that never contains PII or a conversation id. | Must | R — Grok persona fields ([docs](https://docs.x.ai/grok-bot/bots)); MAF requires stable ids for checkpoint rehydration ([Learn](https://learn.microsoft.com/agent-framework/workflows/checkpoints)) |
| FR-AGT-02 | Each agent runs on its **own `CopilotClient` in `CopilotClientMode.Empty`**, with its own `BaseDirectory` and `WorkingDirectory`, explicit `AvailableTools` on every session, and source-qualified allowlists (`custom:*`, `mcp:<server>-<tool>`). | Must | R — [Types.cs](https://github.com/github/copilot-sdk/blob/main/dotnet/src/Types.cs); [multi-tenancy.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/setup/multi-tenancy.md) |
| FR-AGT-03 | Each agent has one main session with a deterministic id. On every create or resume, the host re-registers **all** hooks, permission, user-input and elicitation handlers, tool sets, MCP config and `ManagedSettings`. | Must | R — non-persisted config silently drops ([dotnet README](https://raw.githubusercontent.com/github/copilot-sdk/main/dotnet/README.md)) |
| FR-AGT-04 | A supervisor health-checks each runtime (`PingAsync`) and restarts it with exponential backoff. After a crash it reconciles in-flight turns from `GetEventsAsync()`, because `session.idle` is ephemeral. A runtime that keeps crashing is quarantined and the user is alerted. | Must | R — no auto-restart ([CHANGELOG](https://raw.githubusercontent.com/github/copilot-sdk/main/CHANGELOG.md)); [agent-loop.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/agent-loop.md) |
| FR-AGT-05 | Each agent has a durable, serialized inbox, and only one turn is in flight per session. A user DM pre-empts background work using `immediate` steering mode. Everything else is enqueued. | Must | R — SDK has no session lock ([scaling.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/setup/scaling.md)); Max queue ([Max](https://mintlify.wiki/burkeholland/max/concepts/architecture)); Grok "DM takes priority" ([docs](https://docs.x.ai/grok-bot/chat-and-collaboration)) |
| FR-AGT-06 | **Agents cannot modify any agent's persona files, config, grants, routines, channels or skill list.** An agent may emit a `propose_change` diff, which goes to the approval inbox for out-of-band human review. | Must | R — Zenity SOUL.md and scheduled-task persistence ([Zenity](https://labs.zenity.io/p/openclaw-or-opendoor-indirect-prompt-injection-makes-openclaw-vulnerable-to-backdoors-and-much-more)) |
| FR-AGT-07 | Lifecycle operations: create (from template or approved proposal), pause, hide, duplicate (profile, skills and routines, **but not memory or history**), archive, and delete. Delete also revokes grants, removes the workspace and `BaseDirectory`, and unschedules routines. Grok's delete leaves shared files and logins behind. | Should | R — [Grok docs: Bots](https://docs.x.ai/grok-bot/bots) |
| FR-AGT-08 | Model selection per agent and per routine. Routine and background agents default to `auto`, which carries a 10% discount. Premium models are pinned explicitly, for example for `code`. Model changes go through `SetModelAsync`. | Should | R — [billing docs](https://docs.github.com/en/enterprise-cloud@latest/copilot/concepts/billing/usage-based-billing-for-individuals); [dotnet README](https://raw.githubusercontent.com/github/copilot-sdk/main/dotnet/README.md) |
| FR-AGT-09 | A startup self-test asserts that each agent's custom tools and agents are visible after both create and resume. Startup fails closed if they are not. | Must | R — issue [#251](https://github.com/github/copilot-sdk/issues/251) |
| FR-AGT-10 | Optional BYOK provider per agent (Azure OpenAI/Foundry, Ollama) for high-volume background agents. Credentials are re-supplied on every resume. | Could | R — [byok.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/auth/byok.md) |
| FR-AGT-11 | System prompt uses `SystemMessageMode.Customize`. It replaces the `Identity`/`Tone` sections with persona content and preserves `Safety`. `Replace` mode is prohibited because it "removes all guardrails". | Must | R — [dotnet README](https://raw.githubusercontent.com/github/copilot-sdk/main/dotnet/README.md) |

#### 4.2 Agent-to-agent communication (FR-A2A)

The design is hub-and-spoke "agent-as-tools", with an inbox for asynchronous work. It is not free-form group chat. Microsoft's guidance is to "use the lowest level of complexity that reliably meets your requirements", and it warns that handoff loops are "challenging" to prevent ([Azure Architecture Center](https://learn.microsoft.com/en-us/azure/architecture/ai-ml/guide/ai-agent-design-patterns)).

| ID | Requirement | Pri | Basis |
|---|---|---|---|
| FR-A2A-01 | **`list_agents`** returns a directory of A2A-AgentCard-shaped records: id, name, job, description, skills with examples, whether the agent accepts consults, messages or tasks, a cost/latency hint, and its trifecta profile. It is filtered by the caller's peer allowlist. | Must | R — Agent Card model ([A2A spec](https://a2a-protocol.org/latest/specification/)); description routing is unofficial ([MindStudio](https://www.mindstudio.ai/blog/grok-bot-ai-agent-fleet/)) |
| FR-A2A-02 | **`ask_agent(target, question, outputSchema?)`** is a synchronous consult. It creates a fresh consult session on the target's client with the target's persona and read-only access to its memory. It returns an answer of at most about 2k tokens plus artifact references, then deletes the session after the audit retention period. The consulted agent never writes the caller's memory. | Must | R — MAF `AsAIFunction` creates a new session per call ([Learn](https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.aiagentextensions.asaifunction?view=agent-framework-dotnet-latest)); condensed 1–2k-token returns ([Anthropic](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents)) |
| FR-A2A-03 | **`send_message(target, body, attachments?)`** is asynchronous. It enqueues into the target's inbox and wakes the target. Replies return to the sender's inbox. Everything appears in the fleet thread. | Must | R — Grok async handoff ([docs](https://docs.x.ai/grok-bot/chat-and-collaboration)) |
| FR-A2A-04 | **`delegate_task(target, objective, outputSchema, boundaries, budget, deadline)`** creates a durable task owned by the target and run in a task session. The result is an **artifact by reference**, a file in the project workspace, not a long chat blob. | Must | R — delegation brief and pass-by-reference ([Anthropic](https://www.anthropic.com/engineering/multi-agent-research-system)); handoff by file ([AV](https://www.analyticsvidhya.com/?p=257374)) |
| FR-A2A-05 | Every A2A call carries an **envelope**: `{traceId, rootTaskId, parentTaskId, depth, hopPath[], budget{credits, toolCalls, wallclock}, deadline, originPrincipal, taint}`. The broker **rejects** a call when `depth > 2`, when the target is already in `hopPath`, or when budget is exhausted. A child's budget is carved out of the parent's remainder. | Must | D — derived from MAST step repetition and termination failures ([arXiv](https://arxiv.org/html/2503.13657)) and Claude Code's ban on nested teams ([docs](https://code.claude.com/docs/en/agent-teams)) |
| FR-A2A-06 | Sessions at maximum depth get **no** `ask_agent`, `send_message` or `delegate_task` tools. The tools are removed, not merely denied by policy. | Must | R — OpenClaw children get no session tools by default ([docs](https://docs.openclaw.ai/tools/subagents)) |
| FR-A2A-07 | **Only the root, user-facing agent of a task talks to the human.** A sub-agent that needs input sets `INPUT_REQUIRED` with a question. The question bubbles up the hop path. The root answers from context or memory, or escalates through `ask_user` to the phone channel. | Must | R — A2A `INPUT_REQUIRED` continuation ([spec](https://a2a-protocol.org/latest/specification/)); "fail to ask for clarification" is 6.8% of MAST failures ([arXiv](https://arxiv.org/html/2503.13657)) |
| FR-A2A-08 | Agent-originated content is **data, not instructions or consent**. It is labelled with `MessageSource.Agent(id)` and provenance. It can never satisfy an approval. Its taint propagates to the receiving session. | Must | R — Claude Code teams rule ([docs](https://code.claude.com/docs/en/agent-teams)); OWASP ASI07 ([OWASP](https://genai.owasp.org/resource/owasp-top-10-for-agentic-applications-for-2026)); `MessageSource` ([dotnet README](https://raw.githubusercontent.com/github/copilot-sdk/main/dotnet/README.md)) |
| FR-A2A-09 | **Single termination owner.** Only the task owner can mark a task `COMPLETED`, and only after a verification step against the original objective and output schema. Unverifiable items are listed explicitly. | Should | R — MAST interventions (+9.4%, +15.6%) ([arXiv](https://arxiv.org/html/2503.13657)); evidence guidance ([Grok docs](https://docs.x.ai/grok-bot/files-and-results)) |
| FR-A2A-10 | Peer allowlist per agent pair, separately for consult, message and task. Default deny. | Should | R — OpenClaw `sessions_send` policy filtering ([docs](https://docs.openclaw.ai/concepts/session-tool)) |
| FR-A2A-11 | Concurrency and ping-pong limits: at most 5 concurrent non-main sessions across the fleet, at most 2 concurrent consults per agent, and at most 4 agent-to-agent exchanges per pair per task before the user must be involved. | Should | R/D — Max caps workers at 5 ([Max](https://mintlify.wiki/burkeholland/max/concepts/architecture)); MAF handoff defaults to 50 turns, far too high for personal use ([Learn](https://learn.microsoft.com/agent-framework/workflows/orchestrations/handoff)) |
| FR-A2A-12 | Group threads of 2–6 agents with @-mention routing and an arbiter that picks who answers. | Could | R — [Grok docs](https://docs.x.ai/grok-bot/chat-and-collaboration); defer because of the loop risk |

#### 4.3 Memory (FR-MEM)

The host owns memory. Copilot runtime Memory and the session store stay **off**, which is the Empty-mode default. The reasons: the location and scoping of runtime memory are unverified, and our memory must be inspectable ([dotnet README](https://raw.githubusercontent.com/github/copilot-sdk/main/dotnet/README.md)).

| ID | Requirement | Pri | Basis |
|---|---|---|---|
| FR-MEM-01 | One git-versioned markdown wiki with three scopes: `/user/` (profile, people, preferences; PARA folders), `/agents/{id}/` (learned procedures, domain notes) and `/projects/{id}/` (decisions and conventions shared by participating agents). Pages carry YAML frontmatter and `[[links]]`, and are compatible with Obsidian. | Must | R — Max wiki ([GitHub](https://github.com/burkeholland/max)); Octopal PARA vault ([GitHub](https://github.com/RyanHecht/octopal)); project-isolated memory ([claude.ai](https://support.claude.com/en/articles/11817273)); Copilot user vs repo scopes ([GitHub Docs](https://docs.github.com/en/copilot/concepts/agents/copilot-memory)) |
| FR-MEM-02 | **Index-first context.** Every turn gets a ranked table of contents (path, one-line description, updated date) within a token budget, plus a capped profile of about 500 tokens. Page bodies load only through `memory_read`. | Must | R — Max ranked TOC; OpenClaw loads MEMORY.md and searches daily notes rather than injecting them ([docs](https://docs.openclaw.ai/concepts/memory)); Hermes caps ([Atlas](https://hermesatlas.com/guide/memory/)) |
| FR-MEM-03 | Tools: `memory_list`, `memory_read`, `memory_search`, `remember` and `forget`. `remember` merges into existing pages using an ADD/UPDATE/DELETE/NOOP decision rather than appending duplicates. | Must | R — Max tools; Mem0 update operation ([arXiv](https://arxiv.org/abs/2504.19413)) |
| FR-MEM-04 | Provenance frontmatter on every page and section: `source` (agent, session, connection or URL), `created`, `lastValidated`, `lastAccessed` and `taint`. | Must | R — Copilot Memory stores citations and validates them before use ([GitHub Docs](https://docs.github.com/en/copilot/concepts/agents/copilot-memory)) |
| FR-MEM-05 | Scope access rules: an agent writes only `/agents/{self}/`. `/user/` writes go through a steward path that produces a user-visible diff, and profile edits require approval. `/projects/{p}/` is readable and writable only in that project's context. Consult sessions are read-only. | Must | R — Letta read-only and shared blocks ([Letta](https://docs.letta.com/guides/agents/memory-blocks)) |
| FR-MEM-06 | Persona files, config, routines, channels and skill lists sit **outside** the memory root, so memory tools cannot address them. The handler rejects path traversal, symlinks, files over a size cap, and categories on a sensitive-data denylist, enforced in code. | Must | R — Claude memory-tool security guidance ([docs](https://platform.claude.com/docs/en/agents-and-tools/tool-use/memory-tool)); Zenity |
| FR-MEM-07 | Memory is **not authoritative**. Before a consequential action, agents must re-verify facts against the source system. | Must | R — Grok "not an authoritative source" ([docs](https://docs.x.ai/grok-bot/bots)) |
| FR-MEM-08 | A nightly **consolidation** job runs as a dedicated low-privilege steward session. It promotes daily logs into durable pages, dedupes, and archives pages unused for N days (default 28). It writes a review digest for the user. | Should | R — OpenClaw dreaming; Letta sleep-time ([docs](https://docs.letta.com/guides/agents/architectures/sleeptime)); 24-hour synthesis ([claude.ai](https://support.claude.com/en/articles/11817273)); 28-day expiry ([Copilot Memory](https://docs.github.com/en/copilot/concepts/agents/copilot-memory)) |
| FR-MEM-09 | Facts that change over time are superseded (`validFrom`/`validTo`) rather than overwritten. | Should | R — Graphiti bi-temporal invalidation ([arXiv](https://arxiv.org/html/2501.13956)) |
| FR-MEM-10 | Memory content is injected as delimited data and scanned for injection patterns on load. Flagged pages are quarantined. | Should | R — Hermes context-file scanning ([docs](https://hermes-agent.nousresearch.com/docs/user-guide/security)) |
| FR-MEM-11 | SQLite FTS5 search once the index exceeds its budget. A temporal graph (Graphiti) is optional for health and finance data, where facts change quickly. | Could | R — Hermes FTS5; Zep at 1.6k vs 115k context tokens ([arXiv](https://arxiv.org/html/2501.13956)) |
| FR-MEM-12 | Incognito sessions read no memory and write none. The PWA provides view, edit, delete and export over the wiki. | Should | R — ChatGPT temporary chats ([OpenAI](https://help.openai.com/en/articles/8590148-memory-faq)); Grok memory opacity ([Vellum](https://www.vellum.ai/md/blog/official-grok-bot-breakdown)) |

#### 4.4 Connections broker (FR-CON)

| ID | Requirement | Pri | Basis |
|---|---|---|---|
| FR-CON-01 | A single **ConnectionBroker** process, running under its own UID, is the only holder of refresh tokens and API keys. Agents receive tool results, or at most short-lived scoped tokens. They never see raw secrets. | Must | R — NanoClaw vault ([nanoclaw.dev](https://nanoclaw.dev/)); OpenClaw plaintext credentials ([clawdocs](https://clawdocs.org/security/known-vulnerabilities)) |
| FR-CON-02 | Connections are account-level, with one OAuth consent per provider. **Use is per agent, through grants**: connection × operations × constraints (labels, recipients, repos, calendars). | Must | R — this inverts Grok's account-wide sharing ([docs](https://docs.x.ai/grok-bot/computer-and-apps)) |
| FR-CON-03 | Connections are exposed as loopback MCP servers or typed `AIFunction`s, with tools enabled or disabled individually per agent. Pin the MCP C# SDK 2.x, which targets the stateless 2026-07-28 spec. | Must | R — Grok per-tool toggles ([docs](https://docs.x.ai/grok-bot/settings-and-notifications)); [MCP changelog](https://modelcontextprotocol.io/specification/2026-07-28/changelog); [C# SDK 2.0](https://www.i-programmer.info/news/89-net/19065-c-sdk-for-model-context-protocol-version-20-announced.html) |
| FR-CON-04 | Secrets are encrypted at rest: TPM-bound `systemd-creds` locally, Key Vault with a user-assigned managed identity on Azure. | Must | R — [systemd-creds](https://man7.org/linux/man-pages/man1/systemd-creds.1.html) |
| FR-CON-05 | **Token health monitor.** The Google OAuth app must be in "In production" status, because Testing-status refresh tokens expire after 7 days. Track Microsoft's 90-day inactivity window, fine-grained PAT expiry for the Copilot token, and 1-hour GitHub App tokens. Alert before expiry and never fail silently. | Must | R — [Google OAuth](https://developers.google.com/identity/protocols/oauth2); [Entra refresh tokens](https://learn.microsoft.com/entra/identity-platform/refresh-tokens); [GitHub credential types](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/github-credential-types) |
| FR-CON-06 | Request least scopes, e.g. `gmail.readonly` + `gmail.compose` rather than full mail. | Should | D |
| FR-CON-07 | **Adding or removing connections, grants or channels is UI-only and human-only.** No agent tool can create a channel or a connection. | Must | R — the Zenity attack added a Telegram C2 integration ([Zenity](https://labs.zenity.io/p/openclaw-or-opendoor-indirect-prompt-injection-makes-openclaw-vulnerable-to-backdoors-and-much-more)) |
| FR-CON-08 | Interactive authentication steps (OAuth consent, 2FA, CAPTCHA) pause the agent and are routed to the user's device. Passwords are entered in secure forms the model never sees. | Should | R — Grok take-over and secret forms ([docs](https://docs.x.ai/grok-bot/approvals-security-and-privacy)) |
| FR-CON-09 | Browser connection for sites without an API: a persistent Playwright profile per agent (a profile cannot be shared across instances), headed under Xvfb, with noVNC take-over over Tailscale. The profile is treated as a secret. | Could | R — [Playwright BrowserType](https://playwright.dev/dotnet/docs/api/class-browsertype); [Playwright Docker](https://playwright.dev/dotnet/docs/docker) |
| FR-CON-10 | Every broker call is audited: agent, operation, args hash, decision, result size and taint. | Must | D |

#### 4.5 Skills (FR-SKL)

| ID | Requirement | Pri | Basis |
|---|---|---|---|
| FR-SKL-01 | Skills are SKILL.md folders loaded through `SkillDirectories` (`EnableSkills=true` in Empty mode) and resolved in tiers: built-in, then shared library, then per-agent. | Must | R — [skills.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/skills.md); Octopal tiers |
| FR-SKL-02 | Progressive disclosure: names and descriptions first, the body on demand. Deferred tools sit behind `tool_search_tool`. | Must | R — Hermes ([docs](https://hermes-agent.nousresearch.com/docs/user-guide/features/skills)); tool search ([CHANGELOG](https://raw.githubusercontent.com/github/copilot-sdk/main/CHANGELOG.md)) |
| FR-SKL-03 | A skill an agent creates or edits is **staged** in `pending/` and activated only after the user approves the diff. Skill `allowed-tools` grants nothing; permissions come only from the agent's grants. | Must | R — Hermes `write_approval`; SDK note on `allowed-tools` ([skills.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/skills.md)) |
| FR-SKL-04 | No marketplace installs. Third-party skills and MCP servers are vendored, pinned by hash, statically scanned, and run without install hooks. | Must | R — ClawHavoc 341/2,857; Snyk 36% ([clawdocs](https://clawdocs.org/security/known-vulnerabilities)) |
| FR-SKL-05 | The skill template must state when to use it, inputs and access, steps, validation, outputs, and the approval boundary. | Should | R — [Grok docs](https://docs.x.ai/grok-bot/skills-routines-and-automations) |
| FR-SKL-06 | "Teach a task": a recorded browser session (DOM actions plus screenshots) becomes a draft skill. | Could | R — Grok's equivalent is capped at 10 minutes ([docs](https://docs.x.ai/grok-bot/skills-routines-and-automations)) |

#### 4.6 Routines and triggers (FR-RTN)

| ID | Requirement | Pri | Basis |
|---|---|---|---|
| FR-RTN-01 | Three trigger kinds. A **heartbeat** is a periodic turn in the main session, limited to active hours and silent unless there is something to report. A **cron** routine runs in a fresh isolated task session. An **event** trigger fires on a webhook, a mail or calendar push, a GitHub event or a file change. | Must | R — OpenClaw heartbeat vs cron ([docs](https://docs.openclaw.ai/gateway/heartbeat)); Grok routines ([docs](https://docs.x.ai/grok-bot/skills-routines-and-automations)) |
| FR-RTN-02 | A routine record holds: owner agent, trigger, prompt or skill, input source, expected output, delivery target, approval boundary, behaviour when an input source is missing, budget, and model tier. | Must | R — [Grok docs](https://docs.x.ai/grok-bot/skills-routines-and-automations) |
| FR-RTN-03 | Limits: interval of at least 5 minutes, ±10% jitter, at most 50 routines per agent, the last 20 runs kept with evidence, and a fleet-wide concurrency cap. | Must | R — Grok limits; jitter and caps reduce the AUP "bulk activity" risk ([AUP](https://docs.github.com/en/site-policy/acceptable-use-policies/github-acceptable-use-policies)) |
| FR-RTN-04 | Only the user creates, edits or enables routines. Agents can only propose them through FR-AGT-06. | Must | R — Zenity's every-2-minute scheduled task |
| FR-RTN-05 | **Nothing fails silently.** Skipped, refused, over-budget or missed runs raise visible alerts. The scheduler is durable (Quartz.NET on SQLite) with an explicit catch-up policy after downtime. | Must | R — Grok routines were silently refused ([forum](https://forum.cursor.com/t/bot-routines-refuse-to-fire/171173)) |
| FR-RTN-06 | Unattended routines auto-pause after N days without user interaction, with a confirmation prompt to resume. | Should | R — [Grok docs](https://docs.x.ai/grok-bot/skills-routines-and-automations) |
| FR-RTN-07 | Event triggers require narrow filters. Event payloads are always tainted. | Should | R — Grok advises narrow rules; trifecta |
| FR-RTN-08 | Dry-run mode for testing a routine, in which approval-required tools are auto-denied. Grok's "test" does real work. | Should | D |
| FR-RTN-09 | Each heartbeat run is a billed model loop. Show the projected monthly cost before a routine is enabled. | Must | R — token billing per loop iteration ([agent-loop.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/agent-loop.md)) |

#### 4.7 Safety and approvals (FR-SAF)

| ID | Requirement | Pri | Basis |
|---|---|---|---|
| FR-SAF-01 | Every tool call passes a **layered gate** in `OnPreToolUse` / `OnPermissionRequest`, and an explicit handler is always registered. The order: (1) static deny via `ManagedSettings` and rules, (2) the deterministic rules engine (ask-first beats allow), (3) the taint and provenance check, (4) the reviewer agent for whatever is unresolved, (5) human approval. | Must | R — Grok Auto Review ordering ([docs](https://docs.x.ai/grok-bot/approvals-security-and-privacy)); SDK hooks and the conflicting default ([compatibility.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/troubleshooting/compatibility.md)) |
| FR-SAF-02 | Each agent declares a **trifecta profile**. The config validator rejects any agent that holds all three legs unless every tool in the external-action leg is approval-required. | Must | R — Rule of Two ([Willison](https://simonwillison.net/2025/Nov/2/new-prompt-injection-papers/)) |
| FR-SAF-03 | **Taint tracking.** A session that ingested untrusted content (mail bodies, web, documents, or output from a tainted agent) is tainted. In a tainted session, consequential actions, and any call whose arguments derive from tainted data, require human approval unless the destination is on an allowlist (own addresses, known contacts). | Must | R — CaMeL data-flow policies ([Willison](https://simonwillison.net/2025/Apr/11/camel/)) |
| FR-SAF-04 | An **approval** object holds: id, a 128-bit nonce, agent, tool, a **canonical args hash**, a rendered preview, risk level, the reviewer's verdict and rationale, origin, and expiry (about 10 minutes for unattended origins). It is single-use and fails closed on expiry. Options are Allow once, Always (creates a rule, and is not offered for high-risk tools) and Deny. | Must | R — Grok approval cards and expiry ([docs](https://docs.x.ai/grok-bot/approvals-security-and-privacy)); Hermes fail-closed ([docs](https://hermes-agent.nousresearch.com/docs/user-guide/security)) |
| FR-SAF-05 | The **reviewer agent** runs in a separate session with no tools, a cheap model and a fixed rubric, and outputs allow, escalate or block. It can block or escalate but **can never override an ask-first or deny rule**. It is defense in depth only. | Must | R — "The Attacker Moves Second" (>90% bypass) ([Willison](https://simonwillison.net/2025/Nov/2/new-prompt-injection-papers/)) |
| FR-SAF-06 | **Kill switch**, reachable from the phone and the TUI. One action cancels all turns, stops runtimes, freezes the broker (all grants revoked) and pauses every routine. A per-agent pause is also available. Resuming requires the desktop. | Must | D |
| FR-SAF-07 | Egress allowlist per agent: an `srt`/proxy domain allowlist locally, Sandbox egress policy on Azure. Output channels do not render remote images or auto-unfurl links, which are a common exfiltration path. | Must | R — [sandbox-runtime](https://github.com/anthropic-experimental/sandbox-runtime); [ACA egress](https://learn.microsoft.com/azure/container-apps/sandboxes-egress-policies) |
| FR-SAF-08 | Secrets never enter model context. Tool outputs are redacted. | Must | R — Grok's secret forms are "not shown to the model" |
| FR-SAF-09 | An append-only, hash-chained audit log records every tool call, approval, config change and kill-switch event. It is kept separate from telemetry. | Must | D — follows ZeroClaw's "tool receipts" ([clawdocs](https://clawdocs.org/guides/lightweight-variants)) |
| FR-SAF-10 | Approval-fatigue controls: low-risk approvals are batched, recipient and domain allowlists are respected, and phone prompts are reserved for consequential actions. | Should | R — CaMeL notes approval fatigue as a limitation |
| FR-SAF-11 | Any change to persona, grants, routines, channels, connections or skills is an out-of-band human action with a diff. Overlaps FR-AGT-06 and FR-CON-07, restated here as policy. | Must | R — Zenity |

#### 4.8 Clients and channels (FR-CLI)

| ID | Requirement | Pri | Basis |
|---|---|---|---|
| FR-CLI-01 | The gateway (ASP.NET Core minimal API plus SignalR) **binds only to 127.0.0.1**. It is published to the tailnet through **Tailscale Serve**. It authenticates using the `Tailscale-User-Login` identity header plus a per-device token. Proxy-forwarded requests are never trusted as "localhost". It validates WebSocket `Origin`. Funnel and public ports are never used. | Must | R — [Tailscale Serve](https://tailscale.com/kb/1312/serve); CVE-2026-25253 and JFrog's 93.4% finding ([clawdocs](https://clawdocs.org/security/known-vulnerabilities)) |
| FR-CLI-02 | A **PWA** (Blazor or React) for desktop and phone includes: agent list with Working / Needs attention / Unread states; per-agent chat; fleet thread; approval inbox with rich previews (email render, diff, args); memory browser and editor; routines; budgets; audit; kill switch. | Must | R — Grok sidebar states ([docs](https://docs.x.ai/grok-bot/settings-and-notifications)) |
| FR-CLI-03 | **Push channel:** self-hosted ntfy (preferred) or Telegram, firing on Finished, Needs input and Approval. Payloads are minimal ("approval needed: ea → send email"). Actions deep-link to the PWA or hit a nonce-bound endpoint over the tailnet. No sensitive content goes over Telegram. | Must | R — ntfy action buttons ([docs](https://docs.ntfy.sh/publish/)); Telegram bot chats are not end-to-end encrypted ([FAQ](https://telegram.org/faq)) |
| FR-CLI-04 | Any conversational channel accepts **one allowlisted user id** and silently drops everyone else. | Must | R — Max Telegram ([Max](https://mintlify.wiki/burkeholland/max/concepts/architecture)) |
| FR-CLI-05 | A **TUI** (Spectre.Console) over the same API, with token authentication. Max's TUI has none. | Should | R — [Max](https://mintlify.wiki/burkeholland/max/concepts/architecture) |
| FR-CLI-06 | Web push to an iOS PWA requires iOS 16.4+ and a Home Screen install. ntfy is the fallback. | Should | R — [OneSignal](https://documentation.onesignal.com/docs/web-push-for-ios) |
| FR-CLI-07 | Notification toggle per agent, plus quiet hours. Notifications are suppressed while the PWA has focus. | Should | R — [Grok docs](https://docs.x.ai/grok-bot/settings-and-notifications) |
| FR-CLI-08 | The coding agent can use `EnableRemoteSessions` (GitHub Mission Control on web and mobile) when its working directory is a GitHub repo. | Could | R — [remote-sessions.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/remote-sessions.md) (untested) |
| FR-CLI-09 | Browser take-over view (noVNC over the tailnet) for logins, CAPTCHAs and 2FA. | Could | R — Grok take-over ([docs](https://docs.x.ai/grok-bot/computer-and-apps)) |

#### 4.9 Observability and cost (FR-OBS)

| ID | Requirement | Pri | Basis |
|---|---|---|---|
| FR-OBS-01 | OpenTelemetry through the SDK's `TelemetryConfig`. One trace per run, rooted at its trigger, with spans for LLM calls, tools, gate decisions, approvals and A2A hops. W3C context is carried in the envelope so a task spanning several agents is one trace. | Must | R — SDK OTel and traceparent propagation ([dotnet README](https://raw.githubusercontent.com/github/copilot-sdk/main/dotnet/README.md)) |
| FR-OBS-02 | A **cost ledger** built from `assistant.usage`, attributed per agent, routine, task and model, with daily and monthly budgets. Each short-lived session (task, consult, incognito, reviewer) gets `SessionLimits.MaxAiCredits`; the long-lived main session gets an explicitly empty limit (which also clears one stored at its creation by an earlier build), because the SDK's window is cumulative over a live session and would eventually disable it. **Host-side hard stops** (a per-turn credit limit that aborts the run, and the ledger) are added because the SDK cap is soft. The ledger polls `account.getQuota` and alerts at 50%, 80% and 100%. | Must | R — [session-limits.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/session-limits.md); [usage-and-billing.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/usage-and-billing.md); Grok credit drain ([forum](https://forum.cursor.com/t/grok-bot-draining-cursor-credit-pool/169982)) |
| FR-OBS-03 | Model attribution is visible for every run and every credit spent. | Must | R — Grok's opaque routing |
| FR-OBS-04 | Locally: the standalone Aspire dashboard for live traces, which is not durable, and Grafana LGTM for retention. Prompt and response content capture is **off by default**. No third-party telemetry SaaS. | Should | R — [Aspire standalone](https://aspire.dev/dashboard/standalone/); [Learn](https://learn.microsoft.com/dotnet/core/diagnostics/observability-otlp-example) |
| FR-OBS-05 | Alerts for: budget thresholds, spikes in egress denials, approval expiries, routine misfires, token expiry, runtime crash loops, and compaction storms. | Should | R — egress-denial spikes signal exfiltration ([ACA egress](https://learn.microsoft.com/azure/container-apps/sandboxes-egress-policies)) |
| FR-OBS-06 | Each run keeps evidence: sources, screenshots, an action log and the items that could not be verified. | Should | R — [Grok docs](https://docs.x.ai/grok-bot/files-and-results) |

#### 4.10 Hosting (FR-HST)

| ID | Requirement | Pri | Basis |
|---|---|---|---|
| FR-HST-01 | An **`IAgentHost`** abstraction (`Provision`, `Start`, `Stop`, `Health`, `Exec`, `Snapshot`) covers an agent runtime plus its execution sandbox. Implementations: `SystemdHost`, `PodmanHost`, and later `AcaSandboxHost`. | Must | R/D — Hermes's seven terminal backends ([GitHub](https://github.com/NousResearch/hermes-agent)) |
| FR-HST-02 | **Local:** the services are Generic Host with `AddSystemd()` (`Type=notify`). Units use a dedicated user or `DynamicUser`, `ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`, `NoNewPrivileges`, `StateDirectory`, `MemoryMax`/`CPUQuota`, `IPAddressDeny/Allow`, and `LoadCredentialEncrypted`. **Do not set `MemoryDenyWriteExecute`**, because it is incompatible with JIT. | Must | R — [AddSystemd](https://learn.microsoft.com/dotnet/api/microsoft.extensions.hosting.systemdhostbuilderextensions.addsystemd?view=net-11.0-pp); [systemd.exec](https://man7.org/linux/man-pages/man5/systemd.exec.5.html); [resource-control](https://man7.org/linux/man-pages/man5/systemd.resource-control.5.html) |
| FR-HST-03 | Agents that run code or browse run inside rootless Podman (Quadlet) or `srt`/bubblewrap with a domain allowlist. Playwright runs as non-root with its seccomp profile. | Must | R — [Quadlet](https://docs.podman.io/en/latest/markdown/podman-systemd.unit.5.html); [srt](https://github.com/anthropic-experimental/sandbox-runtime); [Playwright Docker](https://playwright.dev/dotnet/docs/docker) |
| FR-HST-04 | Pin the exact SDK version, which pins the CLI runtime. Publish for the target RID (linux-x64 or linux-arm64). Upgrade deliberately, behind the FR-AGT-09 self-test. | Must | R — [dotnet README](https://raw.githubusercontent.com/github/copilot-sdk/main/dotnet/README.md); [usage-and-billing.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/features/usage-and-billing.md) |
| FR-HST-05 | Nightly backup, with a tested restore, of each agent's `COPILOT_HOME`, the memory repo, the SQLite stores and the encrypted credential blobs. | Must | R — file-based session state ([scaling.md](https://raw.githubusercontent.com/github/copilot-sdk/main/docs/setup/scaling.md)) |
| FR-HST-06 | State sits behind interfaces (`IStateStore`: SQLite now, Azure SQL or Postgres later; `IBlobStore`: filesystem now, Azure Files or Blob later; `ISecretStore`: systemd-creds now, Key Vault later). | Must | D |
| FR-HST-07 | **Azure phase:** the gateway, orchestrator and broker run as an ACA app (consumption plan, minimum 1 replica, user-assigned managed identity, Key Vault references). Agent workspaces run as **ACA Sandboxes**: microVM, suspend/resume, default-deny egress with **Transform** rules that inject credentials so the sandbox never holds them. Dynamic sessions are used only for ephemeral code execution. Custom-container session pools (dedicated E16 billing) and **ACA Express** (no Key Vault refs, OTel or system-assigned MI) are avoided. | Should | R — [Sandboxes](https://learn.microsoft.com/azure/container-apps/sandboxes-overview); [egress](https://learn.microsoft.com/azure/container-apps/sandboxes-egress-policies); [billing](https://learn.microsoft.com/azure/container-apps/billing#dedicated-plan); [InfoQ](https://infoq.com/news/2026/10/container-apps-express-sandboxes) |
| FR-HST-08 | **Why not App Service:** it can host the gateway, but it has no per-session microVM isolation and no egress policy engine, so it does not solve agent sandboxing. This is an inference; App Service was not researched in depth. | — | D |
| FR-HST-09 | A hybrid mode is supported: agents that must stay on-premises (for example health or finance) remain on the home box and are reached over an outbound-only relay or the tailnet. | Could | R — Azure Relay Hybrid Connections ([Learn](https://learn.microsoft.com/azure/azure-relay/relay-what-is-it)) |

### 5. Non-functional requirements

| ID | Category | Requirement |
|---|---|---|
| NFR-01 | Security | Default deny everywhere: gateway auth, peer allowlists, egress, grants. No listener on a non-loopback interface except Tailscale Serve. Secrets never in plaintext on disk. |
| NFR-02 | Reliability | A crashed runtime restarts in ≤30 s. No inbox message is lost: the inbox is a durable outbox persisted before acknowledgment. Resume after reboot needs no manual steps. |
| NFR-03 | Cost | The fleet runs within the Copilot plan's monthly credits (target: Pro+ at 7,000 credits). Background agents can fall back to BYOK. Hard stop at 100% of budget. |
| NFR-04 | Latency | P95 for a consult under 90 s. The MAF Copilot wrapper's 60 s default timeout must be raised if MAF is used ([MAF devblog](https://devblogs.microsoft.com/agent-framework/build-production-ready-agents-with-the-github-copilot-harness-and-agent-framework/)). Push for an approval arrives within 10 s of the gate decision. |
| NFR-05 | Resource | Each agent has `MemoryMax`/`CPUQuota`. Mini-PC sizing has to be measured, because no per-runtime footprint is published. |
| NFR-06 | Privacy | Telemetry content capture is off. Sensitive payloads never go to Telegram or third-party SaaS. Memory can be exported and deleted. |
| NFR-07 | Compliance | Single human per Copilot login. Use a **personal** Copilot plan, not an employer-provided seat. Jittered schedules and concurrency caps keep volume well below "bulk activity". |
| NFR-08 | Maintainability | Pinned SDK. Integration tests for create/resume/hook/custom-agent visibility. Adapters isolate `GHCP001` experimental APIs. |
| NFR-09 | Portability | Moving from local to Azure is a configuration and `IAgentHost` swap, not a code fork. |
| NFR-10 | Auditability | Every consequential action can be traced from trigger to approval to outcome in one trace plus the audit log. |

### 6. Recommended .NET architecture

**Process topology (MVP).** Four systemd units, plus one sandbox per agent:

- **`capybarad`** contains the gateway, orchestrator, scheduler, policy engine, memory service and channel adapters. It hosts one `CopilotClient` per agent, each with its own `BaseDirectory` under `/var/lib/capybara/agents/{id}/copilot` and its own stdio CLI child process.
- **`capybara-broker`** has its own UID. It is the only reader of the encrypted credentials and serves loopback MCP and gRPC.
- **`aspire-dashboard`** and **`ntfy`** run as Quadlet containers bound to localhost.
- **Per-agent sandboxes**: Podman or `srt` wrappers for shell and browser execution.

Phase 2 splits each agent into its own unit (`capybara-agent@{id}.service`) with its own UID. The orchestrator talks to it over gRPC on a Unix domain socket using the same contracts.

**Sandboxing the built-in shell.** Two seams are available, and the second is an untested inference. Option (a): replace the built-in shell and edit tools with custom tools (`OverridesBuiltInTool`) that execute through `srt` or `podman exec`. Option (b): launch the CLI runtime itself under a wrapper via `RuntimeConnection.ForStdio(path, args)` or `CopilotCliBinaryPath`, so every native tool inherits the sandbox. Option (b) is stronger and cheaper to maintain, and the first spike should validate it ([dotnet README](https://raw.githubusercontent.com/github/copilot-sdk/main/dotnet/README.md)).

**Projects:**

| Project | Responsibility | Key dependencies |
|---|---|---|
| `Capybara.Contracts` | A2A-shaped records: `AgentCard`, `FleetTask` (A2A state enum), `FleetMessage` / `Part` / `Artifact`, `Envelope`, `ApprovalRequest`, `Grant`, `RoutineSpec` | none |
| `Capybara.Runtime.Copilot` | `AgentRuntime` wraps `CopilotClient`: Empty mode, session id scheme, resume with full re-registration, supervisor, inbox pump, usage capture | `GitHub.Copilot.SDK` (pinned) |
| `Capybara.Orchestration` | Agent registry, `list_agents`, `ask_agent`, `send_message`, `delegate_task`, envelope enforcement, task table, fleet thread | `System.Threading.Channels`, optionally `Microsoft.Agents.AI` |
| `Capybara.Policy` | Rules engine, taint tracker, approval service (nonce + args hash + TTL), reviewer agent, kill switch, `ManagedSettings` generation | — |
| `Capybara.Memory` | Wiki store (git via LibGit2Sharp), index ranker, tools, steward and consolidation job, FTS5 | `Microsoft.Data.Sqlite` |
| `Capybara.Connections` | Broker, OAuth flows, token vault, MCP servers per connection, grant enforcement, token health | `ModelContextProtocol` 2.x, MSAL, Google APIs |
| `Capybara.Scheduling` | Heartbeat, cron and event triggers, jitter, catch-up, cost projection | Quartz.NET |
| `Capybara.Gateway` | Minimal API, SignalR hub, Tailscale identity middleware, PWA host, approval endpoints | ASP.NET Core |
| `Capybara.Channels.*` | ntfy and Telegram adapters (and Slack Socket Mode if wanted) | — |
| `Capybara.Hosting` | `IAgentHost`: `SystemdHost`, `PodmanHost`, later `AcaSandboxHost` | — |
| `Capybara.Tui` | Spectre.Console client | — |
| `Capybara.AppHost` | Aspire AppHost for **dev only** | Aspire |

**Where Microsoft Agent Framework helps, and where it doesn't:**

- **Use the Copilot SDK directly for the agent runtime.** The spec depends on hooks, `OnPermissionRequest`, `ManagedSettings`, elicitation, `SessionLimits` and custom-agent config. It is unverified which of these the MAF abstraction exposes.
- **Use MAF selectively:**
  - **checkpointed workflows** for defined multi-step jobs such as a weekly finance review or trip booking, with stable agent ids and a file- or SQLite-backed checkpoint store treated as a trust boundary ([Learn: checkpoints](https://learn.microsoft.com/agent-framework/workflows/checkpoints))
  - `AsAIAgent` to put BYOK-backed agents behind the same `AIAgent` abstraction
  - `ApprovalRequiredAIFunction` where a MAF-hosted tool needs human-in-the-loop ([Learn: tool approval](https://learn.microsoft.com/agent-framework/agents/tools/tool-approval))
- **Avoid Magentic and autonomous handoff** at this scale. They are "slow to converge", and handoff bounces are hard to prevent ([Azure Architecture Center](https://learn.microsoft.com/en-us/azure/architecture/ai-ml/guide/ai-agent-design-patterns)).

**Protocols:**

- **In process:** A2A-shaped contracts dispatched over `Channel<T>`.
- **MCP:** for connectors only. Under the 2026-07-28 spec MCP is stateless and has no `contextId`, which makes it a poor internal bus ([MCP changelog](https://modelcontextprotocol.io/specification/2026-07-28/changelog)).
- **Real A2A:** expose `MapA2A()` only when an agent leaves the box. The .NET `A2A` package is still 1.0.0-preview2, and MAF's `A2AAgent` "supports only messages", not tasks ([NuGet A2A](https://www.nuget.org/packages/A2A); [A2AAgent](https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.a2a.a2aagent?view=agent-framework-dotnet-latest)).
- **Copilot auth:** a fine-grained PAT stored via `systemd-creds` and passed as `GitHubToken`, with `UseLoggedInUser=false`.

### 7. Phased roadmap

| Phase | Scope | Exit criteria |
|---|---|---|
| **0 — Spike (1–2 wks)** | Pinned SDK on the mini-PC. One agent in Empty mode with its own `BaseDirectory`. Deterministic session resume with full handler re-registration. Explicit permission handler. Usage-to-ledger capture. Self-test for #251. Validate the wrapper that runs the CLI runtime under `srt`. Measure runtime RAM and CPU per agent. | Resume survives `kill -9`. Ledger matches `getMetrics`. Footprint known. |
| **1 — MVP (3 agents)** | `ea`, `code` and `research` with the trifecta profiles above. Memory wiki (MEM-01 to MEM-07). `list_agents`, `ask_agent` and `send_message` with envelope, depth ≤ 2 and cycle checks. Broker with Gmail/Graph and GitHub grants. Rules engine and approvals (nonce, hash, TTL). Kill switch. PWA over Tailscale Serve plus ntfy. Heartbeat and cron routines. Budgets with hard stops. OTel to Aspire. systemd hardening. Nightly backup. | Scenarios S1, S3, S5 and S6 pass. One month runs within budget with no silent failures. |
| **2 — Hardening and autonomy** | One unit per agent with its own UID. Podman sandbox for `code`. Taint tracking and the reviewer agent. `delegate_task` with durable tasks and `INPUT_REQUIRED` bubbling. Event triggers (mail push, GitHub). Consolidation job. TUI. Grafana LGTM. | S2 and S4 pass. A red-team injection set (email, document, web) cannot trigger an unapproved external action. |
| **3 — Breadth** | More agents (finance, health, home). Browser connection with noVNC take-over. Staged skill authoring. Optional group threads. BYOK for high-volume background agents. MAF workflows for multi-step jobs. | Adding an agent is config only. No agent holds all three trifecta legs without approval gating. |
| **4 — Azure** | `AcaSandboxHost`. Gateway, orchestrator and broker on ACA with managed identity and Key Vault. Sandboxes with egress Transform rules. Hybrid on-premises agents. | Moving an agent between hosts is a config change, and its memory, sessions and grants are preserved. |

### 8. Open questions and risks

| # | Item | Type | Notes / mitigation |
|---|---|---|---|
| 1 | Credit burn from heartbeats and tool loops | Risk | Three agents with 30-minute heartbeats make 144 billed loops a day before any real work. Start with 2-hour heartbeats and active hours, use the `auto` tier, and project cost (FR-RTN-09). Usage spills over at zero credits only if an extra-usage budget is set ([billing docs](https://docs.github.com/en/enterprise-cloud@latest/copilot/concepts/billing/usage-based-billing-for-individuals)). |
| 2 | Unpublished rate-limit and abuse thresholds | Risk | Rate limits are "temporary" and unspecified ([rate limits](https://docs.github.com/en/copilot/concepts/rate-limits)). Mitigate with concurrency caps, jitter and BYOK for bulk work. |
| 3 | Which Copilot seat to use | Open | Using an employer-provided Copilot seat for a personal daemon is a question for employer policy, governed by the Microsoft Product Terms, not just GitHub's ([GitHub additional terms](https://docs.github.com/en/site-policy/github-terms/github-terms-for-additional-products-and-features)). Recommendation: a personal Pro+ plan. |
| 4 | SDK churn and experimental APIs | Risk | 134 NuGet versions, with 1–2 releases a week ([NuGet](https://api.nuget.org/v3-flatcontainer/github.copilot.sdk/index.json)). Mitigate with pinning, adapters and self-tests. |
| 5 | Custom agents broken with hooks (#251) | Risk | Closure is unconfirmed. Mitigation: the spec does **not** rely on in-session custom agents. The fleet's own agents are separate clients, and FR-AGT-09 guards the rest. |
| 6 | Default when no permission handler is set | Open | The docs conflict. Always register an explicit handler that fails closed. |
| 7 | Per-runtime footprint on the mini-PC | Open | Unpublished. Measure in Phase 0, since each agent runs its own CLI process. |
| 8 | Wrapping the CLI runtime in `srt` | Open | Untested. Fallback is overriding the built-in shell and edit tools. |
| 9 | Reviewer bypass and approval fatigue | Risk | The reviewer is never the sole gate. Rely on allowlists, batching and the trifecta split. |
| 10 | Memory poisoning via `/user` writes | Risk | Steward path, diffs, provenance, taint and injection scanning on load (FR-MEM-05, FR-MEM-10). |
| 11 | ACA Sandboxes need Entra ID; personal Microsoft accounts are unsupported | Open | Confirm the Azure subscription's tenant can be used ([Sandboxes](https://learn.microsoft.com/azure/container-apps/sandboxes-overview)). GA limits and pricing were not confirmed. |
| 12 | Telegram privacy | Risk | Prefer ntfy. Telegram carries only content-free notifications. |
| 13 | Grok Bot source conflicts | Note | Per-user vs per-agent computer, description routing, three memory layers and the Projects feature are all unverified or contradicted. Nothing in this spec depends on them. |
| 14 | Unmeasured multi-agent value | Risk | MAST gains are from research frameworks. Cognition argues for single-threaded agents ([Cognition](https://cognition.com/blog/dont-build-multi-agents)). Keep consults cheap and optional, and measure whether they add value. |
| 15 | MCP C# SDK 2.x dates and package names | Note | Unverified. Pin a version after checking NuGet. |

## Conclusion

The research leads to one choice: **put the security boundary in the infrastructure, and use the model only for usefulness.** Grok Bot, OpenClaw and Max show what people want from an agent fleet: named teammates, handoffs, routines, and approvals from the phone. They also show the cost of putting isolation at the persona level. Grok shares one computer across Bots, OpenClaw runs a single Node process with plaintext credentials, and Max's TUI has no authentication.

The Copilot SDK makes the stronger design cheap. Separate clients and `BaseDirectory`s, Empty-mode allowlists, and pre-tool hooks are first-class features. That leaves the real engineering in the host: the supervisor, the inbox, the envelope, the broker, the policy gate and the cost ledger. Those are exactly the pieces no existing Copilot-SDK project has built.

Two consequences are easy to underestimate. First, token billing turns "always-on" from a hosting problem into a budgeting problem: heartbeat frequency is now a line item. Second, the trifecta split across the three MVP agents means the research agent never holds private data and the EA never fetches the web. That split does more for safety than any reviewer model can. Build Phase 0 to answer the three open empirical questions first: runtime footprint, whether the CLI can run under a sandbox wrapper, and real credit burn. Every later sizing and hosting decision depends on those numbers.
