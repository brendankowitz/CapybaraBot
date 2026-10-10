# Deployment artifacts

- `systemd/`: hardened local units for `capybarad`, `capybara-broker`, per-agent Phase 2 runtimes, and nightly backups.
- `quadlet/`: loopback-only dev/ops containers for Aspire dashboard, ntfy, Grafana LGTM, and per-agent Podman sandboxes.
- `srt/`: example sandbox-runtime policy. The gateway generates one per agent at launch with `AgentSandboxPlanner`; see
  [Sandbox policy](#sandbox-policy-srt) for exactly what it denies.
- `azure/`: Bicep for ACA consumption hosting, user-assigned identity, Key Vault references, and Log Analytics. Public ingress is disabled by default.
- `capybarad.backup.sample.json`: sample `Capybara:Hosting` and `Capybara:Backup` configuration.

## What the capybarad composition provides

`Capybara.Gateway/Program.cs` wires what the units rely on:

- **`AddSystemd()`**, so `Type=notify` units report readiness once every hosted service has started.
- **`backup` / `restore` dispatch** to `BackupCommand.RunAsync` before any host is built (used by `capybara-backup.service`).
- **`capybarad agent <id>`** (Phase 2): only that agent's runtime and supervisor. Without the gateway<->agent RPC it receives no turns.
- **Per-agent srt launch** via `AgentSandboxPlanner` when `Capybara:Runtime:Sandbox:SrtPath` and `CopilotCliPath` are set; the
  settings files go to `{StateRoot}/srt`. Without them agents run unsandboxed (development only).
- **Kestrel on 127.0.0.1 only** (`Capybara:Gateway:Port`); Tailscale Serve is the only way in.

## Install steps

1. Create the service users (`capybara`, `capybara-broker`) and `install -d -o capybara -g capybara -m 0700 /var/backups/capybara`.
2. Put the config tree in `/etc/capybara` (`agents/`, `rules.yaml`, `routines.yaml`) owned by `capybara`; capybarad commits every
   change to it with git, so `git` must be installed.
3. Encrypt the credentials into `/etc/capybara/credentials` with `systemd-creds encrypt --name=<name> <file> <name>.cred`:
   - capybarad: `github-token` (Copilot PAT), `broker-api-key` (random, shared with the broker), optionally `telegram-bot-token` and
     `webpush-vapid-private-key` (then uncomment their lines in `capybarad.service`);
   - capybara-broker: `broker-api-key` (same value), `broker-vault-key` (`head -c32 /dev/urandom | base64`), `connection-github`,
     `oauth-google-client-secret`, plus one line per further connection.
4. Copy `systemd/capybarad.egress.sample.conf` to `/etc/systemd/system/capybarad.service.d/egress.conf` and list the public
   endpoints capybarad must reach.
5. `systemctl enable --now capybara-broker capybarad` (capybarad orders itself after the broker). Then `tailscale serve` the loopback
   port.
6. Backups: either keep `Capybara:Backup:InProcess=true` (default) with sources in capybarad's config, or enable
   `capybara-backup.timer` and set `Capybara__Backup__InProcess=false` in a capybarad drop-in. Never both.
## systemd units

- `capybarad.service` is `Type=notify`, has `MemoryMax`/`CPUQuota`, and filters egress with `IPAddressDeny=any` + loopback and tailnet
  allowed. systemd filters by IP, not DNS name, so public endpoints capybarad must reach are added in a drop-in: copy
  `systemd/capybarad.egress.sample.conf` to `/etc/systemd/system/capybarad.service.d/egress.conf`. Until you do, capybarad can only reach
  loopback and the tailnet. Per-agent domain egress is enforced by srt, not by the unit.
- `MemoryDenyWriteExecute` is deliberately never set: the .NET JIT and the Copilot CLI's Node runtime need executable pages (FR-HST-02).
- `capybara-agent@.service` is a **Phase 2** template. It runs `capybarad agent <id>` (per-agent mode of the gateway binary). Do not
  enable it yet: without the gateway<->agent RPC, capybarad still runs every agent itself.

### Units that launch srt are relaxed on purpose

srt sandboxes with bubblewrap on Linux, which needs unprivileged user/mount/pid namespaces, `mount`/`pivot_root`/`capset`, and a fresh
`/proc` mount. So `capybarad.service` and `capybara-agent@.service` relax exactly these, and nothing else:

- `RestrictNamespaces=user mnt pid net ipc uts` (instead of `yes`);
- `SystemCallFilter` adds `@mount`, plus `capset` and `pivot_root` re-allowed from `@privileged` (add others only if the host test below fails);
- `RestrictAddressFamilies` adds `AF_NETLINK` (bwrap's `--unshare-net` brings up loopback over a `NETLINK_ROUTE` socket);
- `ProtectProc`, `ProcSubset` and `ProtectKernelTunables` are dropped (they overmount `/proc`, which makes bubblewrap's own `/proc` mount fail).

All other hardening is kept. `capybara-broker.service` and `capybara-backup.service` launch no sandbox and keep the strict settings.

**This has not been verified on a real host.** Before relying on it, on the target machine run as the service user:
`srt --settings <generated file> -- true`, and check `journalctl -u capybarad` for `bwrap` / `seccomp` denials. Also check that
`kernel.unprivileged_userns_clone` (Debian/Ubuntu) / `user.max_user_namespaces` allow user namespaces. If bubblewrap still fails,
the fallback is rootless Podman (`quadlet/capybara-sandbox@.container`).

## Sandbox policy (srt)

`AgentSandboxPlanner` writes `{SettingsDirectory}/{agent}.srt.json` per agent:

- **Network** is allow-only: `network.allowedDomains` = the agent's `egress` domains. None means no network. Bare `*` and
  top-level wildcards such as `*.com` are rejected.
- **Writes** are allow-only: `filesystem.allowWrite` = the agent's workspace and its `COPILOT_HOME` (BaseDirectory) and nothing
  else. The settings file itself is in `denyWrite` (which wins over `allowWrite`), and the planner refuses a `SettingsDirectory` that
  lies inside a path the agent can write.
- **Reads are allowed everywhere unless denied** (the opposite of writes). So `filesystem.denyRead` lists
  `AgentSandboxOptions.DenyRead`, which has no default: build it with `AgentSandboxOptions.FromHosting(hostingOptions, ...)` so it follows
  the configured state root (which holds every agent's `COPILOT_HOME`, the memory repo and the state databases), plus `/etc/capybara`,
  `/run/credentials` and `$CREDENTIALS_DIRECTORY`. `PlanAsync` fails closed: it throws if an agent's workspace or `COPILOT_HOME` is not
  under a denied path, since the agent's neighbours would then be readable. The agent's own workspace and
  `COPILOT_HOME` are re-opened with `filesystem.allowRead`, which srt gives precedence over `denyRead`. Anything outside those paths
  (for example `/usr`, `/opt`, `/home`) stays readable; add it to `DenyRead` if it must not be. Install the Copilot CLI outside the state
  root so it stays readable inside the sandbox.

## Backup (FR-HST-05)

Back up the following, as `Capybara:Backup:Sources` (see `capybarad.backup.sample.json`): every agent's `COPILOT_HOME`
(`/var/lib/capybara/agents/<id>/copilot`, one source per agent), the memory repo, each SQLite store (`Kind: SqliteDatabase`, snapshotted
with the online-backup API so WAL is safe) and the encrypted credential blobs (`/etc/capybara/credentials`, still encrypted
in the archive). Archives are integrity-checked `tar.gz` files in the blob store (`Capybara:Hosting:BlobRoot`; the blob store is never
archived). `RetainCount` newest archives are kept. Archives are verified, not authenticated: protect the blob store, or encrypt it.

**Where archives go.** `BlobRoot` defaults to `{StateRoot}/blobs` (`/var/lib/capybara/blobs`). The sample sets it to
`/var/backups/capybara/blobs` so archives live outside the state they protect; this is the only location besides `/var/lib/capybara` that
`capybara-backup.service` (`ProtectSystem=strict`) may write, and `capybarad.service` allows it too. Create it first:
`install -d -o capybara -g capybara -m 0700 /var/backups/capybara`. `HostingOptions.FromConfiguration` reads `Capybara:Hosting`.

Two triggers exist; use only one:

- In-process (default): when `Capybara:Backup` has sources, `capybarad` runs a nightly job (default 02:00 UTC) with catch-up on start.
- Out-of-process: `capybara-backup.timer` runs `capybara-backup.service`, which executes `/opt/capybara/capybarad backup`. The command reads the
  same `Capybara:Backup` section; set `Capybara:Backup:InProcess=false` for capybarad (`Environment=Capybara__Backup__InProcess=false` in a
  drop-in) so the running gateway does not also run the nightly job.

Command line (exit code 0 ok, 1 failed, 2 usage):

```
capybarad backup
capybarad restore <archive> <destination>
```

`<archive>` is a blob path such as `backups/20260101T020000000Z.tar.gz`; `<destination>` must be empty or new.

## Restore procedure

1. Stop `capybarad`, `capybara-broker`, and any `capybara-agent@*.service` units.
2. Run `capybarad restore backups/<timestamp>.tar.gz /var/tmp/capybara-restore` (use `cp` of the archive into the blob root first if it
   lives elsewhere, e.g. Azure). The manifest SHA-256 hashes are verified before anything is written; a tampered or truncated archive
   exits 1 and writes nothing.
3. Move restored agent homes, memory repository, SQLite stores, and encrypted credential blobs from `<destination>/<source name>/` into place.
4. Start the services and check `capybara-backup.timer` plus health alerts.
