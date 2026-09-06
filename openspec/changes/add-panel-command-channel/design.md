## Context

The fleet runs on a shared network: the control panel can reach every
agent directly. The daemon already serves Operator-authenticated
intervention endpoints and a pairing exchange that issues device bearer
tokens. The registration stream (`RegistrationEnvelope` /
`HeartbeatPayload`) is the panel's only view of the fleet. The envelope
carries no address, so the panel cannot know where to call.

Decisions locked with the operator during planning:

- Transport: direct call (panel → daemon). No outbound command poll.
- v1 command scope: interventions only.
- Auth: per-agent device token via the existing pairing exchange.
- Advertisement: explicit-only `Registration.ApiBaseUrl`.

## Goals / Non-Goals

**Goals:**

- One optional config property, one envelope field, one validator
  rule, one schema entry.
- A written contract for panel integrators.
- Zero new endpoints, zero auth changes, zero inbound surface change.

**Non-Goals:**

- Panel-side code (external project).
- Policy-update or upgrade commands.
- Outbound command polling (a later fallback for NAT'd fleets).
- Auto-derivation of the command URL from bind address or exposure
  mode.

## Decisions

### D1: Explicit-only advertisement

`apiBaseUrl` is populated only from `Registration.ApiBaseUrl`. The
daemon never derives it. Advertising "here is where you can command
me" is an authority decision; coupling it silently to
`ExposureMode != Local` would let a remote-admin-UI exposure
implicitly grant the panel a command address nobody chose to give.
The cost is one config line per commandable agent.

### D2: Field shape and omission

The envelope property is nullable and carries
`[JsonIgnore(Condition = WhenWritingNull)]`, so an unset value omits
the field entirely (camelCase JSON: `apiBaseUrl`). The serializer
options are unchanged — no global null-handling change that could
alter the rest of the status body.

### D3: Validation follows the BaseUrl pattern

`RegistrationConfigValidator` requires an absolute HTTP or HTTPS URI
when the value is set; blank stays the valid off state. The rule runs
inside the shared `TryValidate`, so daemon startup, the config TUI,
and doctor reject the same values with the same message — one surface
cannot persist what another rejects.

### D4: Heartbeat unchanged

The heartbeat is minimal liveness. The panel persists envelopes; a URL
change reaches it on the next status send (health projection change or
startup). Adding the field to the heartbeat would double the contract
for no gain.

### D5: Contract lives in a doc, not code

`docs/integrations/control-panel.md` records the full recipe for panel
integrators (discovery → pairing → commands → errors → revocation).
The daemon side already satisfies the `panel-direct-commands` spec;
the spec deltas document the contract for verification.

## Risks / Trade-offs

- [Operator sets ApiBaseUrl but never pairs the panel] --> Commands
  fail 401; the roster shows a commandable agent that rejects
  commands. The integration doc states pairing as step 2 of 2.
- [ApiBaseUrl leaks an internal hostname to the panel] --> Same trust
  model as `Registration.BaseUrl`: the operator chose the panel. The
  envelope carries no credentials.
- [Panel loses the envelope and only sees heartbeats] --> It cannot
  command until the next envelope arrives (restart or health change).
  Accepted: commands are incident-response tools, not steady-state
  control.

## Migration Plan

Additive: a config without `ApiBaseUrl` behaves exactly as before; the
schema gains an optional property (no default — absence is the off
state, so `netclaw doctor --fix` must not inject it). Rollback is
removing the property; the panel degrades the agent to telemetry-only.

## Security Considerations

- Positive: operator sets `ApiBaseUrl` and pairs the panel device; the
  panel's stop command reaches the intervention endpoint with a valid
  token and the ledger records the device identity.
- Negative: an unpaired caller that learns the URL posts a stop and
  receives 401; the ledger gains nothing.
- Negative: an operator who never sets `ApiBaseUrl` keeps a
  telemetry-only agent even when the daemon API is exposed for other
  purposes — exposure and command consent do not couple.
