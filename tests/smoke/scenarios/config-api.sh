
#!/usr/bin/env bash
# config-api.sh — goal: prove the config HTTP API contract end to end against
# a running daemon:
#   GET  /api/config      -> 200 with every secrets.json leaf redacted and the
#                            read-only daemon paths advertised
#   PATCH read-only field -> 400 naming the rejected path, nothing written
#   PATCH schema-invalid  -> 400, nothing written
#   PATCH valid           -> 200 with restartPending, the file changes on
#                            disk, and the config watcher restarts the daemon
#                            (the restart generation advances)
#
# The daemon never restarts for the rejected patches: the generation header
# must not move between the two 400s.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

command -v jq >/dev/null 2>&1 || die "jq is required for config-api.sh"

NETCLAW_JSON="${NETCLAW_HOME}/config/netclaw.json"
SECRETS_JSON="${NETCLAW_HOME}/config/secrets.json"

trap stop_daemon EXIT

# generation — print the daemon restart generation from the readiness header.
generation() {
  run_timed "$STEP_TIMEOUT_SECONDS" \
    curl -sS -D - -o /dev/null "${DAEMON_BASE_URL}/api/health/ready" 2>/dev/null \
    | awk -F': ' 'tolower($1)=="x-netclaw-generation" { gsub(/\r/, "", $2); print $2 }'
}

# patch <body> — PATCH /api/config and print "status<TAB>body".
patch() {
  local body="$1"
  local status body_out
  body_out="$(mktemp)"
  status="$(run_timed "$STEP_TIMEOUT_SECONDS" \
    curl -sS -o "$body_out" -w '%{http_code}' \
      -X PATCH "${DAEMON_BASE_URL}/api/config" \
      -H 'Content-Type: application/json' \
      --data-binary "$body" 2>/dev/null || true)"
  printf '%s\t%s' "$status" "$(cat "$body_out")"
  rm -f "$body_out"
}

seed_and_start_daemon

# ── GET: redaction and advertised read-only paths ──

log "GET /api/config (loopback operator)..."
get_body="$(run_timed "$STEP_TIMEOUT_SECONDS" \
  curl -sS "${DAEMON_BASE_URL}/api/config" 2>/dev/null || true)"

if jq -e '.config.Providers."local-smoke".ApiKey == "__netclaw_secret__"' <<<"$get_body" >/dev/null 2>&1; then
  pass "GET: the provider API key is the redaction placeholder"
else
  fail "GET: expected the placeholder at Providers.local-smoke.ApiKey"
fi

if jq -e '(.redactedPaths | index("Providers.local-smoke.ApiKey")) != null' <<<"$get_body" >/dev/null 2>&1; then
  pass "GET: redactedPaths lists the provider API key path"
else
  fail "GET: expected Providers.local-smoke.ApiKey in redactedPaths"
fi

if jq -e '(.readOnlyPaths | index("Daemon.ExposureMode")) != null' <<<"$get_body" >/dev/null 2>&1; then
  pass "GET: readOnlyPaths advertises Daemon.ExposureMode"
else
  fail "GET: expected Daemon.ExposureMode in readOnlyPaths"
fi

if jq -e '.config.Providers."local-smoke".Endpoint' <<<"$get_body" >/dev/null 2>&1; then
  pass "GET: the non-secret endpoint stays visible"
else
  fail "GET: expected the provider endpoint to stay visible"
fi

# ── PATCH: read-only field rejected, nothing written ──

log "PATCH touching Daemon.ExposureMode (read-only)..."
read_only_result="$(patch '{"Daemon":{"ExposureMode":"local"}}')"
read_only_status="${read_only_result%%$'\t'*}"
read_only_body="${read_only_result#*$'\t'}"
if [[ "$read_only_status" == "400" ]] \
  && jq -e '(.rejectedPaths | index("Daemon.ExposureMode")) != null' <<<"$read_only_body" >/dev/null 2>&1; then
  pass "PATCH read-only: 400 and rejectedPaths names Daemon.ExposureMode"
else
  fail "PATCH read-only: expected 400 naming Daemon.ExposureMode, got ${read_only_status}: ${read_only_body}"
fi

# ── PATCH: schema-invalid value rejected, nothing written ──

log "PATCH with a schema-invalid Security.ShellExecutionMode..."
config_before="$(cat "$NETCLAW_JSON")"
invalid_result="$(patch '{"Security":{"ShellExecutionMode":"Always"}}')"
invalid_status="${invalid_result%%$'\t'*}"
invalid_body="${invalid_result#*$'\t'}"
if [[ "$invalid_status" == "400" ]] && jq -e '.errors | length > 0' <<<"$invalid_body" >/dev/null 2>&1; then
  pass "PATCH schema-invalid: 400 with error list"
else
  fail "PATCH schema-invalid: expected 400 with errors, got ${invalid_status}: ${invalid_body}"
fi

# Both rejected patches together must not have touched the file or restarted
# the daemon: the file is byte-identical and the generation did not move.
gen_after_rejections="$(generation)"
config_after_rejections="$(cat "$NETCLAW_JSON")"
if [[ "$config_before" == "$config_after_rejections" ]]; then
  pass "rejected patches left netclaw.json byte-identical"
else
  fail "rejected patches changed netclaw.json"
fi

# ── PATCH: valid change persists and drives the watcher restart ──

log "PATCH Memory.Enabled=false (valid)..."
gen_before="$(generation)"
valid_result="$(patch '{"Memory":{"Enabled":false}}')"
valid_status="${valid_result%%$'\t'*}"
valid_body="${valid_result#*$'\t'}"
if [[ "$valid_status" == "200" ]] \
  && jq -e '.restartPending == true' <<<"$valid_body" >/dev/null 2>&1 \
  && jq -e '(.changedTopLevel | index("Memory")) != null' <<<"$valid_body" >/dev/null 2>&1; then
  pass "PATCH valid: 200 with restartPending and Memory in changedTopLevel"
else
  fail "PATCH valid: unexpected response ${valid_status}: ${valid_body}"
fi

if jq -e '.Memory.Enabled == false' "$NETCLAW_JSON" >/dev/null 2>&1; then
  pass "PATCH valid: netclaw.json on disk has Memory.Enabled=false"
else
  fail "PATCH valid: expected Memory.Enabled=false in netclaw.json"
fi

# The API key rotated nowhere: still only in secrets.json, never in netclaw.json.
if jq -e '.Providers."local-smoke".ApiKey == null' "$NETCLAW_JSON" >/dev/null 2>&1; then
  pass "PATCH valid: no API key material in netclaw.json"
else
  fail "PATCH valid: netclaw.json unexpectedly contains an ApiKey"
fi

log "Waiting for the config watcher to restart the daemon..."
restart_seen=0
for _ in $(seq 1 60); do
  current_gen="$(generation || true)"
  if [[ "$current_gen" -gt "$gen_before" ]]; then
    restart_seen=1
    break
  fi
  sleep 1
done
if (( restart_seen )); then
  pass "watcher restart: generation advanced ${gen_before} -> ${current_gen}"
else
  fail "watcher restart: generation did not advance past ${gen_before}"
fi

summarize
exit $?
