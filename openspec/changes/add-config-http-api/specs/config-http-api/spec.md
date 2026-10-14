## Purpose

Gives authorized remote clients a read and mutation surface for the daemon
configuration document over HTTP, with secrets redacted, validation before
persistence, and local-only network fields.

## ADDED Requirements

### Requirement: Operator-only config API

The daemon SHALL expose `GET /api/config` and `PATCH /api/config` only to
authenticated operator principals. Unauthenticated callers SHALL receive `401`.
Authenticated non-operator principals SHALL receive `403`. The daemon owns this
decision at the endpoint layer; no config data crosses the boundary before the
check.

#### Scenario: Anonymous remote request is refused

- **GIVEN** the daemon receives a config request from a non-loopback address
  without credentials
- **WHEN** the caller sends `GET /api/config`
- **THEN** the daemon responds `401`
- **AND** the response body carries no configuration content

#### Scenario: Operator reads the document

- **GIVEN** a loopback caller or a paired operator device
- **WHEN** the caller sends `GET /api/config`
- **THEN** the daemon responds `200` with the configuration document

### Requirement: Config read redacts secrets

`GET /api/config` SHALL return the `netclaw.json` document with every leaf that
`secrets.json` stores replaced by a non-secret placeholder. The secrets file
defines the secret set. The response SHALL include a metadata array that lists
the redacted paths. The response SHALL include the read-only daemon paths. No
error path, log line, or partial response SHALL disclose a decrypted secret
value.

#### Scenario: Secret leaf is replaced

- **GIVEN** `secrets.json` stores `Providers.OpenAI.ApiKey`
- **WHEN** the operator sends `GET /api/config`
- **THEN** the response shows the `Providers.OpenAI` entry with a placeholder
  at `ApiKey`, not the stored value
- **AND** the metadata array lists `Providers.OpenAI.ApiKey` as redacted

#### Scenario: Non-secret expiry field stays visible

- **GIVEN** `netclaw.json` stores `Providers.OpenAI.OAuthTokenExpiry`
- **WHEN** the operator sends `GET /api/config`
- **THEN** the response shows the expiry value unchanged

### Requirement: Patch applies a JSON merge patch

`PATCH /api/config` SHALL accept an RFC 7386 JSON Merge Patch body over the
effective document. A `null` member SHALL delete that key. The daemon SHALL
apply the patch as one all-or-nothing operation: it writes both files, or
neither.

#### Scenario: Field update

- **GIVEN** the current config has `Memory.Enabled` set to `true`
- **WHEN** the operator sends `{"Memory":{"Enabled":false}}`
- **THEN** the daemon responds `200`
- **AND** the persisted `netclaw.json` has `Memory.Enabled` set to `false`
- **AND** all other keys keep their values

#### Scenario: Null deletes a key

- **GIVEN** the current config has `Identity.UserName` set
- **WHEN** the operator sends `{"Identity":{"UserName":null}}`
- **THEN** the daemon removes `Identity.UserName` from `netclaw.json`
- **AND** an empty parent section is pruned

### Requirement: Secret values route to the secrets file

A patch that supplies a plain string at a secret path SHALL store the value
encrypted in `secrets.json` and SHALL NOT write it to `netclaw.json`. A patch
that supplies the redaction placeholder at a secret path SHALL keep the stored
value unchanged. A patch that supplies `null` at a secret path SHALL delete the
stored secret. Secret values SHALL never appear in any response body or log
line.

#### Scenario: New API key lands encrypted

- **GIVEN** `secrets.json` stores `Providers.OpenAI.ApiKey`
- **WHEN** the operator sends `{"Providers":{"OpenAI":{"ApiKey":"sk-new"}}}`
- **THEN** the daemon stores the new key encrypted in `secrets.json`
- **AND** `netclaw.json` carries no `ApiKey` member
- **AND** the response reports the changed path without the value

#### Scenario: Placeholder preserves the stored secret

- **GIVEN** `secrets.json` stores `Providers.OpenAI.ApiKey`
- **WHEN** the operator sends the placeholder from `GET` back unchanged at that
  path
- **THEN** the daemon keeps the stored key
- **AND** the response reports no change at that path

### Requirement: Validation gate blocks persistence

The daemon SHALL validate the merged `netclaw.json` document before it writes
any file. Validation SHALL check the v1 schema, an `IConfiguration` build and
bind, and provider type existence. An invalid patch SHALL return `400` with
field-level errors, and the daemon SHALL write nothing to disk.

#### Scenario: Schema-invalid value is rejected

- **GIVEN** the schema allows `Security.ShellExecutionMode` values `Off`,
  `SandboxOnly`, `HostAllowed`
- **WHEN** the operator sends `{"Security":{"ShellExecutionMode":"Always"}}`
- **THEN** the daemon responds `400` with an error that names the field
- **AND** both files on disk are unchanged

#### Scenario: Unknown provider type is rejected

- **GIVEN** the provider registry knows provider types `openai` and `anthropic`
- **WHEN** the operator sends `{"Providers":{"MyProvider":{"Type":"nonsense"}}}`
- **THEN** the daemon responds `400`
- **AND** both files on disk are unchanged

### Requirement: Daemon network fields are read-only over HTTP

`Daemon.Host`, `Daemon.ExposureMode`, and `Daemon.TrustedProxies` SHALL be
readable through the API and SHALL NOT be modifiable through it. A patch that
sets, changes, or deletes any of these paths SHALL return `400`, SHALL name the
rejected paths, and SHALL write nothing. The daemon owns this list as policy
data; local CLI, TUI, and file edits stay the only mutation paths.

#### Scenario: Exposure mode change is rejected

- **GIVEN** the daemon runs with `Daemon.ExposureMode` set to `ReverseProxy`
- **WHEN** the operator sends `{"Daemon":{"ExposureMode":"Loopback"}}`
- **THEN** the daemon responds `400` and names `Daemon.ExposureMode`
- **AND** both files on disk are unchanged

#### Scenario: Full-document patch that repeats current values is still rejected

- **GIVEN** the daemon runs with `Daemon.Host` set to `127.0.0.1`
- **WHEN** the operator sends a whole-document patch that includes
  `Daemon.Host` with its current value
- **THEN** the daemon responds `400` and names `Daemon.Host`

#### Scenario: Security section stays editable

- **GIVEN** the daemon runs with `Security.DeploymentPosture` set to `Team`
- **WHEN** the operator sends `{"Security":{"DeploymentPosture":"Personal"}}`
- **THEN** the daemon accepts the change subject to validation

### Requirement: Accepted patches ride the file reload path

An accepted patch SHALL persist atomically to disk and SHALL NOT mutate runtime
config in place. The response SHALL report that a restart is pending and SHALL
carry the current restart generation. The existing config watcher remains the
only reload trigger.

#### Scenario: Accepted patch reports pending restart

- **GIVEN** the daemon runs with the config watcher active
- **WHEN** an operator patch passes validation and is written
- **THEN** the daemon responds `200` with a pending-restart marker and the
  restart generation
- **AND** the config watcher performs the coordinated restart

#### Scenario: Watcher rejection keeps the old config

- **GIVEN** an accepted patch was written and the watcher later rejects the
  merged file
- **WHEN** the watcher completes validation
- **THEN** the daemon continues on the previous effective config
- **AND** the rejection is visible in the daemon log

### Requirement: Config writes are audited

The daemon SHALL record an audit entry for every `PATCH /api/config` attempt
that passes authorization. The entry SHALL name the actor and the changed
top-level paths. Entries that change the `Security` section SHALL log at warning
level with the old and new values. Audit entries SHALL NOT contain secret
values. Audit records are durable only through the daemon log.

#### Scenario: Security change is audited with values

- **GIVEN** `Security.ShellExecutionMode` is `Off`
- **WHEN** an operator patch sets it to `HostAllowed`
- **THEN** the daemon writes a warning-level audit entry with the actor, the
  path, the old value, and the new value

#### Scenario: Secret change is audited without the value

- **WHEN** an operator patch rotates an API key
- **THEN** the audit entry names the path and the actor
- **AND** the entry contains no key material
