# Session Intervention Runbook

How to stop, suspend, quarantine, and resume one Netclaw session — and
how to freeze the agent as a whole.

External control panels use the same endpoints directly — see
`docs/integrations/control-panel.md` for the panel-side contract
(discovery, device pairing, command URLs).

## What each command does

| Command | In-flight turn | Session actor | Tools | Until |
|---------|----------------|---------------|-------|-------|
| `stop` | Aborted | Terminated | n/a | `resume` |
| `suspend` | Aborted | Stays resident, input rejected | n/a | `resume` |
| `quarantine` | Aborted | Stays resident, input rejected | All denied | `resume` |
| `resume` | — | Gate cleared | Restored | — |

One command affects one session. The daemon process, all other sessions,
and all channel bindings keep running. Sub-agents die with their session
when it stops.

## Commands

All calls require an Operator-class ticket (loopback or a paired device
token). Session ids contain `/`; both raw and `%2F`-encoded forms work.

```bash
# Stop a session that misbehaves (abort turn, kill actor, gate it)
curl -X POST http://127.0.0.1:5000/api/sessions/slack/C123/T456/stop \
  -H "Authorization: Bearer $DEVICE_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason":"ran rm -rf in wrong directory"}'

# Suspend (abort turn, keep actor warm, reject input)
curl -X POST http://127.0.0.1:5000/api/sessions/slack/C123/T456/suspend \
  -H "Authorization: Bearer $DEVICE_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason":"investigating odd output"}'

# Quarantine (suspend + every tool call denied — inspection state)
curl -X POST http://127.0.0.1:5000/api/sessions/slack/C123/T456/quarantine \
  -H "Authorization: Bearer $DEVICE_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason":"suspected prompt injection"}'

# Resume
curl -X POST http://127.0.0.1:5000/api/sessions/slack/C123/T456/resume \
  -H "Authorization: Bearer $DEVICE_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason":"false alarm"}'

# Audit history for one session (append-only ledger)
curl http://127.0.0.1:5000/api/sessions/slack/C123/T456/interventions \
  -H "Authorization: Bearer $DEVICE_TOKEN"
```

Responses: `200` with the new state, `409` when the transition is invalid
(for example suspending a stopped session — resume it first), `404` for an
unknown command segment, `400` for a malformed path.

## Semantics operators must know

- **The gate is durable.** State lives in `netclaw.db`
  (`session_interventions`), written before the actor is commanded. A
  daemon restart does not clear it.
- **Stopped sessions stay dead.** User messages, reminder re-entry, and
  channel re-join are all rejected while the gate says `stopped`. Only
  `resume` reopens the session.
- **Same-state commands are idempotent.** Stopping a stopped session
  returns `200` with `"status":"already-in-state"` and writes no new
  ledger event.
- **The ledger is the audit trail.** Every accepted intervention appends
  one row to `session_intervention_events` with the command, reason,
  initiator (device id or `local`), and timestamp. Query it per session
  via the interventions endpoint.
- **The AGT kill switch is in-process classification only.** It records
  kill events and lifecycle transitions for the running process; the
  SQLite ledger answers who killed what and when across restarts.

## Agent freeze (whole-agent suspend)

`POST /api/agent/suspend` freezes the agent: every channel, session
creation, and reminder delivery rejects new input with the
agent-suspended reason. In-flight turns finish and deliver. The
process, HTTP API, and heartbeat keep running — the status envelope
reports `agentState: suspended` so a control panel sees it.

```bash
# Freeze the agent (soft: in-flight turns finish)
curl -X POST http://127.0.0.1:5000/api/agent/suspend \
  -H "Authorization: Bearer $DEVICE_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason":"incident 42 — freeze fleet node"}'

# Thaw
curl -X POST http://127.0.0.1:5000/api/agent/resume \
  -H "Authorization: Bearer $DEVICE_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason":"incident closed"}'

# Agent-level audit history
curl http://127.0.0.1:5000/api/agent/interventions \
  -H "Authorization: Bearer $DEVICE_TOKEN"
```

Operators must know:

- **The freeze is durable.** State lives in `netclaw.db`
  (`agent_intervention`). The systemd unit ships `Restart=always`, but
  a suspended agent boots back frozen — the daemon re-closes the gate
  before it accepts traffic.
- **Shutdown still drains.** Stopping the daemon while frozen still
  drains active sessions gracefully; the freeze does not skip that.
- **Config hot-reload restarts are deferred** while frozen; apply
  them after `resume`.
- **Webhook-triggered command work is outside the freeze** (v1); its
  paths do not consult the session ingress gate.
- **Terminating the process stays host-level.** No endpoint kills the
  daemon — use `netclaw daemon stop` on the host or the service
  manager. This is deliberate: no token can darken a fleet.

## Incident recipe

1. `GET /api/sessions` — find the session id.
2. `quarantine` it (turn aborts, tools denied, transcript intact).
3. Inspect the session log (path in the catalog entry) and the
   interventions ledger.
4. Decide: `resume`, or `stop` when the session should not run again.
5. Record the outcome in the incident ticket; the ledger already holds
   the who/what/when.
