## ADDED Requirements

### Requirement: External policy gate is additive and fail-closed

The system SHALL evaluate resolved tool invocations against an external ACS Rego
policy before the native tool access policy authorizes them. The gate SHALL run
after tool resolution and before approval processing. A governor deny SHALL be
categorical and SHALL NOT be approvable through chat. The gate SHALL only
restrict: no governor outcome SHALL loosen any native layer. When governance is
disabled, the executor SHALL behave exactly as before.

#### Scenario: Governor deny blocks a natively allowed call

- **GIVEN** governance is enabled and the policy denies a tool call
- **WHEN** the native policy would allow that call
- **THEN** the invocation is denied and the deny reason identifies the policy

#### Scenario: Governor allow does not bypass native gates

- **GIVEN** governance is enabled and the policy allows a tool call
- **WHEN** the native policy denies or requires approval for that call
- **THEN** the native decision applies unchanged

#### Scenario: Unknown tool denies before governance

- **GIVEN** a tool call names a tool that is not registered
- **WHEN** the executor authorizes the call
- **THEN** the call is denied as unknown before the governor is consulted

#### Scenario: Disabled governance keeps prior behavior

- **GIVEN** the `Governance` section is absent or `Enabled` is false
- **WHEN** tool calls execute on any entry point
- **THEN** no ACS types are constructed and behavior is byte-identical to before

#### Scenario: Gate covers every execution path

- **GIVEN** governance is enabled
- **WHEN** a tool call executes through the non-streaming path, the streaming
  path, or standalone authorization
- **THEN** the governor gate evaluates the call on each path

### Requirement: Verdicts map to a closed set of deny reasons

The system SHALL map every ACS verdict to a Netclaw decision. `allow` and `warn`
SHALL proceed, with `warn` recorded as a structured warning. `deny` SHALL block
with reason `acs_policy_denied` and the policy's reason attribution. `escalate`
SHALL block fail-closed with reason `acs_policy_escalate`. `transform` SHALL
block fail-closed with reason `acs_transform_unsupported`. Any evaluation error
SHALL block fail-closed with reason `acs_evaluation_failed`. No code path SHALL
return an implicit allow.

#### Scenario: Policy deny carries rule attribution

- **GIVEN** the policy returns a deny verdict with a reason code
- **WHEN** the governor maps the verdict
- **THEN** the deny reason contains `acs_policy_denied` and the policy reason

#### Scenario: Escalate fails closed

- **GIVEN** the policy returns an escalate verdict
- **WHEN** no approval backend is wired
- **THEN** the call is denied with reason `acs_policy_escalate`

#### Scenario: Transform is rejected

- **GIVEN** the policy returns a transform verdict
- **WHEN** the governor maps the verdict
- **THEN** the call is denied with reason `acs_transform_unsupported`
- **AND** the original arguments are neither mutated nor the transform applied

#### Scenario: Engine failure denies

- **GIVEN** the ACS evaluation throws
- **WHEN** the governor handles the failure
- **THEN** the call is denied with reason `acs_evaluation_failed` and the error
  is logged

### Requirement: Governance startup fails loud

When governance is enabled, the daemon SHALL refuse to start when the manifest
is missing, fails artifact validation, binds a policy type other than `rego`,
omits or empties the `tools` catalog, does not bind the `pre_tool_call`
intervention point, or declares Rego policy while no OPA binary resolves. The
system SHALL resolve OPA from the configured `OpaPath`, then the
`ACS_OPA_PATH` environment variable, then `PATH`. The system SHALL NOT start
with an implicitly empty or partial policy. A tool call for a tool absent
from the manifest `tools` catalog SHALL deny fail-closed at evaluation time
with the runtime's `runtime_error:tool_unknown` reason.

#### Scenario: Missing manifest refuses start

- **GIVEN** governance is enabled and the manifest path does not exist
- **WHEN** the daemon constructs governance
- **THEN** construction throws and the daemon does not start

#### Scenario: Invalid manifest refuses start

- **GIVEN** governance is enabled and the manifest fails `ArtifactValidator`
  validation
- **WHEN** the daemon constructs governance
- **THEN** construction throws with the validation diagnostics

#### Scenario: Unsupported policy type refuses start

- **GIVEN** the manifest binds a `cedar`, `test`, or `custom` policy
- **WHEN** the daemon constructs governance
- **THEN** construction throws with an unsupported-policy-type error

#### Scenario: Empty tools catalog refuses start

- **GIVEN** the manifest omits the `tools` block or declares an empty catalog
- **WHEN** the daemon constructs governance
- **THEN** construction throws naming the tools catalog, because every
  evaluated tool call would otherwise deny at runtime

#### Scenario: Rego without OPA refuses start

- **GIVEN** the manifest declares a Rego policy and no OPA binary resolves
- **WHEN** the daemon constructs governance
- **THEN** construction throws naming the OPA resolution paths

#### Scenario: Uncataloged tool denies at evaluation time

- **GIVEN** governance is enabled with a valid manifest and tools catalog
- **WHEN** a tool call names a tool absent from the catalog
- **THEN** the ACS runtime denies the call with `runtime_error:tool_unknown`
- **AND** the governor surfaces that reason in the deny

### Requirement: Configuration is schema-bound and default-off

The `Governance` section SHALL be optional in the v1 config schema with
`Enabled` defaulting to false, `ManifestPath` defaulting to
`acs/manifest.yaml`, and `OpaPath` optional. Relative manifest paths SHALL
resolve against the Netclaw config directory. An absent section SHALL produce
disabled governance. The schema SHALL reject unknown properties in the section.

#### Scenario: Absent section is disabled

- **GIVEN** a config file without a `Governance` section
- **WHEN** the daemon loads configuration
- **THEN** governance is disabled and the NoOp governor is registered

#### Scenario: Relative manifest resolves against config directory

- **GIVEN** `ManifestPath` is `acs/manifest.yaml`
- **WHEN** governance is constructed
- **THEN** the manifest resolves under the Netclaw config directory

#### Scenario: Schema rejects unknown fields

- **GIVEN** a config file sets an unknown property inside `Governance`
- **WHEN** the schema validates the config
- **THEN** validation fails

### Requirement: Sub-agent executors share the parent governor

Sub-agent tool executors SHALL receive the same governor instance as their
parent session. The spawner SHALL require a governor dependency. A missing
governor on the sub-agent path SHALL be a construction error, not a nullable
silent skip.

#### Scenario: Sub-agent calls are governed

- **GIVEN** governance is enabled and a sub-agent invokes a tool its policy
  denies
- **WHEN** the sub-agent's executor authorizes the call
- **THEN** the call is denied with the policy deny reason

#### Scenario: Spawner without governor fails construction

- **GIVEN** a `SubAgentSpawner` is constructed without a governor
- **WHEN** the constructor runs
- **THEN** construction fails

### Requirement: Governance state is observable

The system SHALL report governance health through `netclaw doctor` with an
`acs-policy` check covering manifest presence, validation, bound policy types,
and OPA resolution. The system SHALL emit ACS decision metrics through the SDK
telemetry sink. Doctor SHALL report a disabled section as passing with a
disabled note rather than failing.

#### Scenario: Doctor reports enabled governance health

- **GIVEN** governance is enabled with a valid manifest and resolvable OPA
- **WHEN** `netclaw doctor` runs the `acs-policy` check
- **THEN** the check passes and reports the manifest and OPA resolution

#### Scenario: Doctor reports misconfiguration

- **GIVEN** governance is enabled with a manifest that no longer validates
- **WHEN** `netclaw doctor` runs the `acs-policy` check
- **THEN** the check fails with the reason

#### Scenario: Decision metrics are emitted

- **GIVEN** governance is enabled and a tool call is evaluated
- **WHEN** the policy returns a verdict
- **THEN** the `acs_intervention_*` counters record the decision
