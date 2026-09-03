## Context

`DispatchingToolExecutor.EvaluateAuthorizationResultAsync` resolves the tool, then runs `ToolAccessPolicy.AuthorizeInvocation` (audience grants, path policy, approval gates). The method is async, so an async external policy call slots between resolution and native authorization without synthesis. Sub-agents build private executors from the same policy objects, so the governor must thread through `SubAgentSpawner` as a required dependency.

The ACS .NET SDK (`AgentControlSpecification` 0.3.1-beta.1) ships the native Rust core alongside the managed assembly and exposes `AgentControl.FromPath(manifest)`, `ArtifactValidator.Validate`, and async intervention-point evaluation. Rego policies evaluate through the bundled OPA dispatcher, which shells out to the `opa` CLI. The SDK is fail-closed by contract: runtime errors return deny verdicts with reserved `runtime_error:` reasons.

## Goals / Non-Goals

**Goals:**

- Evaluate one Rego policy at `pre_tool_call` for every resolved tool call, including sub-agents and streaming paths.
- Fail closed on every governor failure path: deny, escalate, transform, engine error.
- Fail loud at daemon start when config names a policy the runtime cannot load or evaluate.
- Keep the native stack unchanged in behavior when governance is disabled.
- Emit ACS decision metrics through the existing OTel pipeline.

**Non-Goals:**

- Cedar or custom policy types, `escalate` approval bridging, `transform` application, other intervention points, annotators.
- Weakening or replacing any native layer. The governor only adds denies.
- Policy hot-reload. Manifest changes require a daemon restart.

## Decisions

1. **Additive deny-overrides gate, not a replacement PDP.** The governor runs after tool resolution (unknown tools deny first, so the governor never sees unresolvable names) and before `ToolAccessPolicy` (a doomed call never triggers approval-prompt work). A governor deny is categorical and rides the existing `ToolAccessDeniedException` surface. Composition: native-deny OR governor-deny blocks; both must allow for a call to proceed.

2. **Async governor contract.** ACS evaluation is async, so the seam is `Task<ToolGovernanceDecision> CheckAsync(...)` returning `Proceed` or `Deny(reason)`. `NoOpToolInvocationGovernor` is the explicit disabled state; the governor parameter is optional at the executor (defaults to NoOp) and required at `SubAgentSpawner`, because a null governor on the sub-agent path would silently disable checks where the least-supervised code runs.

3. **Rego only, verified at startup.** `AcsGovernance.Build` runs `ArtifactValidator.Validate` and rejects manifests that bind any non-`rego` policy type with an explicit error. This prevents an operator from wiring `cedar` or `custom` engines this change did not test.

4. **Verdict mapping is closed and total.** `allow`/`warn` proceed (warn logs a structured warning); `deny` maps to `acs_policy_denied: {reason}` with policy reason attribution; `escalate` maps to `acs_policy_escalate` fail-closed because no approval backend is wired in this phase (the ACS contract itself fails closed when escalate has no resolver); `transform` maps to `acs_transform_unsupported` fail-closed rather than silently dropping or applying an argument rewrite; any SDK exception maps to `acs_evaluation_failed`. No path returns an implicit allow.

5. **Snapshot shape.** `pre_tool_call` receives `{"tool_call":{"id","name","args"},"session":{"id","audience","principal"}}`. The manifest binds `policy_target: $.tool_call.args` and `tool_name_from: $.tool_call.name`. Arguments are raw values; Rego owns string derivation (`lower`, `contains`, regex). Null argument values are skipped to keep the JSON clean for `object.get`-style policies.

6. **OPA resolution order.** `Governance.OpaPath` config, then the `ACS_OPA_PATH` environment variable, then `PATH`. When the manifest declares Rego policy and no OPA resolves, startup throws: every tool call would otherwise fail closed at runtime with a confusing reason. The doctor check reports the resolved OPA path and version.

7. **One manifest, config-relative default.** `ManifestPath` defaults to `acs/manifest.yaml` under the Netclaw config directory. Relative paths resolve against the config directory, matching `NetclawPaths` conventions. Enabled with a missing manifest throws rather than running an implicitly empty policy.

8. **Metrics through the SDK sink.** `AcsGovernance.Build` passes `OtelMetricsTelemetrySink` to `AgentControl.FromPath`. The sink emits `acs_intervention_*` counters and the duration histogram under the BCL meter `agent_control_specification`, which the existing OTLP pipeline collects. Telemetry is never load-bearing; the SDK isolates sink failures.

9. **DI shape.** `Program.cs` binds `GovernanceConfig`, registers `AcsGovernance` through a factory that throws on invalid configuration (hosted-service construction makes startup fail loud), and registers `IToolInvocationGovernor` as the real governor when enabled or `NoOpToolInvocationGovernor` when disabled. The registration is never null.

10. **Tools catalog is load-bearing.** The ACS runtime resolves each called tool against the manifest's `tools` catalog; an uncataloged tool denies with `runtime_error:tool_unknown`. Verified end-to-end against the native runtime. Consequences: the runbook documents cataloging as part of setup; startup rejects a missing or empty catalog (a misconfiguration that would deny every call at runtime — an intended lockdown is expressed as an explicit deny policy instead); and unlisted tools deny fail-closed at evaluation time with the runtime's own reason surfaced to the model and logs.
