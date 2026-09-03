## 1. Governor Seam

- [x] 1.1 Add `IToolInvocationGovernor` with async `CheckAsync` and `ToolGovernorDecision`, plus `NoOpToolInvocationGovernor`
- [x] 1.2 Add the governor gate to `DispatchingToolExecutor` on all entry points (execute, stream, authorize)
- [x] 1.3 Thread the governor through `SubAgentSpawner` (required) and `SubAgentActor` (defaulted to NoOp) and update call sites

## 2. Configuration Contract

- [x] 2.1 Add `GovernanceConfig` with `Enabled`, `ManifestPath`, `OpaPath` and defaults
- [x] 2.2 Extend `netclaw-config.v1.schema.json` with the `Governance` section
- [x] 2.3 Add schema round-trip, absent-section, and unknown-field rejection tests

## 3. ACS Engine Binding

- [x] 3.1 Create `Netclaw.Governance` project referencing `AgentControlSpecification`
- [x] 3.2 Implement `AcsGovernance.Build` with fail-loud validation (manifest, artifact validation, rego-only, tools catalog, OPA resolution) and disposal
- [x] 3.3 Implement `AcsToolInvocationGovernor` with snapshot building and the closed verdict mapping
- [x] 3.4 Add unit tests with a fake runtime: verdict mapping, fail-closed paths, snapshot shape, null-arg handling

## 4. Host Wiring

- [x] 4.1 Register config, `AcsGovernance`, `IToolInvocationGovernor`, and the telemetry sink in `Program.cs`
- [x] 4.2 Prove executor-gate behavior with tests: governor deny blocks native-allowed calls on every entry point, unknown tool denies first, NoOp keeps prior behavior
- [x] 4.3 Add startup fail-loud tests for each refusal condition

## 5. Operator Surfaces

- [x] 5.1 Add the `acs-policy` doctor check with disabled-note and failure reporting
- [x] 5.2 Ship a sample manifest and deny-by-example Rego policy in the operator runbook
- [x] 5.3 Update `netclaw-operations` skill guidance and bump its version

## 6. Verification

- [x] 6.1 Add end-to-end Rego tests (real OPA) including the uncataloged-tool fail-closed case; CI gate `NETCLAW_ACS_REQUIRE_OPA` documented
- [x] 6.2 Run build, targeted and full tests, Slopwatch, and header verification
- [x] 6.3 Verify implementation against OpenSpec artifacts and update tasks

Note: the two sub-agent governor scenarios (spawner construction failure,
sub-agent deny fed back to the model) have no automated test — operator
chose manual verification. All other spec scenarios are covered by
`Netclaw.Governance.Tests` (34 tests) and `Netclaw.Cli.Tests` doctor/schema
tests.
