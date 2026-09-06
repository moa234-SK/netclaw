## Why

The control panel holds a roster of registered agents but has no way to
act on them. The daemon now exposes Operator-authenticated session
intervention endpoints, and the fleet runs on a shared network where the
panel can reach each agent directly. What is missing is discovery (the
panel does not know where to call) and a documented integration
contract.

Related PRDs: PRD-003 (operator UX), PRD-010 (self-registration). This
proposal extends PRD-010's registration contract with one optional
field.

## What Changes

- The registration envelope gains an optional `apiBaseUrl`: the base
  URL the panel may use to call this daemon's HTTP API. The field is
  populated only when the operator sets `Registration.ApiBaseUrl`
  explicitly (explicit consent — advertising a command surface is an
  authority decision, never derived from daemon binding).
- The heartbeat payload is unchanged.
- New documented capability `panel-direct-commands`: how an external
  control panel discovers commandable agents, exchanges a pairing code
  for a device bearer token, issues session intervention commands,
  reads the intervention ledger, and how revocation works.
- No new daemon endpoints, no auth changes, no inbound surface changes.
  The intervention endpoints and pairing flow already exist.

## Capabilities

### New Capabilities

- `panel-direct-commands`: the daemon's side of the direct-call
  integration contract for external control panels (discovery,
  pairing, command surface, error semantics, revocation).

### Modified Capabilities

- `daemon-self-registration`: one ADDED requirement — optional
  command URL advertisement in the envelope (`apiBaseUrl`).

## Impact

- Code: `Netclaw.Registration` (config property + validator rule +
  envelope field + service passthrough), configuration schema
  (`Registration.ApiBaseUrl`, optional, no default).
- Security: the advertised URL is operator-declared data sent to the
  panel — the same trust model as `Registration.BaseUrl`. No secrets
  travel in the envelope; command authority still requires a paired
  device token per agent.
- Operations: new `docs/integrations/control-panel.md`; runbook
  cross-link; `netclaw-operations` skill update.
