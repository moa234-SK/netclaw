# Design: add-config-http-api

## Context

See `proposal.md` for motivation. Current state that shapes this design:

- `netclaw.json` and `secrets.json` are plain JSON files under the config
  directory owned by `NetclawPaths`. The daemon reads them at startup through
  `IConfiguration` (`DaemonConfigurationSources`).
- All mutation is local today. The merge engine (`ConfigFileHelper`,
  `ConfigEditorSession`) lives in `Netclaw.Cli`, which the daemon cannot
  reference.
- `ConfigWatcherService` is the single reload trigger: every change goes to
  disk, the watcher debounces, validates, and drives a coordinated restart
  through `IDaemonRestartCoordinator`.
- `SecretsFileWriter`, `ISecretsProtector`, `AtomicFile`,
  `EmbeddedSchemaLoader`, and `DaemonExposureValidator` already live in
  `Netclaw.Configuration`.
- Endpoint precedent: `WebhookRouteEndpointRouteBuilderExtensions` (operator
  authority, validate-then-persist, secret never returned).

## Goals / Non-Goals

**Goals:**

- One shared write engine for CLI, TUI, and HTTP.
- Round-trippable read-modify-write: `GET` then `PATCH` with the same document
  view preserves secrets.
- Zero secret disclosure on any path (response, log, audit, error).
- No new reload trigger. The endpoint only writes files.

**Non-Goals:**

- Typed per-section endpoints (deferred; the merge patch covers them).
- Optimistic concurrency (last write wins, documented).
- Editing `Daemon.Host`, `Daemon.ExposureMode`, `Daemon.TrustedProxies` over
  HTTP (permanent policy, not a phased deferral).
- Operator UI itself (PRD-003 Phase 5 deferral stands).

## Decisions

### D1. Move the write engine to `Netclaw.Configuration`

Relocate the file-level primitives from `Netclaw.Cli/Config/ConfigFileHelper.cs`
and the merge semantics from `ConfigEditorSession` into a new
`NetclawConfigFiles` type in `Netclaw.Configuration`:

- `LoadConfig`, `LoadSecrets`, `WriteConfig` (atomic, `configVersion` stamped),
  `WriteSecrets` (encrypted, via protector).
- Path set/delete with existing-key case resolution. This prevents the
  duplicate `Identity`/`identity` key failure mode.
- Secrets path merge with colon-collision cleanup and empty-section prune.
- The `OAuthTokenExpiry` exception moves with it: the expiry stays in
  `netclaw.json` because encrypted dates break `IConfiguration` binding.

The CLI keeps `ConfigFileHelper` as an internal forwarding wrapper so call
sites do not churn. Alternative — duplicating the logic in the daemon — was
rejected: two writers of the same file drift, and the key-case hazard is
load-bearing.

### D2. The secret file defines the secret set

There is no hardcoded list of secret paths. `GET` loads both files, overlays
`secrets.json` onto `netclaw.json`, and replaces every secrets-file leaf with
the sentinel string `__netclaw_secret__`. The response carries the redacted
document plus `redactedPaths`. This auto-tracks future secret paths.

A new secret appears only when the patch supplies a plain string at a path the
engine classifies as secret-worthy. Classification follows the existing
convention: `Providers.{name}.ApiKey` and the credential leaves that
`ProviderCredentialWriter` and `OAuthTokenPersistence` already manage. A patch
that supplies a plain string at any other leaf writes that leaf to
`netclaw.json` unencrypted. This is the same trust model the CLI has today.

Sentinel semantics on `PATCH`:

| Patch value at secret path | Result |
|----------------------------|--------|
| `__netclaw_secret__`       | stored value kept |
| plain string               | stored value replaced, encrypted |
| `null`                     | stored value deleted |

### D3. Document service in the daemon, pure helpers in Configuration

`ConfigDocumentService` (daemon, `Config/` folder) owns the pipeline below. It
depends on `NetclawPaths`, `ISecretsProtector`, `IEnumerable<ILlmProviderPlugin>`
(provider type validation), `TimeProvider`, and a logger. The pure parts —
merge, redact, split, read-only path detection — are static helpers in
`Netclaw.Configuration` so both test suites reach them without a host.

Schematic pipeline (omits auth, rate limit, audit, and request logging):

```
load config + secrets            (durable files)
effective = config + secrets overlaid
touched = collect-patch-paths(patch)
reject 400 if touched ∩ readOnlyPaths ≠ ∅
merged = rfc7386-merge(effective, patch)
split merged -> configPart, secretsPart (sentinel = keep)
validate configPart:
    schema (netclaw-config.v1)            -> 400 with field errors
    IConfiguration build + PolicyConfiguration bind -> 400
    provider Type exists in registry/plugins -> 400
    DaemonExposureValidator on bound Daemon -> 400
write secrets.json atomically              (overlay first; harmless alone)
write netclaw.json atomically              (watcher fires from here)
respond 200 { changedTopLevel, restartPending: true, generation }
```

Write order is secrets-then-config: a `netclaw.json` write is what triggers
the watcher, so config must be the last write. A failed config write after a
successful secrets write leaves an overlay the old config already tolerates
(extra secret keys are ignored until referenced).

### D4. Read-only paths are exact top-level-section children

`readOnlyPaths = ["Daemon.Host", "Daemon.ExposureMode", "Daemon.TrustedProxies"]`.
Detection walks the patch and collects every touched path — set, change, delete,
even a repeat of the current value. Any intersection rejects the whole patch
with `400`. Other `Daemon.*` fields (`Port`, `UpdateChannel`,
`DisableSelfUpdate`, `SkipTunnelProcessCheck`) stay editable. The list is one
public constant; `GET` returns it in `readOnlyPaths` so clients need no
hardcoded copy.

### D5. Validation reuses existing gates

The endpoint does not invent validators. It reuses, in order: the v1 schema
embedded resource, the `IConfiguration` build + `PolicyConfiguration.Bind`
path (the same code the TUI `EnsureLoads` uses), provider registry membership
(same as startup), and `DaemonExposureValidator` (same as startup). This keeps
the endpoint and the watcher in agreement: whatever the endpoint accepts, the
watcher accepts on reload.

### D6. Response shapes

- `GET /api/config` → `200`
  `{ "config": <redacted document>, "redactedPaths": [...], "readOnlyPaths": [...] }`
- `PATCH /api/config` → `200`
  `{ "changedTopLevel": [...], "restartPending": true, "restartGeneration": n }`
- Failures → `400` with `{ "errors": [...], "rejectedPaths": [...] }`
  (`rejectedPaths` present only for read-only rejections). No secret ever
  appears in any shape.

### D7. Authorization, rate limit, audit

- Both endpoints: `.RequireAuthorization()` default operator policy — same as
  every other control-plane route. Loopback callers receive the auto-operator
  ticket; paired devices present their token.
- `PATCH` sits behind a `config-write` rate-limit policy modeled on the
  existing `local-control` policy.
- Audit through `ILogger` at the endpoint: actor identity, verb, changed
  top-level sections. `Security`-section changes log at warning level and
  include old/new values of the three `Security` fields (enums/booleans, not
  secrets). Audit data is call-local; durability comes from the daemon log
  pipeline, not from new state.

### D8. Actor boundaries unchanged

No actor owns config. The service is call-local within the HTTP request; the
files are the durable state; the watcher (a hosted service) initiates the
coordinated restart exactly as it does for a TUI save. Sessions drain through
the existing restart coordinator.

## Risks / Trade-offs

- [Two writers race: HTTP patch vs. local TUI save] → Last write wins by
  design (user decision). The key-case resolution makes the merge
  non-destructive per-path; full-section patches can still clobber a
  concurrent edit. Documented; optimistic concurrency is a future change.
- [Secrets write succeeds, config write fails] → Server responds `500` loudly;
  the extra overlay keys are inert for the old config. The next `GET` shows the
  new sentinel. No silent fallback.
- [Watcher disagrees with endpoint validation] → Not expected: both use the
  same gates (D5). If an external edit interleaves between write and reload,
  the watcher rejects and the daemon keeps the previous effective config —
  existing behavior, logged.
- [Sentinel string collides with a real value] → A real API key equal to the
  sentinel is indistinguishable from "keep". Acceptable: the sentinel is a
  reserved value, documented in the endpoint summary.
- [Engine relocation breaks CLI flows] → Forwarding wrappers keep call sites
  stable; the full CLI test suite and light smoke tapes cover the moved code.

## Migration Plan

Ship as one backwards-compatible change: new endpoints, moved engine, wrapper
shims. No file format changes, no schema changes. Rollback is a revert; no
data migration.

## Open Questions

- None blocking. Typed per-section endpoints and concurrency control can be
  added later without changing this contract.
