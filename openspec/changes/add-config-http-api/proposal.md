## Why

All config mutation today is local: the TUI, the CLI, or a file edit. The daemon
HTTP surface cannot read or change config. Remote operator clients — the future
ops console (PRD-003 UX-006), paired devices, and scripts — need an authorized
way to view and change every config section without shell access to the host.

## What Changes

- Add `GET /api/config`. It returns the full `netclaw.json` document merged with
  redacted placeholders for every secret stored in `secrets.json`.
- Add `PATCH /api/config`. It applies an RFC 7386 JSON Merge Patch over the
  config document. A `null` member deletes the key.
- Route secret values into `secrets.json` through `ISecretsProtector`. Secret
  values never land in `netclaw.json` and never appear in any response.
- Validate the merged document before persistence: v1 schema, `IConfiguration`
  binding, and provider type checks. An invalid patch returns `400` with
  field-level errors and writes nothing.
- Reject patches that touch `Daemon.Host`, `Daemon.ExposureMode`, or
  `Daemon.TrustedProxies`. These three paths stay local-only.
- Apply accepted patches to disk atomically. The existing `ConfigWatcherService`
  stays the single reload trigger and performs the coordinated restart.
- Require operator authority for both endpoints and rate-limit writes.
- Audit every write: actor identity and changed top-level paths. `Security`
  changes log at warning level with old and new values.
- Move the netclaw.json/secrets.json write engine from `Netclaw.Cli` to
  `Netclaw.Configuration` so the daemon and the CLI share one writer.

## Capabilities

### New Capabilities

- `config-http-api`: HTTP read and mutation surface for the daemon
  configuration document, including secret redaction, validation-before-persist,
  read-only daemon fields, authorization, audit, and restart semantics.

### Modified Capabilities

- `netclaw-config-hot-reload`: no requirement changes. The watcher remains the
  single reload trigger; the new endpoint only writes to disk. Listed here for
  traceability only — no delta file.

## Impact

- **Code**: `Netclaw.Configuration` (shared write engine), `Netclaw.Daemon`
  (new endpoint route builder, document service, audit), `Netclaw.Cli`
  (forwarding wrappers over the moved engine).
- **APIs**: new authenticated endpoints `GET /api/config` and
  `PATCH /api/config` on the daemon control plane. No existing endpoint changes.
- **Security**: config writes require operator authority. Secret leaves are
  redacted on read and write-only on patch. `Daemon.Host`, `Daemon.ExposureMode`,
  and `Daemon.TrustedProxies` are read-only over HTTP to prevent lockout and
  forwarded-header auth bypass (PRD-002 security envelope). The `Security`
  section stays editable and is audited at warning level.
- **Operations**: accepted patches trigger the existing coordinated restart;
  clients poll `/api/health/ready` and compare `X-Netclaw-Generation`.
- **Out of scope**: typed per-section endpoints, optimistic concurrency,
  editing the three read-only daemon fields over HTTP, and the operator UI
  itself (PRD-003 Phase 5 deferral stands).

Source PRDs: PRD-003 (UX-006), PRD-004 (CLI-001B), PRD-002 (gateway security
envelope).
