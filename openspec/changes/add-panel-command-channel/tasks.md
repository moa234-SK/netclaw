## 1. Config + envelope

- [x] 1.1 `RegistrationConfig.ApiBaseUrl` with `ConfigValue` mapping
  and doc comment
- [x] 1.2 `RegistrationConfigValidator`: absolute HTTP(S) URI rule in
  `TryValidate`
- [x] 1.3 `RegistrationEnvelope.ApiBaseUrl` (nullable,
  `JsonIgnore(WhenWritingNull)`)
- [x] 1.4 `SelfRegistrationService.PostEnvelopeAsync` passes
  `_config.ApiBaseUrl`
- [x] 1.5 TUI registration editor preserves `ApiBaseUrl` on save (the
  page rewrites the whole section; without this, a TUI save would drop
  a hand-set value)

## 2. Schema + docs

- [x] 2.1 `netclaw-config.v1.schema.json`: `Registration.ApiBaseUrl`
  (optional string, format uri, no default — absence is the off state)
- [x] 2.2 `docs/integrations/control-panel.md`: discovery, pairing,
  command surface, error semantics, revocation
- [x] 2.3 Runbook cross-link in `docs/runbooks/session-intervention.md`
- [x] 2.4 `netclaw-operations` skill: panel-command note +
  version bump (2.67.1)

- [x] 2.5 Command surface spec + integration doc extended with the
  agent freeze commands (`/api/agent/suspend|resume|interventions`,
  `governance.agentState` in the envelope) — built with the
  `add-agent-governance` change

## 3. Gates

- [x] 3.1 `openspec validate add-panel-command-channel`
- [x] 3.2 `dotnet build Netclaw.slnx`
- [x] 3.3 `dotnet slopwatch analyze`
- [x] 3.4 `./scripts/Add-FileHeaders.ps1 -Verify`

## 4. Tests (deferred by operator instruction)

- [ ] 4.1 Validator: valid/invalid/blank ApiBaseUrl cases
- [ ] 4.2 Envelope serialization: field omitted when null, present
  and camelCase when set
- [ ] 4.3 Schema round-trip: config with ApiBaseUrl validates against
  the schema; doctor fix does not inject the property
- [ ] 4.4 TUI save preserves ApiBaseUrl (fake-failure proof that save
  is blocked before persistence when the validator rejects, and that
  passthrough survives a save)

Note: the operator deferred test work. Do not archive this change
before section 4 is complete.
