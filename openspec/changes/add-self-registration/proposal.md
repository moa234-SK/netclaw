# Proposal: add-self-registration

## Why

A Netclaw daemon that runs in a fleet has no way to announce itself to an
external control panel. The operator must add each daemon to the panel by
hand. PRD-010 asks for one configured link. The daemon then registers itself
and repeats a heartbeat, so a panel can list daemons and show their state.

## What Changes

- New `Registration` section in `netclaw.json`: `Url`, `Enabled`,
  `HeartbeatIntervalSeconds`.
- New class library `Netclaw.Registration` with the config type, the
  registration service, the payload envelope, and the persisted state record.
- New hosted service `SelfRegistrationService`. It POSTs the status envelope
  on startup and on each heartbeat tick.
- New persisted state file `registration-state.json` under the daemon data
  directory.
- New `RegistrationDoctorCheck` in `netclaw doctor`.
- New Registration section editor in the config TUI, with URL validation
  before persistence.
- New schema section in `netclaw-config.v1.schema.json`.
- Thin daemon wiring: one project reference, one adapter, and service
  registration in `Program.cs`.

In scope for MVP:

- Single registration target, no authentication.
- Startup send plus heartbeat sends.
- Doctor and log surfacing.

Out of scope for MVP:

- Authentication with the control panel.
- Commands or acknowledgements from the control panel.
- Multiple registration targets.
- Registration state inside `/api/health/status`.
- Retry queues beyond the next heartbeat.

## Capabilities

### New Capabilities

- `daemon-self-registration`: outbound self-registration of the daemon to an
  operator-configured control panel endpoint. Covers configuration, payload
  envelope, heartbeat, failure isolation, persisted state, and doctor
  surfacing.

### Modified Capabilities

None. The change adds one capability. Existing specs keep their requirements.

## Impact

- **Code**: new `src/Netclaw.Registration` and `src/Netclaw.Registration.Tests`.
  `Netclaw.Daemon` gains a project reference and about four wiring lines plus
  one adapter. `Netclaw.Cli` gains the doctor check file and the TUI editor.
  `Netclaw.Configuration` gains only schema JSON.
- **APIs**: no public API changes. The `/api/health/status` payload does not
  change.
- **Dependencies**: no new package dependencies.
- **Security**: default deny holds. An unset `Url` produces no outbound calls.
  The payload contains no secrets. The link alone authorizes the daemon. The
  state file stores a sanitized error string only.
- **Operations**: `netclaw doctor` reports registration state. Log warnings
  report failed sends. Config hot reload applies through the standard restart
  path.

Source PRD: PRD-010 (`docs/prd/PRD-010-self-registration.md`).
