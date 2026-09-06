# Control Panel Integration (Direct Commands)

How an external control panel discovers Netclaw agents, authenticates,
and issues intervention commands (session and agent level) over a
shared network.

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
  "apiBaseUrl": "https://agent-03.corp.local:5000",
  "pairingCode": "A3KD-7W2M"
}
```

- `instanceId` is stable across agent restarts — dedupe the roster by
  it.
- `apiBaseUrl` is present **only** when the agent's operator set
  `Registration.ApiBaseUrl`. Present = commandable at that URL.
  Absent = telemetry-only; do not send commands to that agent.
- `pairingCode` is present **only** on the agent's startup send, and only
  when the operator set `Registration.AutoPair` (which requires
  `ApiBaseUrl`). See § 2.
- Persist the last envelope per instanceId. Heartbeats carry no URL.

## 2. Authority (one-time per agent)

Commands require a device bearer token issued by the agent. Two ways to
obtain it:

### 2a. Automatic (auto-pairing envelope)

When the operator sets `Registration.AutoPair`, each agent startup
envelope carries a fresh single-use `pairingCode` (5-minute TTL):

1. On envelope arrival, immediately exchange it:
   `POST {apiBaseUrl}/api/pair/exchange` with
   `{ "code": "...", "deviceName": "panel" }`.
   The response carries the bearer token.
2. Store `{ instanceId → (apiBaseUrl, token) }`.
3. Never log the `pairingCode` value.

Panel-side rules (MUST):

- Bind `instanceId → apiBaseUrl` at first sight of the instanceId. When a
  later envelope carries a **different** `apiBaseUrl` for a known
  instanceId, do not auto-exchange and do not overwrite the binding.
  Escalate to the operator instead. The registration stream is
  unauthenticated, so this rule blocks a spoofed envelope from
  redirecting command traffic to an attacker host.
- Treat an exchange answer of `409` (device name `panel` already exists)
  as "already paired". Keep the existing token.
- Ignore `pairingCode` when you already hold a live token for the
  instanceId (test liveness with a roster or status call, not with
  another exchange).

Positive example: panel sees instanceId `agent-03` for the first time
with `apiBaseUrl` `https://agent-03.corp.local:5000` and `pairingCode`.
The panel exchanges the code, stores the token, and commands the agent.

Negative example: panel knows `agent-03` at
`https://agent-03.corp.local:5000`. An envelope arrives for `agent-03`
with `apiBaseUrl` `https://evil.example.net` and a `pairingCode`. The
panel keeps the old binding, does not exchange, and raises an alert.

### 2b. Manual (operator-minted code)

1. On the agent host, generate a pairing code (time-limited):
   `netclaw daemon pair`.
2. Exchange it from the panel:
   `POST {apiBaseUrl}/api/pair/exchange` with `{ "code": "...", "deviceName": "panel" }`.
   The response carries the bearer token.
3. Store `{ instanceId → (apiBaseUrl, token) }` in the panel.

If the panel was down at agent startup and missed the auto-pairing code,
use this path as the fallback.

Recommendation: use the device name `panel` on every agent so one
revocation sweep (`DELETE {apiBaseUrl}/api/pair/devices/panel`) cuts
the panel's authority fleet-wide.

Tokens are per-agent. A token from agent A is rejected by agent B.

## 3. Commands (v1: session interventions + agent freeze)

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
| `POST {apiBaseUrl}/api/agent/suspend` | Freeze the whole agent: all new session ingress rejected, in-flight turns finish, process stays up |
| `POST {apiBaseUrl}/api/agent/resume` | Thaw the agent |
| `GET {apiBaseUrl}/api/agent/interventions` | Audit ledger for agent-level commands |

Body for the commands: `{ "reason": "..." }`.

Agent state visibility: the status envelope carries
`status.governance.agentState` (`active` / `suspended`), and a
suspend or resume triggers an immediate full status send — no need to
poll for the transition.

Agent freeze semantics: the state is durable (a service-manager
restart boots the agent back frozen), and terminating the agent
process is **not** in the command surface — panels escalate to the
host operator for that.

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
    "ApiBaseUrl": "https://agent-03.corp.local:5000",
    "AutoPair": true
  }
}
```

`ApiBaseUrl` is deliberate consent: the agent never derives it from
its bind address, and removing it returns the agent to telemetry-only
on the next status send. `AutoPair` (default `false`) is a second layer
of consent: with it on, every agent startup mints and ships one
single-use pairing code in the envelope. The daemon's HTTP API must be
reachable at that URL (see the daemon exposure documentation for
`ExposureMode`).

## 5. Failure modes

| Symptom | Meaning |
|---|---|
| Envelope present, commands 401 | Pairing missing or token revoked — redo § 2 |
| `pairingCode` present, exchange 409 | Already paired — keep the existing token |
| `pairingCode` present, exchange 401 | Code expired (5-minute TTL) — use § 2b manual pairing |
| Heartbeats arrive, no `apiBaseUrl` | Operator did not advertise the URL — agent is read-only |
| Commands time out | Network path or exposure misconfigured on the agent |
| 409 on every command | Session is in a gated state — resume first |
| Heartbeats keep flowing, all channels dark | The agent is suspended — check `agentState`, `POST /api/agent/resume` |

Operator runbook: `docs/runbooks/session-intervention.md`.
