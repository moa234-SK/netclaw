## Why

Netclaw has no operator-facing control over a running conversation. An
operator who sees a session misbehave can only stop the whole daemon.
Organizational deployments (PRD-003 operator console direction) need a
per-session kill, suspend, and quarantine with an audit trail. The
Microsoft Agent Governance Toolkit (AGT) ships the runtime primitives
(kill switch, lifecycle states, hash-chained audit); Netclaw lacks the
wiring and the durable authority beneath them.

## What Changes

- Adopt the `Microsoft.AgentGovernance` 5.0.0 package in
  `Netclaw.Governance` as an always-on, zero-config in-process runtime:
  an armed `KillSwitch`, per-session `LifecycleManager` instances, and
  an `AuditLogger`.
- Add session intervention: `stop` (abort turn, terminate the session
  actor, durable gate until `resume`), `suspend` (abort turn, actor
  stays warm, gate), `quarantine` (abort turn, gate, all tool calls
  denied), `resume` (clear the gate, validated transition).
- Add Operator-authorized HTTP endpoints
  `POST /api/sessions/{**sessionId}/stop|suspend|quarantine|resume`.
- Persist intervention state and an append-only intervention event
  ledger in the shared SQLite database. SQLite is the durable
  authority; AGT history stays in-memory.
- Bind AGT `agentId` to the session id. Record the initiator in the
  durable ledger because `KillEvent` has no initiator field.
- Block session resurrection while a gate is set: user input,
  reminders, and cold recovery all check the durable state.
- Add agent-level intervention (the freeze): `suspend` closes the
  global ingress gate — all channels, session creation, and reminder
  delivery reject new input; in-flight turns finish; the process,
  HTTP, and heartbeat keep running. `resume` reopens the gate. The
  state is durable: the boot check re-freezes a suspended agent that
  the service manager restarts. Operator endpoints
  `POST /api/agent/suspend|resume` and `GET /api/agent/interventions`;
  agent state is reported in the runtime status and the registration
  projection. Process termination stays host-level.
- Later slices in this change (tasks only, specs marked ADDED when
  built): input-safety detector, rate limiting, governance
  observability.

Related PRDs: PRD-003 (operator UX), PRD-002 (gateway security
envelope). No dedicated PRD exists for governance; this proposal is
the source document.

## Capabilities

### New Capabilities

- `session-intervention`: operator commands that stop, suspend,
  quarantine, and resume a single session; durable gate; resurrection
  blocking; intervention audit ledger.
- `agent-governance-runtime`: the always-on AGT runtime singletons,
  the session-to-agentId binding, and the audit hooks that record
  governance decisions.
- `agent-intervention`: operator commands that freeze and thaw the
  agent as a whole; durable agent state; boot re-freeze; panel-visible
  state.

### Modified Capabilities

(none — existing session requirements do not change text; the
intervention gate is new behavior owned by the new capability)

## Impact

- Code: `Netclaw.Governance` (new intervention subsystems),
  `Netclaw.Actors` (new protocol file, session actor gate + abort
  path, ingress-gate suspended reason), `Netclaw.Daemon` (endpoints,
  boot freeze check, status field, drain/restart interplay,
  DI wiring), `Netclaw.Registration` (instance-id resolution exposed),
  `Directory.Packages.props`.
- Security: new authenticated mutation endpoints (Operator class,
  same default-deny posture as the rest of the API); durable gate is
  fail-closed (unknown session state reads as `Active`, but every
  write goes through validated transitions); no new config surface.
- Operations: new runbook `docs/runbooks/session-intervention.md`;
  `netclaw-operations` system skill updated.
- Dependencies: adds `Microsoft.AgentGovernance` 5.0.0 (pure managed,
  in-proc; no sidecar, no policy files).
