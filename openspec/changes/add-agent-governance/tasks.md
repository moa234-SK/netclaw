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

## 3. Slice 3 — Agent intervention (freeze)

- [x] 3.1 Create `src/Netclaw.Governance/AgentIntervention/AgentInterventionStore.cs`:
  single-row durable agent state + append-only ledger, fail-closed
  reads, idempotent DDL
- [x] 3.2 Create `src/Netclaw.Governance/AgentIntervention/AgentInterventionCoordinator.cs`:
  durable write first, gate actuation, AGT instance-level lifecycle +
  audit, idempotent same-state commands
- [x] 3.3 `SessionIngressGate.AgentSuspendedMessage` constant;
  shutdown-drain task still drains when the gate was closed by
  suspension; `DaemonRestartCoordinator` logs the deferred restart
- [x] 3.4 `DaemonRuntimeStatus.Governance.AgentState` populated from
  the durable reader; registration projection includes it (suspend /
  resume triggers a full status envelope)
- [x] 3.5 Create `src/Netclaw.Daemon/AgentInterventionEndpointRouteBuilderExtensions.cs`:
  `POST /api/agent/suspend|resume`, `GET /api/agent/interventions`,
  Operator auth
- [x] 3.6 `Program.cs`: coordinator registration with the registration
  instance id (`SelfRegistrationService.ResolveInstanceId` made
  public), boot freeze check before the host starts,
  `MapAgentInterventionEndpoints()`
- [x] 3.7 Spec delta `specs/agent-intervention/spec.md` + proposal
  capability entry (this slice)

## 4. Slice 4 — Input safety (later)

- [ ] 4.1 `PromptInjectionDetector` at session ingress (spec delta
  required before build)

## 5. Slice 5 — Runtime limits (later)

- [ ] 5.1 AGT rate limiting wired to per-session budgets (spec delta
  required before build)

## 6. Slice 6 — Observability (later)

- [ ] 6.1 Governance metrics + audit export into the existing OTel
  pipeline (spec delta required before build)

## 7. Docs and gates

- [x] 7.1 `docs/runbooks/session-intervention.md`
- [x] 7.2 `netclaw-operations` skill: intervention commands section +
  version bump
- [x] 7.3 `dotnet build` passes
- [x] 7.4 `dotnet slopwatch analyze` passes with no new violations
- [x] 7.5 `./scripts/Add-FileHeaders.ps1 -Verify` passes
- [x] 7.6 Runbook + integration doc + skill updated for the agent
  freeze

## 8. Tests (deferred by operator instruction)

- [ ] 8.1 Actor tests: stop/suspend abort mid-turn, gate NACKs,
  recovery gate, quarantine tool denial
- [ ] 8.2 Store tests: round-trip, idempotent DDL, ledger ordering
  (session + agent)
- [ ] 8.3 Coordinator tests: transition matrix, durable-before-actuate
  ordering (session + agent)
- [ ] 8.4 Endpoint tests: Operator auth, encoded ids, 409 on invalid
  transitions
- [ ] 8.5 Freeze tests: boot re-close after restart, drain runs while
  frozen, config-reload deferral, projection change triggers envelope

Note: the operator deferred all test work. Do not archive this change
before section 8 is complete.
