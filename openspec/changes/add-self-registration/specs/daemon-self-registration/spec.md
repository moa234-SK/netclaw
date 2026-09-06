## Purpose

Lets a Netclaw daemon announce itself to an operator-configured external
control panel. The daemon sends its runtime status on startup and repeats a
heartbeat so a panel can list daemons and show their state.

## ADDED Requirements

### Requirement: Registration configuration

The system SHALL read a `Registration` section from `netclaw.json` with the
properties `BaseUrl`, `Enabled`, and `HeartbeatIntervalSeconds`. The section
SHALL be declared in the configuration schema with `additionalProperties:
false`. An unset or empty `BaseUrl` SHALL disable registration.

#### Scenario: Section is missing

- **WHEN** `netclaw.json` has no `Registration` section
- **THEN** registration is disabled and the daemon starts normally

#### Scenario: Unknown property in section

- **WHEN** `netclaw.json` sets a property that the schema does not declare
  inside `Registration`
- **THEN** schema validation reports the unknown property

#### Scenario: Base URL arrives from the environment

- **WHEN** the `NETCLAW_Registration__BaseUrl` environment variable is set
  and `netclaw.json` has no `Registration` section
- **THEN** the daemon registers with the environment value as if it came from
  the file
- **AND** an invalid environment value fails startup loudly, the same as a
  file value

### Requirement: Registration endpoint derivation

The daemon SHALL derive both endpoints from `BaseUrl`:
`{base}/api/v1/agent/status` and
`{base}/api/v1/agent/heartbeat`. The derivation SHALL treat bases
with and without a trailing slash, and bases with a reverse-proxy path
prefix, as equivalent.

#### Scenario: Trailing slash is normalized

- **WHEN** `BaseUrl` is `https://panel.example.com/`
- **THEN** the status endpoint is
  `https://panel.example.com/api/v1/agent/status`

#### Scenario: Reverse-proxy prefix is preserved

- **WHEN** `BaseUrl` is `https://corp.example.com/netclaw`
- **THEN** the heartbeat endpoint is
  `https://corp.example.com/netclaw/api/v1/agent/heartbeat`

### Requirement: Registration payload contract

The daemon SHALL POST one JSON envelope to the status endpoint. The
envelope SHALL contain `instanceId`, `registeredAtUtc`, and `status`. The
`status` field SHALL equal the JSON body of `GET /api/health/status`. The
`instanceId` SHALL be stable across daemon restarts.

#### Scenario: Panel dedupes daemons

- **WHEN** the daemon restarts and sends a new envelope
- **THEN** the `instanceId` matches the value sent before the restart

#### Scenario: Status body is complete

- **WHEN** the daemon sends an envelope
- **THEN** the `status` field carries the same JSON body as
  `GET /api/health/status` returns at that time

### Requirement: Registration heartbeat and change detection

The daemon SHALL send the envelope to the status endpoint once at startup.
On each heartbeat tick the daemon SHALL compare the health projection of
the current status (overall, connector statuses, build version, and
model-degraded flags) with the projection of the last status send. When the
projection changed, the daemon SHALL send the envelope to the status
endpoint. Otherwise the daemon SHALL send the minimal payload
`{instanceId, timestamp}` to the heartbeat endpoint. Volatile fields (pid,
uptime, counters) SHALL NOT count as a change. The default interval SHALL
be 300 seconds. The minimum interval SHALL be 30 seconds.

#### Scenario: Startup send

- **WHEN** the daemon starts with a valid configured base URL
- **THEN** the daemon sends one envelope to the status endpoint after
  startup

#### Scenario: Health transition sends the envelope

- **WHEN** a heartbeat tick observes an overall or connector status change
  since the last status send
- **THEN** the daemon sends the full envelope to the status endpoint

#### Scenario: Unchanged health sends liveness only

- **WHEN** a heartbeat tick observes no projection change
- **THEN** the daemon sends only `{instanceId, timestamp}` to the heartbeat
  endpoint

#### Scenario: Interval below minimum

- **WHEN** the configured interval is less than 30 seconds
- **THEN** the system rejects the value before persistence

### Requirement: Registration base URL validation

The system SHALL reject an invalid `BaseUrl` before persistence in the
config editor. The daemon SHALL fail startup loudly when the stored
`BaseUrl` is invalid. A disabled or unset registration SHALL be a valid
state.

#### Scenario: Invalid URL in the editor

- **WHEN** the operator enters a non-absolute or malformed URL and saves
- **THEN** the editor blocks the save and `netclaw.json` is unchanged

#### Scenario: Invalid URL at startup

- **WHEN** the daemon starts with a stored invalid `BaseUrl`
- **THEN** startup fails with an error that names the invalid value

### Requirement: Registration failure isolation

A failed registration send SHALL NOT crash or block the daemon. Each failure
SHALL log a warning and update the persisted registration state. Network
failure SHALL be treated as a normal runtime condition.

#### Scenario: Panel is unreachable

- **WHEN** a send fails because the panel is unreachable
- **THEN** the daemon keeps running and logs a warning

#### Scenario: Panel returns an error status

- **WHEN** the panel answers with a non-success HTTP status
- **THEN** the daemon keeps running and records the failure in the state
  file

### Requirement: Persisted registration state

The daemon SHALL persist a registration state record under its data
directory. The record SHALL contain `lastRegisteredUtc`, `state`, and
`errorDetail`. The `errorDetail` value SHALL be sanitized and SHALL NOT
contain the configured URL.

#### Scenario: Successful send updates state

- **WHEN** a send succeeds
- **THEN** the state record shows `state` `registered` and the send time in
  `lastRegisteredUtc`

#### Scenario: Failed send updates state

- **WHEN** a send fails
- **THEN** the state record shows the failure in `state` and a sanitized
  `errorDetail`

### Requirement: Registration doctor surfacing

`netclaw doctor` SHALL report the registration state as Error for an
invalid configured URL, Warning for a persisted state that shows the last
send failed, and Pass when registration is not configured.

#### Scenario: Not configured

- **WHEN** no `Registration.BaseUrl` is set
- **THEN** the doctor check reports Pass

#### Scenario: Last send failed

- **WHEN** the persisted state shows a failed send
- **THEN** the doctor check reports Warning with a remediation hint

### Requirement: Secure default for outbound registration

The daemon SHALL make no outbound registration calls when `BaseUrl` is unset or
`Enabled` is false. The registration HTTP client SHALL identify itself with
the standard Netclaw headers.

#### Scenario: Feature is disabled

- **WHEN** `Enabled` is false and `BaseUrl` is set
- **THEN** the daemon sends no registration traffic

#### Scenario: Client identification

- **WHEN** the daemon sends an envelope
- **THEN** the request carries the standard Netclaw user-agent and purpose
  headers
