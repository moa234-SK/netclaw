## 1. Library foundation

- [x] 1.1 Create `src/Netclaw.Registration` class library and add it to the solution
- [x] 1.2 Implement `RegistrationConfig` with `[ConfigValue]` metadata and `RegistrationConfigValidator` (URL must be absolute http/https URI when set; interval floor 30 s)
- [x] 1.3 Declare the `Registration` section in `netclaw-config.v1.schema.json` with defaults and `additionalProperties: false`
- [x] 1.4 Implement `RegistrationState` record plus atomic read/write helpers (producer-consumer shared shape)

## 2. Registration service

- [x] 2.1 Implement `IStatusSnapshotProvider` seam and the registration envelope DTO
- [x] 2.2 Implement `SelfRegistrationService` background service: startup send, `PeriodicTimer` heartbeat via `TimeProvider`, 10 s timeout, warn-and-continue on failure, state file update, stable `instanceId`
- [x] 2.3 Add DI extensions: typed client with `AddNetclawHeaders("self-registration")` and hosted-service registration gated on `Enabled` + `Url`

## 3. Daemon wiring

- [x] 3.1 Add project reference and wire `SelfRegistrationService` in `Program.cs` with an adapter over `DaemonRuntimeStatusService`
- [x] 3.2 Fail startup loudly when a configured `Url` is invalid; verify unset/disabled stays silent

## 4. Doctor surfacing

- [x] 4.1 Implement `RegistrationDoctorCheck` (Error invalid URL, Warning failed last send, Pass not configured) and register it in `DoctorRegistrationExtensions`

## 5. TUI config editor

- [x] 5.1 Add Registration section editor with URL and interval validation that blocks save before persistence
- [x] 5.2 Register the section in the config dashboard

## 6. Tests

- [ ] 6.1 Library tests: config binding, validator accept/reject cases, envelope serialization, state round-trip — SKIPPED by operator instruction ("skip tests"); draft test project was removed to keep the solution green
- [ ] 6.2 Service tests with fake `HttpMessageHandler`: startup send, heartbeat repeat, failure does not crash, disabled sends nothing, stable `instanceId` across restarts — SKIPPED by operator instruction
- [ ] 6.3 TUI tests: valid URL persists, invalid URL leaves file unchanged, fake write failure blocks save — SKIPPED by operator instruction
- [ ] 6.4 Doctor check tests: Error, Warning, Pass cases — SKIPPED by operator instruction
- [ ] 6.5 Schema doctor test: section validates, unknown property rejected, `--fix` inserts defaults — SKIPPED by operator instruction

## 7. Documentation and gates

- [x] 7.1 Update `netclaw-operations` skill with the new config section and bump its version (2.65.4 → 2.66.0)
- [x] 7.2 Run `dotnet slopwatch analyze` (0 issues) and `./scripts/Add-FileHeaders.ps1 -Verify` (all headers present); `dotnet build Netclaw.slnx` clean
- [ ] 7.3 Full test suite and `./evals/run-evals.sh` — SKIPPED by operator instruction

## 8. Environment variable surface

- [x] 8.1 Confirm the `NETCLAW_Registration__*` env vars flow through the existing layered config chain (daemon and CLI both register `.AddEnvironmentVariables("NETCLAW_")` before section binding) — no production code change required
- [x] 8.2 Document the env form with a Docker Compose example in `docs/spec/configuration.md` § Environment Variable Overrides
- [x] 8.3 Add the env-override scenario to the delta spec and the configuration note to PRD-010
- [x] 8.4 Document the env form and the env-beats-file shadowing rule in the `netclaw-operations` skill (2.66.0 → 2.66.1)

## 9. Base-URL endpoint model

- [x] 9.1 Rename `Url` → `BaseUrl` in `RegistrationConfig`, validator messages, `[ConfigValue]` key, and the schema (clean rename; feature unreleased)
- [x] 9.2 Add `RegistrationEndpoints` derivation (`api/v1/webhook/agent/status`, `api/v1/webhook/agent/heartbeat`) with trailing-slash and reverse-proxy-prefix normalization
- [x] 9.3 Add `HeartbeatPayload` (`instanceId`, `timestamp`) and health-projection change detection in `SelfRegistrationService` (volatile fields excluded)
- [x] 9.4 Name the failed endpoint in the persisted `errorDetail` (status/heartbeat label; still URL-free)
- [x] 9.5 Update TUI editor, dashboard summary, and help text to `BaseUrl` with derived-path hints
- [x] 9.6 Update contracts: `docs/spec/configuration.md` env/compose examples, skill 2.66.1 → 2.66.2, PRD-010 (config, endpoints, payloads, delivery model, REG-001..007), delta spec (derivation + payload + heartbeat/change-detection requirements)
