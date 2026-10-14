## 1. Shared write engine

- [x] 1.1 Create `NetclawConfigFiles` in `Netclaw.Configuration`: load/write
  both files, atomic writes, `configVersion` stamp, path set/delete with
  existing-key case resolution, secrets path merge with collision cleanup and
  empty-section prune, `OAuthTokenExpiry` exception.
- [x] 1.2 Convert `Netclaw.Cli` `ConfigFileHelper` and `ConfigEditorSession`
  to forwarding wrappers over the moved engine; no behavior change.
- [x] 1.3 Move/extend engine tests into `Netclaw.Configuration.Tests`: merge,
  delete, key-case duplicate prevention, secrets split round-trip, prune.
- [x] 1.4 Run the CLI test suite and `./scripts/smoke/run-smoke.sh light` to
  prove the move is behavior-neutral. (CLI suite: 2144 passed; the 10 failures
  also fail on clean HEAD — pre-existing, unrelated. Smoke harness run skipped
  per operator: this machine's WSL has no dotnet on PATH for the linux-x64
  publish step. The `config-api` scenario is authored and registered in the
  light set, so CI covers it.)

## 2. Document pipeline helpers

- [x] 2.1 Implement pure helpers in `Netclaw.Configuration`: effective-view
  overlay (config + secrets), sentinel redaction with `redactedPaths`
  collection, RFC 7386 merge over `Dictionary<string, object>`, patch
  touched-path collection, read-only path intersection, merged-document split
  (sentinel = keep, string = set secret, null = delete).
- [x] 2.2 Unit tests: redaction replaces exactly the secrets-file leaves;
  sentinel round-trip preserves stored secret; null deletes and prunes;
  read-only detection catches set/change/delete/same-value touches; merge
  keeps unrelated keys.

## 3. Validation gate

- [x] 3.1 Implement merged-document validation in the daemon: v1 schema check,
  `IConfiguration` build + bind, provider type existence, exposure validator.
  Return field-level errors.
- [x] 3.2 Fake-failure tests (Automation Floor): schema-invalid value,
  unknown provider type, exposure-invalid daemon values each return 400 and
  prove both files unchanged on disk.

## 4. Endpoints

- [x] 4.1 Add `ConfigEndpointRouteBuilderExtensions`:
  `GET /api/config` (operator-only, redacted document, `redactedPaths`,
  `readOnlyPaths`) and `PATCH /api/config` (merge patch, read-only rejection
  with `rejectedPaths`, all-or-nothing persist, response with
  `changedTopLevel`, `restartPending`, `restartGeneration`).
- [x] 4.2 Register the endpoints, the `config-write` rate-limit policy, and
  `ConfigDocumentService` in `Program.cs`.
- [x] 4.3 Endpoint tests: auth matrix (anonymous 401, operator 200), redaction
  on GET, patch happy path writes and reports pending restart, read-only
  paths rejected (including same-value touch), secrets encrypted into
  secrets.json and absent from netclaw.json, error bodies carry no secret.
- [x] 4.4 Integration test: accepted patch drives `ConfigWatcherService`
  validation through `PendingReload` (no direct restart call).

## 5. Audit

- [x] 5.1 Audit every authorized PATCH: actor, verb, changed top-level paths.
  `Security` changes log at warning level with old/new values of the three
  Security fields. Test: security change entry contains values; secret change
  entry contains no key material.

## 6. Docs, skills, smoke

- [x] 6.1 Add a non-interactive HTTP smoke scenario covering GET redaction,
  PATCH happy path, and read-only rejection. (Scenario `config-api` authored and
  registered in the light set; its harness run is deferred to CI — skipped
  locally per operator because this machine's WSL lacks dotnet for the
  linux-x64 publish step.)
- [x] 6.2 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md`
  with the config API surface; bump `metadata.version`.
- [x] 6.3 Update `docs/architecture/tool-authorization.md` only if the change
  touches an authorization context (expected: no — confirm and record).
  Confirmed: the config API uses the standard control-plane operator policy
  and does not alter any tool-authorization context, owner, grant scope,
  candidate kind, or consent surface. No doc change required.
- [x] 6.4 Post-code gates: `dotnet slopwatch analyze` (0 issues),
  `./scripts/Add-FileHeaders.ps1 -Verify` (all files have headers), full daemon
  + configuration test suites (1453 + 806 passed, 0 failed).
