# Device Pairing


## Device Pairing


Remote devices authenticate with the daemon using a two-sided pairing protocol.

### Pairing flow

**Daemon side** (requires local/SSH access):

```
shell_execute: netclaw daemon pair
```

This generates a single-use pairing code (8 chars, 5-minute TTL). The code
generation endpoint is loopback-only.

If `netclaw daemon pair` fails immediately after an exposure-mode change, run
`netclaw doctor` and inspect `~/.netclaw/logs/crash-*.log` for the specific
startup validation failure instead of assuming a generic readiness timeout.

**Client side** (remote device):

```
shell_execute: netclaw pair https://my-daemon.tail1234.ts.net:5000
```

The user is prompted for the pairing code. On success, the bearer token is
saved to `secrets.json` (`DeviceToken` field) and the endpoint is saved to
`~/.netclaw/client/config.json`.

### Auto-pairing a control panel

Manual pairing needs the operator to copy the code to the panel. With
`Registration.AutoPair: true` (requires `Registration.ApiBaseUrl`) in
`netclaw.json`, the daemon mints a single-use code at startup and includes
it as `pairingCode` in the registration envelope. The panel exchanges it
itself at `POST {apiBaseUrl}/api/pair/exchange` with
`{"code": "...", "deviceName": "panel"}`. The code rides the startup
envelope only — never heartbeats. If the panel missed it (it was down, or
the 5-minute TTL lapsed), fall back to the manual flow above.

The pairing code grants command authority. It does not authenticate the
registration sender to the panel.

### Device management

| Action | Command |
|--------|---------|
| List paired devices | `netclaw daemon devices` |
| Revoke a device | `netclaw daemon devices revoke <name>` |

### Security notes

- Codes: single-use, 8 chars from 32-char alphabet (~1.1 trillion
  combinations), 5-minute TTL
- Rate limiting: 5 attempts/min/IP; after 10 failures, the IP is blocked for
  15 minutes
- When no code is pending, the exchange endpoint returns 404 (invisible to
  scanners)
- Tokens are stored as salted SHA-256 hashes on the daemon; the raw token is
  never persisted server-side

### Config locations

- `~/.netclaw/config/devices.json` — paired device registry (daemon side)
- `~/.netclaw/config/secrets.json` — `DeviceToken` field (client side, added
  by `netclaw pair`)
- `~/.netclaw/config/netclaw.json` — `Daemon` section (`Host`, `Port`,
  `ExposureMode`)
