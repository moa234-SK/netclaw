## 1. Slice 1 — AGT foundation

- [x] 1.1 Add `Microsoft.AgentGovernance` 5.0.0 to
  `Directory.Packages.props` and reference it from
  `Netclaw.Governance.csproj`
- [x] 1.2 Create `SessionIntervention/AgentGovernanceRuntime.cs`:
  armed `KillSwitch`, per-session `LifecycleManager` registry,
  `AuditLogger`; always-on
- [x] 1.3 Create
  `SessionIntervention/GovernanceServiceCollectionExtensions.cs` with
  `AddAgentGovernance()`

## 2. Slice 2 — Session intervention

- [x] 2.1 Create `src/Netclaw.Actors/Protocol/SessionInterventionControl.cs`:
  `SessionInterventionState` enum, four command records,
  `ISessionInterventionStateReader` port, default active reader
- [x] 2.2 Create
  `src/Netclaw.Governance/SessionIntervention/SessionInterventionStore.cs`:
  SQLite state upsert + append-only event ledger, idempotent DDL,
  implements the reader port
- [x] 2.3 Create
  `src/Netclaw.Governance/SessionIntervention/SessionInterventionCoordinator.cs`:
  transition validation, durable write first, actor ask, AGT kill /
  transition / audit, idempotent same-state commands
- [x] 2.4 `LlmSessionActor`: intervention command handlers (all
  phases), ingress gate NACKs, recovery gate in `RecoveryCompleted`,
  quarantine tool denial at tool-batch dispatch, stop routing into
  passivation with ack in `FinalizePassivation`
- [x] 2.5 `SessionServices` gains the reader; update both
  construction sites (`Program.cs`, `LlmSessionTestExtensions.cs`)
- [x] 2.6 Create
  `src/Netclaw.Daemon/SessionInterventionEndpointRouteBuilderExtensions.cs`:
  catch-all POST route + GET interventions ledger route, Operator
  auth, URL-decoded ids. Note: ASP.NET Core forbids a catch-all
  followed by `/stop`, so each verb mounts one catch-all and the
  trailing path segment names the command — the URL surface
  `/api/sessions/<id>/<command>` is unchanged.
- [x] 2.7 Wire `Program.cs`: `AddAgentGovernance()`, reader into
  `SessionServices`, `MapSessionInterventionEndpoints()`

## 3. Slice 3 — Input safety (later)

- [ ] 3.1 `PromptInjectionDetector` at session ingress (spec delta
  required before build)

## 4. Slice 4 — Runtime limits (later)

- [ ] 4.1 AGT rate limiting wired to per-session budgets (spec delta
  required before build)

## 5. Slice 5 — Observability (later)

- [ ] 5.1 Governance metrics + audit export into the existing OTel
  pipeline (spec delta required before build)

## 6. Docs and gates

- [x] 6.1 `docs/runbooks/session-intervention.md`
- [x] 6.2 `netclaw-operations` skill: intervention commands section +
  version bump
- [x] 6.3 `dotnet build` passes
- [x] 6.4 `dotnet slopwatch analyze` passes with no new violations
- [x] 6.5 `./scripts/Add-FileHeaders.ps1 -Verify` passes

## 7. Tests (deferred by operator instruction)

- [ ] 7.1 Actor tests: stop/suspend abort mid-turn, gate NACKs,
  recovery gate, quarantine tool denial
- [ ] 7.2 Store tests: round-trip, idempotent DDL, ledger ordering
- [ ] 7.3 Coordinator tests: transition matrix, durable-before-actuate
  ordering
- [ ] 7.4 Endpoint tests: Operator auth, encoded ids, 409 on invalid
  transitions

Note: the operator deferred all test work. Do not archive this change
before section 7 is complete.
