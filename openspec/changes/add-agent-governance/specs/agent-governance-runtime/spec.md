## Purpose

An always-on, in-process governance runtime that gives Netclaw a
session-scoped kill switch, lifecycle state tracking, and a
hash-chained audit trail for governance decisions.

## ADDED Requirements

### Requirement: Governance runtime is always on

The daemon SHALL construct the governance runtime at startup with the
kill switch armed. The runtime SHALL require no configuration and no
external process. A failure to construct the runtime SHALL fail daemon
startup.

#### Scenario: Daemon starts with no governance config

- **WHEN** the daemon starts with no governance-specific configuration
- **THEN** the kill switch is armed and lifecycle tracking is available

#### Scenario: Runtime construction fails

- **WHEN** the governance runtime cannot be constructed
- **THEN** the daemon refuses to start

### Requirement: Session identity binds to the governance agent id

The runtime SHALL use the session id as the governance agent id for
kill events, lifecycle transitions, and audit entries.

#### Scenario: Kill event carries the session id

- **WHEN** a session is stopped
- **THEN** the kill event agent id equals the stopped session id

### Requirement: Governance decisions are audited

Every accepted intervention SHALL produce one entry in the in-process
hash-chained audit log with the session id, the action, and the
decision. The durable record of the intervention SHALL live in the
SQLite intervention ledger, which also records the initiator.

#### Scenario: Audit entry follows a stop

- **WHEN** an operator stops a session
- **THEN** the audit log contains one entry for that session id with
  the stop action and an accept decision

#### Scenario: Initiator is recorded durably

- **WHEN** an operator stops a session
- **THEN** the SQLite intervention ledger records who issued the stop

### Requirement: Rejected transitions are not applied

The runtime SHALL validate every requested state transition before it
writes durable state. A transition between two gated states (for
example suspend on a stopped session) SHALL be rejected without a
durable write and without an actor command. A command that requests
the state the session already has SHALL be idempotent: it SHALL return
the current state and write no new ledger event.

#### Scenario: Suspend on a stopped session

- **WHEN** a suspend request arrives for a session in `Stopped`
- **THEN** the request is rejected as an invalid transition and no new
  ledger event is written

#### Scenario: Stop on an already stopped session

- **WHEN** a stop request arrives for a session already in `Stopped`
- **THEN** the request returns the current state and writes no new
  ledger event
