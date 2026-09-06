## Context

Netclaw sessions are persistent actors under a child-per-entity
`session-manager`. Sessions passivate when idle and are recreated by
any `IWithSessionId` message: user input, reminder re-entry, or
channel re-join. The only lifecycle controls today are per-session
passivation (automatic) and whole-daemon drain
(`PrepareForDaemonRestart`). There is no operator kill, no gate that
survives recreation, and no audit of who stopped what.

The Microsoft Agent Governance Toolkit (AGT) 5.0.0 supplies in-proc
primitives: `KillSwitch` (arm/kill/history), per-agent
`LifecycleManager` (validated state transitions), and a hash-chained
`AuditLogger`. All are in-memory and enforce nothing — the host owns
actuation and durability.

Operator constraints from the planning session:

- Minimal edits outside `Netclaw.Governance`.
- ACS/OPA stays the policy decision point; AGT adds runtime controls.
- Daemon shutdown (`/api/lifecycle/shutdown`) stays untouched.
- Session-granularity kills: one kill = one session, agentId = session
  id.

## Goals / Non-Goals

**Goals:**

- One capability: stop / suspend / quarantine / resume one session.
- Durable authority in SQLite; AGT provides classification and audit.
- Resurrection-proof gate.
- Thin daemon wiring; implementation lives in `Netclaw.Governance`.

**Non-Goals:**

- Daemon-level kill, restart, or shutdown changes.
- Panel-to-daemon command transport (later change).
- Input safety, rate limiting, observability slices (tasks listed,
  built later).
- Config surface (zero-config runtime).

## Decisions

### D1: Implementation home is `Netclaw.Governance`

The library already references `Netclaw.Actors` and hosts ACS
governance. Intervention logic (coordinator, store, AGT runtime, DI)
lives there. The actor-facing contract (message records, reader
interface) lives in `Netclaw.Actors` because `Governance` references
`Actors`, not the reverse — the same pattern as
`ISessionLifecycleObserver` (interface in Actors, implementation in
the daemon-side service).

- Alternative: put the store in `Netclaw.Daemon` next to
  `SessionCatalogService`. Rejected: operator asked for minimal
  changes outside the governance project.

### D2: `agentId` = session id; AGT is classification, SQLite is authority

`KillSwitch.Kill(sessionId, ManualOverride, detail)` classifies the
event; `LifecycleManager` validates transitions. Neither survives a
restart, so `SessionInterventionStore` in SQLite (`netclaw.db`,
`SessionCatalogService` conventions: open-per-operation connections,
idempotent `CREATE TABLE IF NOT EXISTS` DDL) is the durable authority.
The actor gate reads the store, never AGT.

- Consequence: no new migration file. The store self-creates schema,
  the exact `EnsureSchemaUpToDate` precedent.

### D3: Durable write first, actuation second

The coordinator validates the transition, writes the durable state,
then commands the actor, then records AGT events. A crash between
write and actuation leaves a gated session that cold recovery
respects (see D5) — the safe direction. The reverse order could leave
a killed actor with no gate, which resurrection would undo.

### D4: `KillEvent` has no initiator field

AGT `LifecycleEvent` carries `InitiatedBy`; `KillEvent` does not. The
initiator travels in `KillEvent.Detail` (for the in-proc history) and
in the ledger's `initiated_by` column (durable). The ledger answers
"which human killed this session".

### D5: Gate checks at three points

1. Actor command handlers: a gated session NACKs `SendUserMessage`
   (all phases) with the intervention reason — mirrors the
   `_restartDrainRequested` NACK pattern.
2. Cold recovery: `RecoveryCompleted` reads the durable state. A
   `Stopped` session stops itself; `Suspended`/`Quarantined` sessions
   become Ready but gated.
3. Quarantine additionally denies tool dispatch at the tool-batch
   entry point.

The session-manager's get-or-create can still materialize an actor
for a gated session (any routed message does); the gate makes that
harmless — the actor refuses work and (for `Stopped`) stops itself.

### D6: Stop reuses the passivation machinery

`StopSession` sets an intervention-stop flag, stores the reply-to,
cancels the in-flight CTSes, and routes into the existing
passivation path (snapshot, grace window, final stop). The ack is
sent in `FinalizePassivation` next to the drain ack. Suspend and
quarantine cancel the CTSes (the fail-turn path lands the actor back
in Ready) and stay resident.

- Alternative: `Context.Stop` immediately. Rejected: loses the final
  snapshot and skips in-flight cleanup.

### D7: One shared abort lever

The existing per-actor CTSes (`_activeLlmCts`,
`_activeToolExecutionCts`) are the abort mechanism for all three
commands. Cancellation flows through the existing failure handlers,
which produce a clean failed-turn transcript instead of a wedged one.

### D8: Endpoints, auth, routes

`MapGroup("/api/sessions").RequireAuthorization()` — parameterless
authorization equals Operator class today (both success paths —
loopback and device token — issue Operator claims). Catch-all route
`{**sessionId}` because session ids contain `/`; the route value is
URL-decoded. `initiatedBy` derives from the authenticated principal.
Body is `{ "reason": string }`. Responses: 200 with the new state,
409 for invalid transitions, 400 for a malformed id.

### D9: Zero config

The AGT runtime is always constructed and armed. It needs no files or
endpoints, so an `Enabled` toggle would be a silent-bypass risk, not
a feature. ACS keeps its existing toggle because it drives external
OPA resolution.

### D10: Resurrection semantics for `resume`

`resume` clears the durable gate and commands a resident actor if
one exists. If the actor is gone (stopped), the coordinator's ask
materializes it; recovery sees `Active` and the actor idles until the
next real message. The warm-up cost is one journal replay — accepted
for simplicity.

## Risks / Trade-offs

- [Gate check adds one SQLite read per user message] --> The read
  happens only at cold recovery and on intervention command receipt;
  steady-state ingress uses the actor's in-memory cached state, which
  recovery seeded durably.
- [Ask materializes actors for dead sessions] --> The gate makes the
  materialization harmless (refuses work, self-stops for `Stopped`).
- [AGT in-memory history lost on restart] --> The SQLite ledger is
  the authority; AGT history is advisory classification only.
- [Buffered messages dropped on stop] --> Mirrors the accepted
  restart-drain behavior; callers get a NACK, not silence.

## Migration Plan

Deploy is additive: new tables self-create on first use, no config
changes, no schema file changes, no endpoint removals. Rollback is a
binary rollback; the tables are inert without the endpoints.

## Security Considerations

- Positive: an Operator stop of session `slack/C123/T456` writes
  `Stopped` durably, kills the actor, and every later message to that
  session NACKs until an Operator resumes it.
- Negative: an unauthenticated POST to `/api/sessions/x/stop`
  receives 401/403 and writes nothing — the endpoint group requires
  authorization before any handler runs.
- Negative: a suspend request for a stopped session returns 409
  without a ledger write — transitions between gated states are
  structurally invalid, not best-effort.
