# ACS Policy Governance Runbook

This runbook explains how to operate the Agent Control Specification (ACS)
policy integration. ACS evaluates an operator-authored Rego policy before
every tool call. Netclaw keeps its native four-layer gate underneath. The ACS
layer can only add denies.

## How it composes

A tool call passes these gates in order:

1. Tool resolution — unknown tools deny.
2. **ACS governor (this feature)** — Rego policy deny is categorical.
3. Native policy — hard deny lists, audience grants, approval gates.

A deny at any layer blocks the call. An ACS allow never bypasses layer 3.

## Enable

1. Install OPA on the machine that runs the daemon
   ([openpolicyagent.org](https://www.openpolicyagent.org/docs/latest/#install)).
   Netclaw resolves OPA in this order: `Governance.OpaPath`, the
   `ACS_OPA_PATH` environment variable, then `PATH`.
2. Create `~/.netclaw/config/acs/manifest.yaml`. Every tool the policy should
   govern must appear in the `tools` catalog — a tool call for a tool that is
   absent from the catalog is denied with `runtime_error:tool_unknown`:

   ```yaml
   agent_control_specification_version: "0.3.1-beta"
   metadata:
     name: netclaw
   policies:
     tool_policy:
       type: rego
       bundle: ./policy
       query: data.netclaw.verdict
   intervention_points:
     pre_tool_call:
       policy_target: "$.tool_call.args"
       policy_target_kind: tool_args
       tool_name_from: "$.tool_call.name"
       policy:
         id: tool_policy
   tools:
     file_write:
       type: Tool
       id: file_write
       clearance: internal
     file_read:
       type: Tool
       id: file_read
       clearance: internal
     shell_execute:
       type: Tool
       id: shell_execute
       clearance: restricted
   ```

3. Create `~/.netclaw/config/acs/policy/netclaw.rego`. This sample denies
   `file_write` for team and public audiences:

   ```rego
   package netclaw

   import rego.v1

   default verdict := {"decision": "allow"}

   verdict := {
     "decision": "deny",
     "reason": "write_blocked_for_audience",
     "message": "This audience may not write files.",
   } if {
     input.tool.name == "file_write"
     input.snapshot.session.audience in {"team", "public"}
   }
   ```

   The policy reads this input, projected from the snapshot Netclaw sends:

   - `input.tool.name` — the tool name (from `tool_name_from`).
   - `input.policy_target.value` — the tool arguments (from `policy_target`).
   - `input.snapshot` — the full snapshot:

   ```json
   {
     "tool_call": { "name": "file_write", "args": { "Path": "/tmp/a.txt" } },
     "session": { "id": "slack/C123/T456", "audience": "team", "channel_type": "slack", "boundary": "boundary:trusted-instance" }
   }
   ```

4. Add the section to `~/.netclaw/config/netclaw.json` and restart the daemon:

   ```json
   {
     "Governance": {
       "Enabled": true,
       "ManifestPath": "acs/manifest.yaml",
       "OpaPath": null
     }
   }
   ```

## Verdicts

| Rego decision | Netclaw behavior |
|---|---|
| `allow` | Proceed. |
| `warn` | Proceed; warning logged. |
| `deny` | Blocked with `acs_policy_denied: {reason}`. |
| `escalate` | Blocked with `acs_policy_escalate` — no approval backend is wired in this version. |
| `transform` | Blocked with `acs_transform_unsupported` — argument transforms are not supported in this version. |

## Deny reasons

- `acs_policy_denied: {reason}` — the policy denied the call. The reason code
  comes from the policy.
- `acs_policy_escalate` — the policy asked for a human; none is wired.
- `acs_transform_unsupported` — the policy returned a transform.
- `acs_evaluation_failed` — the engine errored; the call was blocked on
  purpose (fail closed). Report this as a governance malfunction.

Governance denies are categorical. No approval prompt appears, and no chat
approval can override them. Change the policy file and restart the daemon.

## Startup refusals

The daemon refuses to start when `Governance:Enabled` is true and:

- the manifest file is missing,
- the manifest fails ACS artifact validation,
- any policy type is not `rego`,
- the `pre_tool_call` intervention point is not bound,
- the `tools` catalog is missing or empty,
- no OPA binary resolves.

Every refusal writes the reason to the daemon log. Run `netclaw doctor` to
re-check without starting the daemon; the `ACS Policy Governance` check
reports the manifest, OPA path, and OPA version.

## Limitations

- Policy changes need a daemon restart; there is no hot reload.
- Only `pre_tool_call` is evaluated. Input, output, and model calls are not
  mediated.
- `escalate` and `transform` fail closed (see Verdicts).
- Metrics: ACS decision counters (`acs_intervention_*`) export through the
  existing OpenTelemetry pipeline when telemetry is enabled.
