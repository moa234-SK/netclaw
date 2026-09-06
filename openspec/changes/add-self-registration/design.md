## Context

The daemon already produces a complete runtime status document
(`DaemonRuntimeStatus.Response`, served at `GET /api/health/status`). The
daemon has no outbound channel that delivers that document to an external
system on a schedule. Webhook notifications carry small alerts only, carry
no stable identity, and track no delivery state. See `proposal.md` for the
motivation. See `specs/daemon-self-registration/spec.md` for the behavior
contract.

Constraints that shape this design:

- `DaemonRuntimeStatusService` lives inside the `Netclaw.Daemon`
  executable, so a separate library cannot reach it directly.
- `netclaw doctor` runs in the CLI process, so it cannot read in-memory
  daemon state.
- Repo rules require the config schema update in the same change and
  reject-before-persistence validation for config editors.
- The product direction keeps the MVP single-process. No sidecar process.

## Goals / Non-Goals

**Goals:**

- Keep existing-project edits to schema JSON and thin wiring.
- One producer and one consumer for the state record shape.
- No new package dependencies.

**Non-Goals:**

- Retry queues, panel-to-daemon commands, authentication, multiple targets
  (see proposal out-of-scope list).
- Changes to the `/api/health/status` payload.
- Actor-system involvement. Registration is not a session-scoped behavior.

## Decisions

### D1: New class library `Netclaw.Registration`

The library references `Netclaw.Configuration` only. It owns the config
type, the envelope, the state record, the service, and the DI extensions.

- Alternative: place code in `Netclaw.Daemon` and `Netclaw.Configuration`
  per the webhooks precedent. Rejected: the operator asked for minimal
  existing-project edits, and the repo already ships subsystems as separate
  projects (channels).

### D2: Status access through a provider seam

The library defines `IStatusSnapshotProvider`. It returns
`DaemonRuntimeStatus.Response`. The daemon wires one adapter that calls
`DaemonRuntimeStatusService`. The seam keeps the library free of a
reference to the executable.

- Alternative: the service calls the daemon HTTP API on loopback. Rejected:
  it needs the listen address and an auth token, and it adds a network hop
  to data the process already holds.

### D3: Envelope with a stable `instanceId`

The envelope is `{ instanceId, registeredAtUtc, status }`. The `instanceId`
is the machine name in lower case. When the machine name is empty, the
daemon generates a random ID once and persists it inside the state record.

- Alternative: POST the status body with no envelope. Rejected: the only
  process identity in the status body is the pid, which changes each
  restart. A panel cannot dedupe daemons.

### D4: Persisted state record, not in-memory state

`SelfRegistrationService` writes `registration-state.json` under the daemon
data directory after each send. The record is
`{ lastRegisteredUtc, state, errorDetail }`. The write is atomic
(write temp file, then move), following the `ConfigFileHelper` pattern.
The record type lives in `Netclaw.Registration`; the CLI doctor check
consumes the same type. A test proves the round trip.

- Alternative: static in-memory store like `UpdateCheckService`. Rejected:
  doctor runs in a separate process and could not read it.

### D5: Plain hosted service, no actors

`SelfRegistrationService` is a `BackgroundService`. It sends once at
startup and then on a `PeriodicTimer` driven by an injected `TimeProvider`.
This follows the `BinaryUpdateCheckService` pattern. Actor boundaries are
not crossed: registration reads a snapshot through the provider seam and
writes one file. Actor systems add supervision and messaging that this
single-writer loop does not need.

- Alternative: a registration actor. Rejected for MVP: no concurrency, no
  request/response, and the status source is a plain service.

### D6: Gating and fail-loud startup

The hosted service is registered only when `Enabled` is true and `Url` is
set. When `Url` is set but invalid, startup fails loudly from the same
wiring point, before the host starts sending. The TUI editor and the CLI
validate the same way through a shared validator in the library.

### D7: HTTP client

A typed client with a 10 second timeout, registered with
`AddNetclawHeaders("self-registration")`. One attempt per send. The next
heartbeat is the retry.

### D8: Hot reload

Config hot reload follows the existing restart path
(`ConfigWatcherService` plus `DaemonRestartCoordinator`). A changed
`Registration` section applies after the in-process restart. No special
handling in the library.

## Risks / Trade-offs

- [Panel URL exfiltrates status data to an attacker-controlled host] -->
  The operator configures the URL by hand in a local file, the same trust
  model as webhook targets. The payload carries no secrets and no tokens.
- [Hostname collision when two daemons share a machine name] --> The panel
  can compare source addresses. A unique-ID mode is a later option; MVP
  accepts the collision risk.
- [State file is corrupted or deleted] --> The service treats a missing or
  unreadable record as `unknown` and overwrites it on the next send. The
  daemon does not crash.
- [Panel outage produces warning noise every heartbeat] --> Warnings are
  rate-limited by the heartbeat interval itself. The interval floor of 30
  seconds bounds the noise.
- [Two sends overlap when the panel is slow] --> The send loop awaits each
  send before the next timer tick is observed. The 10 second timeout is
  below the 30 second interval floor, so overlap cannot occur.

## Migration Plan

Deploy: add the library, wire the daemon, ship the schema section. An
existing config without a `Registration` section keeps working; the
feature stays off. `netclaw doctor --fix` can insert the default section
for operators who want it, because the schema declares defaults.

Rollback: remove the section from `netclaw.json` or set `Enabled` to
false. The binary rollback works without data migration; the state file is
disposable.
