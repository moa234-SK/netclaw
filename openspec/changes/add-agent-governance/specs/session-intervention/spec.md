## Purpose

Operator commands that stop, suspend, quarantine, and resume one
session, with a durable gate that keeps a stopped or gated session
dead until an operator resumes it.

## ADDED Requirements

### Requirement: Intervention commands

The system SHALL accept four session intervention commands: `stop`,
`suspend`, `quarantine`, and `resume`. Each command SHALL carry a
reason and the initiator identity. `stop` SHALL abort any in-flight
turn and terminate the session actor. `suspend` and `quarantine` SHALL
abort any in-flight turn and keep the session actor resident but
gated. `resume` SHALL clear the gate. `quarantine` SHALL additionally
deny every tool call while gated.

#### Scenario: Stop aborts an in-flight turn

- **WHEN** an operator stops a session that is mid-LLM-call
- **THEN** the turn is cancelled, the session actor stops, and the
  session stays dead until an operator resumes it

#### Scenario: Quarantine denies tools

- **WHEN** a quarantined session attempts any tool call
- **THEN** the tool call is denied and the session learns the denial
  reason in-band

#### Scenario: Resume reopens a stopped session

- **WHEN** an operator resumes a stopped session and a user then sends
  a message to it
- **THEN** the session materializes and processes the message normally

### Requirement: Durable gate survives resurrection

The system SHALL persist the intervention state per session in
SQLite before the actor is commanded. While the state is `Stopped`,
`Suspended`, or `Quarantined`, the system SHALL reject new user input
(including reminder re-entry and channel re-join) and SHALL NOT
materialize the session for processing.

#### Scenario: Reminder cannot resurrect a stopped session

- **WHEN** a reminder fires for a stopped session
- **THEN** the reminder turn is rejected and the session stays dead

#### Scenario: Cold recovery respects the gate

- **WHEN** a gated session actor is recreated and recovers its journal
- **THEN** a `Stopped` session stops itself without accepting input,
  and a `Suspended` or `Quarantined` session becomes idle but rejects
  input

#### Scenario: Daemon restart cannot resurrect a stopped session

- **WHEN** the daemon restarts and a message arrives for a session
  with durable state `Stopped`
- **THEN** the session is created only to be rejected by the gate, and
  no turn runs

### Requirement: Operator-only authority

The intervention endpoints SHALL require an authenticated Operator
principal. Unauthenticated or non-Operator callers SHALL receive an
authorization error and SHALL NOT change any session state.

#### Scenario: Unauthenticated stop is refused

- **WHEN** an unauthenticated caller posts a stop request
- **THEN** the response is an authorization error and the session state
  is unchanged

#### Scenario: Operator stop is accepted

- **WHEN** an Operator-class caller posts a stop request with a reason
- **THEN** the response reports the new session state and the ledger
  records the initiator

### Requirement: Catch-all session routes

The intervention URLs SHALL be
`POST /api/sessions/<sessionId>/stop`, `.../suspend`, `.../quarantine`,
and `.../resume`, where `<sessionId>` is the session id embedded in the
path (session ids contain the path separator `/`). The implementation
SHALL accept both raw and percent-encoded separators in the id segment,
and SHALL return the intervention history at
`GET /api/sessions/<sessionId>/interventions`.

#### Scenario: Encoded session id round-trips

- **WHEN** a caller posts to
  `/api/sessions/webhook%2Fname%2Fkey/stop`
- **THEN** the intervention applies to the session whose id is
  `webhook/name/key`

#### Scenario: Invalid transition returns a conflict

- **WHEN** an operator posts `suspend` for a stopped session
- **THEN** the response reports a conflict and the ledger is unchanged

### Requirement: Intervention ledger

The system SHALL record every accepted intervention in an append-only
SQLite ledger with the session id, the command, the reason, the
initiator, and a timestamp. The ledger SHALL be queryable per session
for incident review.

#### Scenario: Ledger lists intervention history

- **WHEN** a session was suspended and then resumed
- **THEN** the ledger returns both events in order with reasons and
  initiators
