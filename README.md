# Capybara

A small, hardened fleet of personal agents (`ea`, `code`, `research`, …) built on the
[GitHub Copilot SDK for .NET](https://www.nuget.org/packages/GitHub.Copilot.SDK). It runs as a single
daemon, `capybarad`, on a home server and can only be reached over your tailnet. See [`spec.md`](spec.md)
for the full specification. Requirement IDs such as `FR-SAF-03` appear in XML docs throughout the code.

The model only ever *proposes* actions. Policy, budgets, taint and approvals are enforced in code
that the model cannot change.

## Layout

```
Capybara.slnx            Solution (XML .slnx format)
global.json                    .NET 10 SDK pin + Microsoft.Testing.Platform runner
Directory.Build.props          Shared build settings: nullable, analyzers, warnings-as-errors
Directory.Packages.props       Central package management (every version lives here)
.editorconfig                  Code style enforced at build time

src/
  Capybara.Contracts/             Pure domain types: AgentId, AgentDefinition, Envelope, Budget, Taint, tasks, routines, proposals
  Capybara.Policy/                Rules engine, PolicyGate, reviewer rubric, taint tracker, approvals, kill switch, hash-chained audit log
  Capybara.Observability/         Cost ledger, quota monitor, alert sinks and detectors (egress spikes, compaction storms)
  Capybara.Scheduling/            Durable Quartz scheduler, routine registry, dispatcher, auto-pause, event triggers
  Capybara.Memory/                Scoped Markdown wiki with provenance, taint, FTS index, consolidation and git versioning
  Capybara.Orchestration/         Config host, proposals, agent lifecycle, registry, A2A coordinator, envelopes, inbox, tasks
  Capybara.Connections/           Broker client (capybarad side) and the broker itself: grants, vault, OAuth, connectors
  Capybara.Channels*/             Notification router, Web Push, ntfy and Telegram adapters, phone escalation
  Capybara.Hosting/               Backups, srt sandbox planning, systemd/Podman/ACA agent hosts, state and secret stores
  Capybara.Runtime.Copilot/       Copilot SDK adapter: one CLI per agent, session hooks, fleet tools, inbox pumps, supervisor
  Capybara.Gateway/               capybarad: composition root, minimal API, SignalR, event wiring, loopback-only, tailnet auth
  Capybara.Broker/                capybara-broker: the credential broker process (holds every connection credential)
  Capybara.Pwa/ Capybara.Client/ Capybara.Tui/   Clients
  Capybara.AppHost/               Aspire dev host

tests/
  Directory.Build.props        xUnit v3 on Microsoft.Testing.Platform for every test project
  Shared/                      Test helpers compiled into each test project (no extra assembly)
  Capybara.*.Tests/               One test project per library

config/                        Sample fleet: agents/<id>/{agent.yaml,SOUL.md,AGENTS.md}, rules.yaml, routines.yaml
deploy/                        Hardened systemd units, quadlets, srt sample, Azure Bicep (see deploy/README.md)
```

Dependencies point inward only. Contracts ← Policy / Observability / Scheduling / Memory ←
Orchestration ← Runtime.Copilot ← Gateway. Only `Capybara.Runtime.Copilot` references the Copilot SDK.

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build Capybara.slnx
dotnet test --solution Capybara.slnx                # all tests
dotnet test --project tests/Capybara.Policy.Tests             # a single project
```

The tests need neither the Copilot CLI nor network access. Gateway tests host `capybarad` in memory
against a copy of `config/` with fakes for the broker, SignalR and push channels, so they also validate the sample configuration.

## Running capybarad

```bash
dotnet run --project src/Capybara.Gateway
```

Configuration comes from the `Capybara` section of `appsettings.json`, which environment variables can
override (for example `Capybara__StateRoot`). Secrets are never configuration values: settings ending in `Credential`
name a systemd credential read from `$CREDENTIALS_DIRECTORY`.

| Setting | Purpose |
| --- | --- |
| `Capybara:ConfigRoot` | Directory containing `agents/`, `rules.yaml` and `routines.yaml`. Invalid config stops startup (fail closed). capybarad validates, writes and git-commits approved proposals and lifecycle changes here. |
| `Capybara:StateRoot` | Per-agent Copilot homes, memory wiki, inbox, tasks, approvals, ledger, alerts, audit log, scheduler database. Every layer must agree on it: `Capybara:Hosting:StateRoot` or `Capybara:Runtime:StateRoot` set to anything else stops startup. |
| `Capybara:GitHubTokenCredential` | Credential holding the Copilot PAT (default `github-token`). |
| `Capybara:Gateway:Port` / `AllowedLogin` / `Devices` / `AllowedOrigins` | Loopback port (Kestrel binds `127.0.0.1` only), the one Tailscale login accepted, per-device token hashes tied to a `Phone`/`Desktop`/`Tui`/`EventRelay` surface, and allowed browser origins. An `EventRelay` token can only post webhook events. |
| `Capybara:Events:Sources:{name}` | Webhook sources accepted at `POST /api/events/{name}/{type}`, each with its HMAC `SecretCredential`. A source without a readable secret is refused (startup warning). |
| `Capybara:Incognito` | `IdleTimeout` (default 30 min) after which an unused incognito conversation's CLI session is deleted, and `SweepInterval` (default 5 min). Sessions from before a restart are deleted at start. |
| `Capybara:Hosting` | `StateDatabasePath` and `BlobRoot` (backup archives; default `{StateRoot}/blobs`). |
| `Capybara:Backup` | `Sources[]`, `RetainCount`, `ScheduleUtc`, shared with `capybarad backup`. `InProcess` (default `true`) runs the nightly job inside capybarad; set `false` when `capybara-backup.timer` runs it. |
| `Capybara:Runtime` | `AutoStart`, `CliPath`/`CliArgs` (unsandboxed only), `TurnTimeout`, `ConsultTimeout` (default 5 min per `ask_agent` answer), `FrontDoorAgent`, `ReviewerModel`, `ReviewerTimeout` (default 45 s; running out escalates the call to you), `DefinitionSwapDrainTimeout` (default 1 min: how long an edit that restarts an agent's CLI waits for its running task, routine and consult turns before stopping them; they are retried or reported), `ToolSearchDeferThreshold`, `BuiltInSkillDirectory`, plus the isolation settings below. |
| `Capybara:Runtime:UntrustedInputTools` | Tools whose results are untrusted, so using one taints the session (FR-SAF-03). Default `web_fetch`, `web_search`, `email_read`, `calendar_read`; add every tool that returns outside content (MCP readers, other connectors), or its output counts as clean. |
| `Capybara:Runtime:CliEnvironmentPassthrough` | Host variables copied into each CLI's otherwise empty environment (proxies, CA bundles). Credential-like names are dropped even if listed. |
| `Capybara:Runtime:SessionScopedGitHubToken` | `true` (default): the PAT goes to each session over RPC, never into the CLI's environment. `false` is a rollback switch only. |
| `Capybara:Runtime:StdioMcpIsolation` | `ClearEnvironment` (Unix default, wraps stdio MCP servers in `env -i`) or `Refuse` (Windows default). |
| `Capybara:Runtime:EnvExecutable` / `EnvExecutableSha256` | The `env` binary used for that isolation, and its SHA-256 pin (set it in deployment). |
| `Capybara:Runtime:Sandbox` | `SrtPath` and `CopilotCliPath` turn on per-agent srt sandboxing (`SettingsDirectory` defaults to `{StateRoot}/srt`). |
| `Capybara:Supervisor` | `CheckInterval`, `MaxCrashes` and back-off for runtime restarts. |
| `Capybara:Broker` | `BaseAddress` or `UnixSocketPath` of capybara-broker, `ApiKeyCredential` (default `broker-api-key`), `TokenExpiryLeadTime`. |
| `Capybara:Channels` | `Ntfy` (`BaseUrl`, `Topic`, `AccessTokenCredential`, `SupportsSensitiveContent` default `true` for a tailnet-only server, `GatewayBaseUrl`: the PWA's tailnet URL, used for the tap target and the "Review" button, which opens the approval in the PWA), `WebPush` (`Subject`, `PublicKey`, `PrivateKeyCredential`), `Telegram` (`BotTokenCredential`, `ChatId`, `AllowedUserId` enables DM polling, `DeepLinkBaseUrl`: the PWA's tailnet URL, so the link in a Telegram message opens the PWA instead of a bare path), `QuietHours`. Channels without settings are off; preference is ntfy, Web Push, Telegram. |
| `Capybara:Scheduling` | `TimeZone`, `ConcurrencyCap`, `AutoPauseDays`, `CatchUpPolicy` (`Skip` or `FireOnceNow`). |
| `Capybara:Approvals` / `Capybara:A2A` / `Capybara:Quota` | Approval TTLs; A2A limits; quota `PollInterval`. |
| `Capybara:Telemetry` | `OtlpEndpoint` (an OTLP/HTTP collector such as the Aspire dashboard or Grafana LGTM; unset = no export) and `OtlpProtocol`. With `http/protobuf` the same endpoint is passed to every agent CLI; the CLI cannot export over gRPC, so with `grpc` set `Capybara:Runtime:OtlpEndpoint` to an OTLP/HTTP endpoint separately (a startup warning says so). Content capture is always off. |

Command-line modes: `capybarad backup`, `capybarad restore <archive> <destination>` (both exit before any host is built) and
`capybarad agent <id>` (Phase 2, see below).

### Deployment

1. Publish both processes for the host's RID (FR-HST-04; use `linux-arm64` on ARM). Each gets its own directory: the content root
   is the working directory, so sharing one would make each process read the other's `appsettings.json`.

   ```bash
   dotnet publish src/Capybara.Gateway/Capybara.Gateway.csproj -c Release -r linux-x64 --self-contained -o /opt/capybara
   dotnet publish src/Capybara.Broker/Capybara.Broker.csproj -c Release -r linux-x64 --self-contained -o /opt/capybara-broker
   ```

   The SDK version is pinned in `global.json`, which pins the Copilot CLI the SDK ships. Upgrade deliberately: the agents' start-up
   tool self-test (FR-AGT-09) is the gate.
2. Kestrel binds to `127.0.0.1` only. Publish it with `tailscale serve --bg 8787`, never with Funnel.
3. Encrypt the secrets with `systemd-creds encrypt` into `/etc/capybara/credentials`: `github-token` and `broker-api-key` for
   capybarad, plus one credential per webhook source, named by `Capybara:Events:Sources:{name}:SecretCredential` (uncomment and
   adapt its `LoadCredentialEncrypted` line in `capybarad.service`); `broker-api-key`, `broker-vault-key`, `connection-*` and
   `oauth-*-client-secret` for capybara-broker.
4. Install `deploy/systemd/capybara-broker.service` and `capybarad.service` (both `Type=notify`; capybarad waits until the broker is
   ready) and, if you want out-of-process backups, the backup timer. `deploy/README.md` has the full steps, the egress drop-in and the
   srt notes.
5. Webhooks from the internet (GitHub, mail push) reach capybarad only through a relay you run on the tailnet. Give that relay its
   own device entry with `Surface: EventRelay` and nothing else: an `EventRelay` token is refused on every route except
   `POST /api/events/**` (including approvals, proposals, memory, the kill switch and the hub), so a compromised relay can post
   events, which are HMAC-checked and always tainted, but cannot act as you. Never give the relay a phone or desktop token.
6. The TUI (`src/Capybara.Tui`) talks to the same API as the PWA, so it must go through Tailscale Serve: set `CAPYBARA_URL` (or
   `--base-url`) to the tailnet Serve URL, e.g. `https://capybara.<tailnet>.ts.net`. The default `http://127.0.0.1:8787` is always
   refused with 401, because a loopback request carries no `Tailscale-User-Login`. Give it its own device entry with `Surface: Tui`
   and pass the token in `CAPYBARA_TOKEN` or a `0600` file at `~/.config/capybara/token`.

## Security model at a glance

- **Empty-mode sessions.** No built-in CLI tools are inherited. Every session lists `AvailableTools`
  explicitly, and a self-test at startup fails if the live tool list differs from the agent's allowlist.
- **One gate for every tool call.** The pre-tool-use hook asks `PolicyGate`. The permission handler
  approves only a call the gate has just allowed, and rejects everything else.
- **Lethal trifecta.** Reading untrusted input (`web_fetch`, email, tainted memory) taints the session.
  `AllowUnlessTainted` rules then block exfiltration paths such as sending messages to non-allowlisted
  destinations. Taint is journaled, so a session resumed after a restart is still tainted.
- **Config edits apply live.** An approved proposal or a hand edit reaches the running agent at its next turn (its main session is
  rebuilt; for egress, MCP or provider changes its CLI is replaced under the runtime's locks, with new turns held back and running ones drained or stopped first) and the tool self-test runs again. Agent-proposed routines land
  disabled until you enable them with their projected cost; a routine's `approvalBoundary` tools need approval in its runs.
- **Human controls stick.** Agent pauses and routine enable/disable decisions survive restarts and are audited.
- **A2A envelopes.** Envelopes carry depth (max 2), hop path (no cycles), deadline and a budget slice.
  Consults are read-only and ephemeral, ping-pong between agents is capped, and taint flows back to the caller.
- **Kill switch.** Any device can engage it. Only a desktop can release it.
- **Audit.** Every decision is appended to a SHA-256 hash-chained JSONL log, which `GET /api/audit/verify` checks.

## Not yet implemented

- **OAuth authorize and callback (FR-CON-08).** The broker can begin and complete a Google or Microsoft authorization
  (`BrokerClient.BeginAuthorizationAsync` / `CompleteAuthorizationAsync`), but no route starts it and nothing handles the provider's
  redirect. Until a desktop-only authorize route and a PWA `/oauth/callback` page exist, OAuth connections cannot be authorized, so the
  sample `ea` agent's email and calendar tools do not work; token-based connections (such as `connection-github`) do.
- **Per-agent processes (Phase 2, FR-HST-01, NFR-09).** The `IAgentHost` implementations are registered but do not run agents.
  `capybarad agent <id>` starts and supervises one agent's CLI, but there is no gateway-to-agent RPC, so it receives no turns; every
  agent still runs inside capybarad.
- **Agents created at runtime** (lifecycle API) get their runtime at the next capybarad restart. Edits to existing agents apply live.
- **File-change triggers (FR-RTN-01).** `FileChangeEventSource` exists but is never registered; heartbeat, cron and webhook triggers work.
- **Routine `input` / `onMissingInput` (FR-RTN-02)** are parsed but not checked: a routine runs whether or not its inputs are available.
- **Tracing (FR-OBS-01, NFR-10).** Spans are exported, and audit records carry the envelope's trace id, but there is no single root span
  per run and no span per A2A hop, so one request is not one trace yet.
- **Consult sessions (FR-A2A-02)** are deleted as soon as the answer is returned, not kept for the audit retention period, and produce
  no artifacts; the fleet thread and audit log keep the record.
- **Memory provenance (FR-MEM-04)** is per page, not per section.
- **Project memory (FR-MEM-05).** The `/projects/` scope exists in the access policy, but no session is ever given a project, so it is
  unreachable.
- **Memory steward model (FR-MEM-08).** Nightly consolidation is deterministic (it stages promotions of daily logs and removals of
  duplicates for review); there is no steward model session to summarize.
- **Phone replies.** A DM that arrived on a phone channel is answered in the PWA; the phone gets a content-free "finished" push
  (Telegram never carries sensitive content).
- **Egress spike alerts** count policy denials of tool calls that carry a URL; network-level denials inside srt are not yet read.
- **Could items:** group threads (FR-A2A-12), teach-a-task (FR-SKL-06), browser take-over (FR-CLI-09, FR-CON-09); live ACA hosting
  (FR-HST-07).

### Checks that need a live Copilot CLI or host

Assumptions marked in code that the tests cannot prove:

- nano-AIU to credit conversion; that an empty `SessionLimits` on resume clears a limit stored with the session (the main session is
  resumed with none; `LiveSessionCreationTests`).
- The SDK's `PreToolUse` hook timeout must be longer than `Capybara:Runtime:ReviewerTimeout` (45 s): if the hook times out first, the
  CLI treats the host tool call as allowed. Built-in and broker tools stay fail-closed either way.
- `OnPreToolUse` firing for SDK custom tools; the steering race (`immediate` mode at turn end); `SetModelAsync` on a resumed session;
  resuming or deleting a missing session; tool metadata being null until tools initialize; the reviewer session with no tools.
- Client-level calls (ping, delete session, tool metadata, `account.getQuota`) with only a session-scoped token; `account.getQuota`
  is an evaluation API in the pinned SDK.
- Whether `McpStdioServerConfig.Env` merges with or replaces the inherited environment (`env -i` isolation assumes merge); a shell
  tool turn under the allowlisted CLI environment.
- Built-in tool names the sample config assumes (`shell`, `read_file`, `edit_file`, `web_fetch`, `web_search`) in Empty mode.
- On the host: srt/bubblewrap under the relaxed `capybarad.service` settings, `Type=notify` readiness, `LoadCredentialEncrypted`,
  and that the broker can read the config tree through the shared group: `sudo -u capybara-broker cat /etc/capybara/agents/*/agent.yaml`
  (also after capybarad has created or duplicated an agent).