#!/usr/bin/env bash
# OPT-IN real-agent demo: submit -> result -> follow-up in the same native session.
# Needs the real agent CLI (claude, codex or pi) on PATH, logged in; it spends
# model tokens and the agent runs with bypassed permissions in the given cwd.
#
# Usage: scripts/demo-real.sh [claude|codex|pi|fake]   (default: claude; fake = plumbing dry run)
#   ATF_BIN=path/atf         use this binary instead of building (Release apphost)
#   ATF_DEMO_CWD=DIR         agent working directory (default: a new empty dir under .run/)
#   DOTNET=/path/to/dotnet   SDK for the build (default: <repo>/.tools/dotnet11/dotnet)
# The script starts its own daemon on a new private state dir under /tmp and
# stops only that daemon (by the PID it started). Nothing is deleted.
# Exit: 0 pass, 1 failed, 2 blocked (CLI/SDK missing).
set -euo pipefail
abs() { if [[ "$1" == /* ]]; then printf '%s\n' "$1"; else printf '%s\n' "$PWD/$1"; fi; }
if [[ -n "${ATF_BIN:-}" ]]; then ATF_BIN="$(abs "$ATF_BIN")"; fi
if [[ -n "${ATF_DEMO_CWD:-}" ]]; then ATF_DEMO_CWD="$(abs "$ATF_DEMO_CWD")"; fi
if [[ "${DOTNET:-}" == */* ]]; then DOTNET="$(abs "$DOTNET")"; fi
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

BACKEND="${1:-claude}"
case "$BACKEND" in claude|codex|pi) command -v "$BACKEND" >/dev/null || { echo "BLOCKED: '$BACKEND' CLI not on PATH" >&2; exit 2; } ;;
  fake) ;; # plumbing dry run only: no model, no recall check
  *) echo "usage: $0 [claude|codex|pi|fake]" >&2; exit 64 ;; esac
TURN_TIMEOUT="${ATF_DEMO_TURN_TIMEOUT:-600}"

mkdir -p .run
RUN_DIR="$(mktemp -d "$ROOT/.run/demo-real-$BACKEND-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
if [[ -z "${ATF_BIN:-}" ]]; then
  if [[ -z "${DOTNET:-}" ]]; then
    common="$(git rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)"
    DOTNET="${common:+$common/../.tools/dotnet11/dotnet}"
  fi
  [[ -n "${DOTNET:-}" && -x "$DOTNET" ]] || { echo "BLOCKED: SDK not found: ${DOTNET:-unset} (set DOTNET= or ATF_BIN=)" >&2; exit 2; }
  export DOTNET_ROOT="$(dirname "$(realpath "$DOTNET")")" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
  echo "== build (Release)"
  "$DOTNET" build src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -c Release -o "$RUN_DIR/bin" > "$RUN_DIR/build.log" 2>&1 \
    || { echo "FAIL: build; see $RUN_DIR/build.log" >&2; exit 1; }
  ATF_BIN="$RUN_DIR/bin/atf"
fi
[[ -x "$ATF_BIN" ]] || { echo "BLOCKED: not executable: $ATF_BIN" >&2; exit 2; }
WORK="${ATF_DEMO_CWD:-$RUN_DIR/work}"
mkdir -p "$WORK"
# Unix socket paths are short: the private state dir lives under /tmp (not deleted).
STATE="$(mktemp -d /tmp/atf-demo-XXXXXX)/state"

"$ATF_BIN" init --state-dir "$STATE" > /dev/null
"$ATF_BIN" daemon --state-dir "$STATE" 2> "$RUN_DIR/daemon.log" &
DAEMON=$!
trap 'kill "$DAEMON" 2>/dev/null || true; wait "$DAEMON" 2>/dev/null || true' EXIT
for _ in $(seq 100); do grep -q ready "$RUN_DIR/daemon.log" 2>/dev/null && break; sleep 0.1; done
grep -q ready "$RUN_DIR/daemon.log" || { echo "FAIL: daemon not ready; see $RUN_DIR/daemon.log" >&2; exit 1; }
echo "== $BACKEND | cwd $WORK | state $STATE | evidence $RUN_DIR"

client() { "$ATF_BIN" client "$@" --state-dir "$STATE"; }
field() { sed -n "s/.*[{,]\"$1\":\"\([^\"]*\)\".*/\1/p"; }
wait_done() { # job-id -> prints final get_job JSON; fails unless completed
  local out status deadline=$((SECONDS + TURN_TIMEOUT))
  while (( SECONDS < deadline )); do
    out="$(client get --job "$1" || true)"
    status="$(field status <<<"$out")"
    case "$status" in queued|running) sleep 2 ;; completed) printf '%s\n' "$out"; return 0 ;;
      *) echo "FAIL: job $1 ended $status: $out" >&2; return 1 ;; esac
  done
  echo "FAIL: job $1 not done after ${TURN_TIMEOUT}s" >&2; return 1
}

KEY="demo-$(date +%s)"
submitted="$(client submit --backend "$BACKEND" --cwd "$WORK" --key "$KEY-1" \
  --instruction "Remember the code word PELICAN. Reply with just: OK")" || { echo "FAIL: submit: $submitted" >&2; exit 1; }
JOB="$(field job_id <<<"$submitted")"
echo "== submitted $JOB; waiting for the result"
first="$(wait_done "$JOB")"
echo "   result: $(field result <<<"$first")"
echo "   session: $(field session_id <<<"$first")"

followed="$(client follow-up --job "$JOB" --key "$KEY-2" \
  --instruction "What was the code word? Reply with just the word.")" || { echo "FAIL: follow-up: $followed" >&2; exit 1; }
CHILD="$(field job_id <<<"$followed")"
echo "== follow-up $CHILD (same session); waiting"
second="$(wait_done "$CHILD")"
echo "   result: $(field result <<<"$second")"
echo "   session: $(field session_id <<<"$second")"
printf '%s\n%s\n' "$first" "$second" > "$RUN_DIR/results.jsonl"
if [[ "$BACKEND" == fake ]]; then
  [[ "$(field session_id <<<"$first")" == "$(field session_id <<<"$second")" ]] || { echo "== FAIL: session not resumed" >&2; exit 1; }
  echo "== PASS (fake dry run): follow-up resumed session $(field session_id <<<"$second")"
elif grep -qi pelican <<<"$(field result <<<"$second")"; then
  echo "== PASS: follow-up recalled the first turn (native session resumed)"
else
  echo "== FAIL: follow-up did not recall the code word; see $RUN_DIR/results.jsonl" >&2; exit 1
fi
