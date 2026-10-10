# Deployment artifacts

- `systemd/`: hardened local units for `capybarad`, `capybara-broker`, per-agent Phase 2 runtimes, and nightly backups.
- `quadlet/`: loopback-only dev/ops containers for Aspire dashboard, ntfy, Grafana LGTM, and per-agent Podman sandboxes.
- `srt/`: example sandbox-runtime policy (`network.allowedDomains`, `filesystem.allowWrite`; srt denies everything not listed).
  The gateway generates one per agent at launch with `AgentSandboxPlanner` (egress = the agent's `egress` domains, write
  allowlist = its workspace + `COPILOT_HOME`).
- `azure/`: Bicep for ACA consumption hosting, user-assigned identity, Key Vault references, and Log Analytics. Public ingress is disabled by default.
- `capybarad.backup.sample.json`: sample `Capybara:Backup` configuration.

## systemd units

- `capybarad.service` is `Type=notify` (the gateway calls `AddSystemd()`), has `MemoryMax`/`CPUQuota`, and filters egress with
  `IPAddressDeny=any` + loopback and tailnet allowed. systemd filters by IP, not DNS name, so public endpoints capybarad must reach
  are added in a drop-in: copy `systemd/capybarad.egress.sample.conf` to `/etc/systemd/system/capybarad.service.d/egress.conf`.
  Until you do, capybarad can only reach loopback and the tailnet. Per-agent domain egress is enforced by srt, not by the unit.
- `MemoryDenyWriteExecute` is deliberately never set: the .NET JIT and the Copilot CLI's Node runtime need executable pages (FR-HST-02).
- `capybara-agent@.service` is a **Phase 2** template. It runs `capybarad agent <id>` (per-agent mode of the gateway binary). Do not
  enable it until the gateway implements that mode.

## Backup (FR-HST-05)

Back up the following, as `Capybara:Backup:Sources` (see `capybarad.backup.sample.json`): every agent's `COPILOT_HOME`
(`/var/lib/capybara/agents/<id>/copilot`, one source per agent), the memory repo, each SQLite store (`Kind: SqliteDatabase`, snapshotted
with the online-backup API so WAL is safe) and the encrypted credential blobs (`/etc/capybara/credentials`, still encrypted
in the archive). Archives are integrity-checked `tar.gz` files in the blob store (`HostingOptions.BlobRoot`; the blob store is never archived).
`RetainCount` newest archives are kept. Archives are verified, not authenticated: protect the blob store, or encrypt it.

Two triggers exist; use one:

- In-process: when `Capybara:Backup` has sources, `capybarad` runs a nightly job (default 02:00 UTC) with catch-up on start.
- Out-of-process: `capybara-backup.timer` runs `capybara-backup.service`, which executes `/opt/capybara/capybarad backup`.
  Disable the in-process job (remove the `Capybara:Backup` section from `capybarad`'s own config) if you use the timer.

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
