# Capybara

A small, hardened fleet of personal agents (`ea`, `code`, `research`, …) built on the
[GitHub Copilot SDK for .NET](https://www.nuget.org/packages/GitHub.Copilot.SDK). It runs as a single
daemon, `capybarad`, on a home server and can only be reached over your tailnet. See [`spec.md`](spec.md)
for the full specification. Requirement IDs such as `FR-POL-03` appear in XML docs throughout the code.

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
| `Capybara:Gateway:Port` / `AllowedLogin` / `Devices` / `AllowedOrigins` | Loopback port (Kestrel binds `127.0.0.1` only), the one Tailscale login accepted, per-device token hashes tied to a `Phone`/`Desktop`/`Tui` surface, and allowed browser origins. |
| `Capybara:Hosting` | `StateDatabasePath` and `BlobRoot` (backup archives; default `{StateRoot}/blobs`). |
| `Capybara:Backup` | `Sources[]`, `RetainCount`, `ScheduleUtc`, shared with `capybarad backup`. `InProcess` (default `true`) runs the nightly job inside capybarad; set `false` when `capybara-backup.timer` runs it. |
| `Capybara:Runtime` | `AutoStart`, `CliPath`/`CliArgs` (unsandboxed only), `TurnTimeout`, `FrontDoorAgent`, `ReviewerModel`, `ToolSearchDeferThreshold`, `BuiltInSkillDirectory`, plus the isolation settings below. |
| `Capybara:Runtime:CliEnvironmentPassthrough` | Host variables copied into each CLI's otherwise empty environment (proxies, CA bundles). Credential-like names are dropped even if listed. |
| `Capybara:Runtime:SessionScopedGitHubToken` | `true` (default): the PAT goes to each session over RPC, never into the CLI's environment. `false` is a rollback switch only. |
| `Capybara:Runtime:StdioMcpIsolation` | `ClearEnvironment` (Unix default, wraps stdio MCP servers in `env -i`) or `Refuse` (Windows default). |
| `Capybara:Runtime:EnvExecutable` / `EnvExecutableSha256` | The `env` binary used for that isolation, and its SHA-256 pin (set it in deployment). |
| `Capybara:Runtime:Sandbox` | `SrtPath` and `CopilotCliPath` turn on per-agent srt sandboxing (`SettingsDirectory` defaults to `{StateRoot}/srt`). |
| `Capybara:Supervisor` | `CheckInterval`, `MaxCrashes` and back-off for runtime restarts. |
| `Capybara:Broker` | `BaseAddress` or `UnixSocketPath` of capybara-broker, `ApiKeyCredential` (default `broker-api-key`), `TokenExpiryLeadTime`. |
| `Capybara:Channels` | `Ntfy` (`BaseUrl`, `Topic`, `AccessTokenCredential`, `SupportsSensitiveContent` default `true` for a tailnet-only server, `GatewayBaseUrl`), `WebPush` (`Subject`, `PublicKey`, `PrivateKeyCredential`), `Telegram` (`BotTokenCredential`, `ChatId`, `AllowedUserId` enables DM polling), `QuietHours`. Channels without settings are off; preference is ntfy, Web Push, Telegram. |
| `Capybara:Scheduling` | `TimeZone`, `ConcurrencyCap`, `AutoPauseDays`, `CatchUpPolicy` (`Skip` or `FireOnceNow`). |
| `Capybara:Approvals` / `Capybara:A2A` / `Capybara:Quota` | Approval TTLs; A2A limits; quota `PollInterval`. |
| `Capybara:Telemetry` | `OtlpEndpoint` (an OTLP/HTTP collector such as the Aspire dashboard or Grafana LGTM; unset = no export) and `OtlpProtocol`. The same endpoint is passed to every agent CLI. Content capture is always off. |

Command-line modes: `capybarad backup`, `capybarad restore <archive> <destination>` (both exit before any host is built) and
`capybarad agent <id>` (Phase 2, see below).

### Deployment

1. Kestrel binds to `127.0.0.1` only. Publish it with `tailscale serve --bg 8787`, never with Funnel.
2. Encrypt the secrets with `systemd-creds encrypt` into `/etc/capybara/credentials`: `github-token` and `broker-api-key` for
   capybarad; `broker-api-key`, `broker-vault-key`, `connection-*` and `oauth-*-client-secret` for capybara-broker.
3. Install `deploy/systemd/capybara-broker.service` and `capybarad.service` (`Type=notify`, ordered after the broker) and, if you
   want out-of-process backups, the backup timer. `deploy/README.md` has the full steps, the egress drop-in and the srt notes.
## Security model at a glance

- **Empty-mode sessions.** No built-in CLI tools are inherited. Every session lists `AvailableTools`
  explicitly, and a self-test at startup fails if the live tool list differs from the agent's allowlist.
- **One gate for every tool call.** The pre-tool-use hook asks `PolicyGate`. The permission handler
  approves only a call the gate has just allowed, and rejects everything else.
- **Lethal trifecta.** Reading untrusted input (`web_fetch`, email, tainted memory) taints the session.
  `AllowUnlessTainted` rules then block exfiltration paths such as sending messages to non-allowlisted
  destinations.
- **A2A envelopes.** Envelopes carry depth (max 2), hop path (no cycles), deadline and a budget slice.
  Consults are read-only and ephemeral, ping-pong between agents is capped, and taint flows back to the caller.
- **Kill switch.** Any device can engage it. Only a desktop can release it.
- **Audit.** Every decision is appended to a SHA-256 hash-chained JSONL log, which `GET /api/audit/verify` checks.

## Not yet implemented

- **REST surface for the PWA/TUI (WP11).** capybarad exposes agents, approvals, kill switch, audit verify, ledger, alerts, thread,
  memory pending changes and runtimes. Still missing: memory pages/export, routines (enable with cost acknowledgement, dry run),
  budgets, proposal decisions, lifecycle operations, `/questions`, push subscription, connections (consent requests), tasks, the
  HMAC-verified event ingress, and `POST /api/approvals/{id}/act` for push action buttons. `POST /api/agents/{id}/turn` still calls the
  runtime directly instead of going through the inbox.
- **PWA and TUI completion (WP12):** rich approval previews, proposal diffs, routines and budget pages, Web Push subscription.
- **Per-agent processes (Phase 2).** `capybarad agent <id>` starts and supervises one agent's CLI, but there is no gateway-to-agent RPC,
  so it receives no turns; every agent still runs inside capybarad. An agent created at runtime gets its runtime at the next restart.
- **Phone replies.** A DM that arrived on a phone channel is answered in the PWA; the phone gets a content-free "finished" push
  (Telegram never carries sensitive content).
- **Egress spike alerts** count policy denials of tool calls that carry a URL; network-level denials inside srt are not yet read.
- **Could items:** group threads (FR-A2A-12), teach-a-task (FR-SKL-06), browser take-over (FR-CLI-09, FR-CON-09); live ACA hosting
  (FR-HST-07).

### Checks that need a live Copilot CLI or host

Assumptions marked in code that the tests cannot prove:

- nano-AIU to credit conversion; whether `SessionLimits.MaxAiCredits` is cumulative over a long-lived main session.
- `OnPreToolUse` firing for SDK custom tools; the steering race (`immediate` mode at turn end); `SetModelAsync` on a resumed session;
  resuming or deleting a missing session; tool metadata being null until tools initialize; the reviewer session with no tools.
- Client-level calls (ping, delete session, tool metadata, `account.getQuota`) with only a session-scoped token; `account.getQuota`
  is an evaluation API in the pinned SDK.
- Whether `McpStdioServerConfig.Env` merges with or replaces the inherited environment (`env -i` isolation assumes merge); a shell
  tool turn under the allowlisted CLI environment.
- Built-in tool names the sample config assumes (`shell`, `read_file`, `edit_file`, `web_fetch`, `web_search`) in Empty mode.
- On the host: srt/bubblewrap under the relaxed `capybarad.service` settings, `Type=notify` readiness, `LoadCredentialEncrypted`.