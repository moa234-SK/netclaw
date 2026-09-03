## Why

Netclaw's native four-layer invocation stack is code-owned and typed. Operators cannot author their own deny rules without rebuilding the daemon. The Agent Governance Toolkit (AGT) ships the Agent Control Specification (ACS): a stateless, deterministic, fail-closed policy runtime with a .NET SDK (`AgentControlSpecification`) that evaluates Rego policy at intervention points. An ACS gate gives operators a portable, file-authored policy layer over tool calls while Netclaw keeps its native default-deny stack underneath.

## What Changes

- Add an additive deny-overrides governor gate to tool invocation. The gate runs after tool resolution and before the native `ToolAccessPolicy`. A governor deny is categorical. It cannot be approved through chat.
- Add a new `Netclaw.Governance` assembly that binds the ACS .NET SDK. It loads one manifest, validates it at startup, and evaluates the `pre_tool_call` intervention point with a Rego policy.
- Map ACS verdicts to Netclaw decisions: `allow` and `warn` proceed, `deny` blocks with the policy reason, `escalate` and `transform` block fail-closed for this phase, and evaluation errors block fail-closed.
- Add a `Governance` config section with `Enabled`, `ManifestPath`, and `OpaPath`. The section is disabled by default.
- Fail loud at startup: an enabled section with a missing manifest, an invalid manifest, an unsupported policy type, or Rego policy without a resolvable OPA binary refuses daemon start.
- Surface governor denies to the operator and the model through the standard deny reason surface, with `netclaw doctor` checks and `netclaw-operations` skill guidance.

In scope: the `pre_tool_call` point only, Rego policy only, deny-overrides composition with the native stack, config schema, doctor check, telemetry, tests, and operator guidance.

Out of scope: Cedar and custom policy types, the `escalate`-to-approval bridge, `transform` application, `post_tool_call`, `input`, `output`, model points, annotators, and the ACS `extends` chain.

## Capabilities

### New Capabilities

- `agent-policy-governance`: External Rego policy evaluation as an additive, fail-closed gate on resolved tool invocations.

### Modified Capabilities

None.

## Impact

- Configuration: new optional `Governance` section in the v1 schema with defaults; absent section keeps prior behavior; invalid or incomplete enabled sections block startup.
- Runtime: one new gate inside `DispatchingToolExecutor` on all entry points; sub-agent executors receive the same governor as their parent.
- Interfaces: no CLI or tool changes; `netclaw doctor` gains an `acs-policy` check; OTel gains `acs_intervention_*` metrics from the SDK sink.
- Security: the gate can only tighten. It runs after native hard-deny layers receive the call and before approvals. All failures deny.
- Operations: governor denies use reason codes `acs_policy_denied`, `acs_policy_escalate`, `acs_transform_unsupported`, and `acs_evaluation_failed`.
