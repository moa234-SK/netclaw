# Control Panel Integration (Direct Commands)

How an external control panel discovers Netclaw agents, authenticates,
and issues session intervention commands over a shared network.

Assumptions: the panel can reach each agent's HTTP API directly
(same LAN, VPC, or tailnet). For NAT'd fleets, direct commands do not
work; an outbound command channel is a future capability.

## 1. Discovery (registration stream)

Each agent is configured with `Registration.BaseUrl` (this panel) and
sends:

| Endpoint (panel-side) | Payload | When |
|---|---|---|
| `POST /api/v1/agent/status` | `RegistrationEnvelope` | startup, and on health changes |
| `POST /api/v1/agent/heartbeat` | `{ instanceId, timestamp }` | every `HeartbeatIntervalSeconds` (default 300) |

Envelope:

```json
{
  "instanceId": "agent-03",
  "registeredAtUtc": "2026-09-06T09:15:00Z",
  "status": { "...": "equals GET /api/health/status body" },
  "apiBaseUrl": "https://agent-03.corp.local:5000"
}
```

- `instanceId` is stable across agent restarts — dedupe the roster by
  it.
- `apiBaseUrl` is present **only** when the agent's operator set
  `Registration.ApiBaseUrl`. Present = commandable at that URL.
  Absent = telemetry-only; do not send commands to that agent.
- Persist the last envelope per instanceId. Heartbeats carry no URL.

## 2. Authority (one-time per agent)

Commands require a device bearer token issued by the agent:

1. On the agent host, generate a pairing code (time-limited):
   `netclaw daemon pair`.
2. Exchange it from the panel:
   `POST {apiBaseUrl}/api/pair/exchange` with `{ "code": "...", "deviceName": "panel" }`.
   The response carries the bearer token.
3. Store `{ instanceId → (apiBaseUrl, token) }` in the panel.

Recommendation: use the device name `panel` on every agent so one
revocation sweep (`DELETE {apiBaseUrl}/api/pair/devices/panel`) cuts
the panel's authority fleet-wide.

Tokens are per-agent. A token from agent A is rejected by agent B.

## 3. Commands (v1: session interventions)

All calls send `Authorization: Bearer <token>`. Session ids contain
`/`; percent-encode them in the path (`slack%2FC123%2FT456`).

| Call | Effect |
|---|---|
| `POST {apiBaseUrl}/api/sessions/<id>/stop` | Abort the in-flight turn; the session stays dead until resume |
| `POST {apiBaseUrl}/api/sessions/<id>/suspend` | Abort the turn; session resident but rejects input |
| `POST {apiBaseUrl}/api/sessions/<id>/quarantine` | Suspend + every tool call denied (inspection state) |
| `POST {apiBaseUrl}/api/sessions/<id>/resume` | Clear the gate |
| `GET {apiBaseUrl}/api/sessions?limit=&offset=` | Roster of recent sessions |
| `GET {apiBaseUrl}/api/sessions/<id>/interventions` | Audit ledger for one session |

Body for the four commands: `{ "reason": "..." }`.

Responses:

- `200` — `{ sessionId, state, status }`; `status: "already-in-state"`
  means the command was idempotent (no new ledger event).
- `401` — token unknown or revoked; stop commanding this agent.
- `409` — invalid transition (for example suspend on a stopped
  session); resume the session first.
- `404` — unknown command segment. `400` — malformed path.

Every accepted command appends to the agent's durable intervention
ledger with the caller's device identity, command, reason, and
timestamp. Panels cannot erase ledger entries.

## 4. Agent-side configuration summary

```json
{
  "Registration": {
    "BaseUrl": "https://panel.corp.local",
    "ApiBaseUrl": "https://agent-03.corp.local:5000"
  }
}
```

`ApiBaseUrl` is deliberate consent: the agent never derives it from
its bind address, and removing it returns the agent to telemetry-only
on the next status send. The daemon's HTTP API must be reachable at
that URL (see the daemon exposure documentation for `ExposureMode`).

## 5. Failure modes

| Symptom | Meaning |
|---|---|
| Envelope present, commands 401 | Pairing missing or token revoked — redo step 2 |
| Heartbeats arrive, no `apiBaseUrl` | Operator did not advertise the URL — agent is read-only |
| Commands time out | Network path or exposure misconfigured on the agent |
| 409 on every command | Session is in a gated state — resume first |

Operator runbook: `docs/runbooks/session-intervention.md`.
