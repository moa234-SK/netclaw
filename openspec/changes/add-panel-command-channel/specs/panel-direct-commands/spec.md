## Purpose

The daemon's side of the direct-call contract for external control
panels: discovery, pairing-based authority, the intervention command
surface, and revocation.

## ADDED Requirements

### Requirement: Panel discovery of commandable agents

The panel SHALL use the registration stream to learn which agents are
commandable. An agent whose status envelope carries `apiBaseUrl` is
commandable at that URL. An agent whose envelope omits the field is
telemetry-only: the panel SHALL NOT attempt commands against it.

#### Scenario: Commandable agent appears in the roster

- **WHEN** a registered agent's envelope carries `apiBaseUrl`
- **THEN** the panel may issue commands to that URL with a paired
  device token

#### Scenario: Telemetry-only agent

- **WHEN** a registered agent's envelope omits `apiBaseUrl`
- **THEN** the panel reports the agent as not commandable and issues
  no commands to it

### Requirement: Panel authority requires device pairing

Every panel command SHALL authenticate with a device bearer token
issued by that agent's pairing exchange. Unauthenticated or
unrecognized callers SHALL receive no state changes. Pairing and token
issuance follow the existing device-pairing capability; this contract
adds no new credential.

#### Scenario: Paired panel commands an agent

- **WHEN** the panel holds a device token for an agent and posts an
  intervention command with it
- **THEN** the agent applies the intervention and returns the new
  session state

#### Scenario: Revoked token stops the panel

- **WHEN** the operator removes the panel's device entry from an agent
- **THEN** the panel's next command to that agent is rejected and no
  state changes

### Requirement: Command surface

The panel's v1 command surface SHALL be exactly: the four session
intervention commands, the session list, the per-session intervention
ledger, the agent freeze commands, the agent intervention ledger, and
the agent state in the runtime status. The panel SHALL send the
session intervention commands as
`POST {apiBaseUrl}/api/sessions/<sessionId>/stop|suspend|quarantine|resume`
with a JSON body `{ "reason": string }`, read the session list from
`GET {apiBaseUrl}/api/sessions`, read the session ledger from
`GET {apiBaseUrl}/api/sessions/<sessionId>/interventions`, send the
agent freeze commands as
`POST {apiBaseUrl}/api/agent/suspend|resume` with the same body
shape, read the agent ledger from
`GET {apiBaseUrl}/api/agent/interventions`, and read the agent state
from `governance.agentState` in the status envelope. Terminating the
agent process SHALL stay outside the panel surface.

#### Scenario: Stop command

- **WHEN** the panel posts `/api/sessions/slack%2FC1%2FT2/stop` with
  reason "incident 42"
- **THEN** the response reports the session state `stopped` and the
  ledger records the panel's device identity

#### Scenario: Agent freeze command

- **WHEN** the panel posts `/api/agent/suspend` with reason
  "incident 42"
- **THEN** the response reports the agent state `suspended`, new
  session ingress is rejected, and the next status envelope reports
  `agentState: suspended`

#### Scenario: Frozen agent cannot be terminated remotely

- **WHEN** the panel wants a suspended agent's process gone
- **THEN** the panel must escalate to the host operator; no command
  surface terminates the process

#### Scenario: Conflict is surfaced to the panel

- **WHEN** the panel posts `suspend` for a session already `stopped`
- **THEN** the response is a conflict carrying the reason, and the
  panel must resume the session first

#### Scenario: Idempotent repeat

- **WHEN** the panel repeats a stop for a session already `stopped`
- **THEN** the response reports `already-in-state` and the ledger
  gains no new event

### Requirement: Panel-initiated commands are auditable

Every panel command accepted by an agent SHALL appear in that agent's
intervention ledger with the device identity of the caller, the
command, the reason, and a timestamp. The panel cannot erase or edit
ledger entries.

#### Scenario: Ledger answers who stopped a session

- **WHEN** the operator reviews a stopped session's ledger
- **THEN** the entries name the panel's device identity for every
  panel-initiated command
