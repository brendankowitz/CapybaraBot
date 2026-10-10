# Deployment artifacts

- `systemd/`: hardened local units for `fleetd`, `fleet-broker`, per-agent Phase 2 runtimes, and nightly backups.
- `quadlet/`: loopback-only dev/ops containers for Aspire dashboard, ntfy, Grafana LGTM, and per-agent Podman sandboxes.
- `srt/`: example sandbox-runtime policy with default-deny egress and write allowlists.
- `azure/`: Bicep for ACA consumption hosting, user-assigned identity, Key Vault references, and Log Analytics. Public ingress is disabled by default.

## Restore procedure

1. Stop `fleetd`, `fleet-broker`, and any `fleet-agent@*.service` units.
2. Copy the selected `backups/*.tar.gz` from the configured `IBlobStore` to the host or Azure restore worker.
3. Run the Fleet.Hosting restore path to verify manifest SHA-256 hashes and extract into a clean target directory.
4. Move restored agent homes, memory repositories, SQLite stores, and encrypted credential blobs into place.
5. Start the services and check `fleet-backup.timer` plus health alerts.
