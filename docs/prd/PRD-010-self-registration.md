# PRD-010: Self-Registration to an External Control Panel

## Status

- State: Draft for execution (new)
- Owner: Netclaw engineering
- Date: 2026-09-03
- Depends on: `PRD-001`, `PRD-002`, `PRD-004`

## Goal

Enable a Netclaw daemon to announce itself to an external control panel. The
operator sets one link in the configuration. The daemon sends its runtime
status to that link on startup. The daemon repeats the send on a heartbeat
interval. A fleet panel can then list daemons and show their state.

## Product Outcomes

1. Operator sets one link to register the daemon with a control panel.
2. The control panel receives the full runtime status of the daemon.
3. The control panel can tell daemons apart across restarts.
4. The operator can see the registration state in `netclaw doctor`.
5. A daemon without a configured link makes no outbound registration calls.

## Registration Architecture

### Configuration

A new `Registration` section in `netclaw.json`:

```json
{
  "Registration": {
    "BaseUrl": "https://panel.example.com",
    "Enabled": true,
    "HeartbeatIntervalSeconds": 300
  }
}
```

- `BaseUrl`: the control panel base URL. An absolute HTTP or HTTPS URI. A
  reverse-proxy path prefix is allowed. When the value is null or empty,
  the feature is off.
- `Enabled`: temporary switch. The operator can pause registration without
  deletion of the link. Default: `true`.
- `HeartbeatIntervalSeconds`: interval between sends. Default: `300`.
  Minimum: `30`.

The same values can arrive from the environment with the standard
`NETCLAW_` prefix (for example `NETCLAW_Registration__BaseUrl` in a Docker
Compose file). Environment values override the file. Validation treats
both sources the same.

### Derived Endpoints

The daemon derives two endpoints from `BaseUrl`:

- `POST {base}/api/v1/agent/status` — registration and status
  changes.
- `POST {base}/api/v1/agent/heartbeat` — liveness.

This path pair is the contract between the daemon and the panel.

### Payloads

The status endpoint receives one JSON envelope:

```json
{
  "instanceId": "build-server-01",
  "registeredAtUtc": "2026-09-03T10:00:00Z",
  "status": { "overall": "healthy", "...": "same body as /api/health/status" }
}
```

The heartbeat endpoint receives a minimal payload:

```json
{
  "instanceId": "build-server-01",
  "timestamp": "2026-09-03T10:05:00Z"
}
```

- `instanceId`: a stable host-based ID. The ID does not change across daemon
  restarts.

### Delivery Model

- The daemon sends one envelope to the status endpoint on startup.
- A timer ticks at the heartbeat interval. When the health projection
  changed (overall, connector status, build version, or model-degraded
  transitions), the daemon sends the envelope to the status endpoint.
  Otherwise it sends the heartbeat payload to the heartbeat endpoint.
- Volatile fields (pid, uptime, counters) never count as a change.
- The HTTP timeout is 10 seconds.
- A failed send logs a warning and updates the state file. The daemon keeps
  running.
- The daemon sends no secrets. The link alone authorizes the daemon.

### Registration State

The daemon writes a small state file under its data directory
(`registration-state.json`):

```json
{
  "lastRegisteredUtc": "2026-09-03T10:00:00Z",
  "state": "registered",
  "errorDetail": null
}
```

`errorDetail` is sanitized. It never contains the full configured URL.

### Surfacing

- `netclaw doctor` reports the registration state (see REG-006).
- Log warnings report each failed send.
- The `/api/health/status` payload does not change.

## Requirements

### REG-001 Registration Configuration

The system SHALL read the `Registration` section from `netclaw.json`. An
unset or empty `BaseUrl` disables registration. The section must appear in
the configuration schema with `additionalProperties: false`.

### REG-002 Payload Contract

The daemon SHALL POST the envelope `{instanceId, registeredAtUtc, status}`
to `{base}/api/v1/agent/status`. The `status` field SHALL equal
the body of `GET /api/health/status`. The `instanceId` SHALL be stable
across daemon restarts.

### REG-003 Heartbeat and Change Detection

The daemon SHALL send the minimal payload `{instanceId, timestamp}` to
`{base}/api/v1/agent/heartbeat` at the configured interval when
the health projection is unchanged. When the health projection changed
(overall, connector status, build version, or model-degraded transitions),
the daemon SHALL send the envelope to the status endpoint instead. The
default interval is 300 seconds. The minimum is 30 seconds.

### REG-004 Validation

The system SHALL reject an invalid `BaseUrl` before persistence in the
config editor. The daemon SHALL fail startup loudly when the stored
`BaseUrl` is invalid. A disabled or unset registration is a valid state
and is not an error.

### REG-005 Failure Isolation

A failed registration send SHALL NOT crash or block the daemon. Each failure
SHALL log a warning and update the state file. Network failure is a normal
runtime condition.

### REG-006 Doctor Surfacing

`netclaw doctor` SHALL report: Error for an invalid configured `BaseUrl`,
Warning for a state file that shows the last send failed, and Pass when
registration is not configured.

### REG-007 Secure by Default

The daemon SHALL make no outbound registration calls when `BaseUrl` is
unset or `Enabled` is false. The registration HTTP client SHALL identify
itself with the standard Netclaw headers.

## Non-Goals (MVP)

- Authentication with the control panel (tokens, mTLS).
- Commands or acknowledgements from the control panel to the daemon.
- Multiple registration targets.
- Registration state inside the `/api/health/status` payload.
- Retry queues beyond the next heartbeat.

## Acceptance Criteria

1. Operator can set the link through the config editor and through direct
   file edit.
2. Daemon POSTs the envelope on startup and on each heartbeat tick.
3. Control panel can dedupe daemons by `instanceId` across restarts.
4. Invalid URL is rejected by the config editor before the file changes.
5. Invalid stored URL fails daemon startup loudly.
6. Failed sends do not stop the daemon.
7. `netclaw doctor` shows Error, Warning, and Pass states as specified.
8. Unset link produces no outbound calls and no errors.

## Cross-References

- MVP scope: PRD-001
- Security posture: PRD-002
- Config, doctor, and TUI surfaces: PRD-004
- Status payload source: `DaemonRuntimeStatus` (engineering glossary and
  `docs/spec/`)
