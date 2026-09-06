## ADDED Requirements

### Requirement: Command URL advertisement

The registration envelope SHALL carry an optional `apiBaseUrl` field:
the absolute base URL of this daemon's HTTP API, for use by the control
panel to issue commands. The daemon SHALL populate the field only when
the operator sets `Registration.ApiBaseUrl` in netclaw.json; it SHALL
never derive the value from the daemon bind address or exposure mode.
An unset value SHALL omit the field from the envelope. A set value MUST
be an absolute HTTP or HTTPS URI; an invalid value SHALL fail
validation before persistence.

#### Scenario: Operator advertises the command URL

- **WHEN** the operator sets `Registration.ApiBaseUrl` to
  `https://agent-03.corp.local:5000`
- **THEN** every status envelope the daemon sends carries
  `apiBaseUrl: "https://agent-03.corp.local:5000"`

#### Scenario: No advertisement without explicit consent

- **WHEN** `Registration.ApiBaseUrl` is unset and the daemon binds a
  non-loopback address
- **THEN** the envelope omits `apiBaseUrl` and the panel must treat
  the agent as telemetry-only

#### Scenario: Invalid URL is rejected before persistence

- **WHEN** a config editor is given `Registration.ApiBaseUrl` =
  `not a url`
- **THEN** validation rejects the value and nothing is persisted
