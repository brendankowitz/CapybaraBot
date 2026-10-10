# CharcuterieBot

A small, hardened fleet of personal agents (`ea`, `code`, `research`, …) built on the
[GitHub Copilot SDK for .NET](https://www.nuget.org/packages/GitHub.Copilot.SDK). It runs as a single
daemon, `fleetd`, on a home server and can only be reached over your tailnet. See [`spec.md`](spec.md)
for the full specification. Requirement IDs such as `FR-POL-03` appear in XML docs throughout the code.

This branch implements the deterministic core: Phase 0 (foundation) and Phase 1 (policy, A2A, memory).
The model only ever *proposes* actions. Policy, budgets, taint and approvals are enforced in code
that the model cannot change.

## Layout

```
CharcuterieBot.slnx            Solution (XML .slnx format)
global.json                    .NET 10 SDK pin + Microsoft.Testing.Platform runner
Directory.Build.props          Shared build settings: nullable, analyzers, warnings-as-errors
Directory.Packages.props       Central package management (every version lives here)
.editorconfig                  Code style enforced at build time

src/
  Fleet.Contracts/             Pure domain types: AgentId, AgentDefinition, Envelope, Budget, Taint, tasks
  Fleet.Policy/                Rules engine, PolicyGate, taint tracker, approvals, kill switch, hash-chained audit log
  Fleet.Observability/         Cost ledger with budget alerts, alert sinks, fleet thread
  Fleet.Scheduling/            Routine definitions, cron/interval schedules, active hours
  Fleet.Memory/                Scoped Markdown wiki with provenance, taint and git versioning
  Fleet.Orchestration/         YAML config loader, agent registry, A2A coordinator, envelopes, inbox, tasks
  Fleet.Runtime.Copilot/       Copilot SDK adapter: one CLI per agent, session hooks, fleet tools, supervisor
  Fleet.Gateway/               fleetd: ASP.NET Core minimal API, loopback-only, tailnet authentication

tests/
  Directory.Build.props        xUnit v3 on Microsoft.Testing.Platform for every test project
  Shared/                      Test helpers compiled into each test project (no extra assembly)
  Fleet.*.Tests/               One test project per library

config/                        Sample fleet: agents/<id>/{agent.yaml,SOUL.md,AGENTS.md}, rules.yaml, routines.yaml
deploy/systemd/fleetd.service  Hardened systemd unit using encrypted credentials
```

Dependencies point inward only. Contracts ← Policy / Observability / Scheduling / Memory ←
Orchestration ← Runtime.Copilot ← Gateway. Only `Fleet.Runtime.Copilot` references the Copilot SDK.

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build CharcuterieBot.slnx
dotnet test --solution CharcuterieBot.slnx                # all tests
dotnet test --project tests/Fleet.Policy.Tests             # a single project
```

The tests need neither the Copilot CLI nor network access. Gateway tests host `fleetd` in memory
against `config/`, so they also validate the sample configuration.

## Running fleetd

```bash
dotnet run --project src/Fleet.Gateway
```

Configuration comes from the `Fleet` section of `appsettings.json`, which environment variables can
override (for example `Fleet__StateRoot`):

| Setting | Purpose |
| --- | --- |
| `Fleet:ConfigRoot` | Directory containing `agents/`, `rules.yaml` and `routines.yaml`. Invalid config stops startup (fail closed). |
| `Fleet:StateRoot` | Per-agent Copilot homes, memory wiki, inbox, tasks and audit log. |
| `Fleet:Gateway:AllowedLogin` | The only Tailscale login accepted, read from the `Tailscale-User-Login` header. |
| `Fleet:Gateway:Devices` | Per-device bearer tokens, stored as SHA-256 hashes, each tied to a `Phone`/`Desktop`/`Tui` surface. |
| `Fleet:Gateway:AllowedOrigins` | Browser origins. WebSocket upgrades must present one of them. |
| `Fleet:Runtime:CliPath` | Path to the Copilot CLI. Each agent gets its own process and `BaseDirectory`. |

### Deployment

1. Kestrel binds to `127.0.0.1` only. Publish it with `tailscale serve --bg 8787`, never with Funnel.
2. Store the GitHub PAT with `systemd-creds encrypt` as the `github-token` credential. The unit reads it
   through `LoadCredentialEncrypted=`, so it never sits in plain files or environment variables.
3. Install `deploy/systemd/fleetd.service`. It runs `fleetd` as a dedicated user with a read-only
   system, private `/tmp`, no new privileges and a restricted syscall set.

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

These parts of the spec are deferred to later phases: the `propose_change` self-modification flow and
reviewer agent, the scheduler's execution loop, the PWA and TUI clients, ntfy/Telegram alert sinks,
MCP server integration, and OpenTelemetry export.

Some assumptions about the Copilot SDK still need checking against a live CLI. These are the conversion
from nano-AIU to credits, whether custom tools pass through `OnPreToolUse`, and what resuming a missing
session does. Each one is marked in code where it is used.
