#!/usr/bin/env bash
# FAKE-BACKEND web console demo (no models). Builds the Release apphost
# (or uses ATF_DEMO_BIN), creates a NEW private state dir whose launch mode
# carries the console port, starts a daemon it owns by PID (the daemon hosts the
# console), submits one fake job, prints the console link and waits.
# Ctrl+C stops the demo-owned daemon; the state dir is kept.
#
# Usage: scripts/demo-web.sh [PORT]        (default 18080; binds 127.0.0.1 only)
#        ATF_DEMO_BIN=path/atf scripts/demo-web.sh   use this binary (published, or a JIT apphost)
#        DOTNET=/path/to/dotnet selects the SDK (default: <repo>/.tools/dotnet11/dotnet)
set -euo pipefail
PORT="${1:-18080}"
# Anchor caller-relative overrides to the caller's cwd (lexically, so a missing
# target stays missing) before changing to the repository root.
abs() { if [[ "$1" == /* ]]; then printf '%s\n' "$1"; else printf '%s\n' "$PWD/$1"; fi; }
if [[ "${DOTNET:-}" == */* ]]; then DOTNET="$(abs "$DOTNET")"; fi
if [[ -n "${ATF_DEMO_BIN:-}" ]]; then ATF_DEMO_BIN="$(abs "$ATF_DEMO_BIN")"; fi
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
if [[ -z "${DOTNET:-}" ]]; then
  common="$(git rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)"
  DOTNET="${common:+$common/../.tools/dotnet11/dotnet}"
fi
# The SDK's root doubles as the runtime for a JIT apphost (ATF_DEMO_BIN next to atf.dll).
sdk=
if [[ -n "${DOTNET:-}" && -x "$DOTNET" ]]; then
  sdk=1
  export DOTNET_ROOT="$(dirname "$(realpath "$DOTNET")")" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
fi
if [[ -z "${ATF_DEMO_BIN:-}" ]]; then
  [[ -n "$sdk" ]] || { echo "BLOCKED: SDK not found: ${DOTNET:-unset} (set DOTNET= or ATF_DEMO_BIN=)" >&2; exit 2; }
  "$DOTNET" build src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -c Release -nologo -v q >/dev/null
  ATF_DEMO_BIN="$ROOT/src/AgentTeamForge.Host/bin/Release/net11.0/atf"
fi
[[ -x "$ATF_DEMO_BIN" ]] || { echo "BLOCKED: not executable: $ATF_DEMO_BIN" >&2; exit 2; }
# A JIT apphost without a reachable runtime cannot start; self-contained and native builds can.
"$ATF_DEMO_BIN" --version >/dev/null 2>&1 \
  || { echo "BLOCKED: $ATF_DEMO_BIN does not start; a JIT apphost needs a .NET runtime: ${DOTNET:-unset} (set DOTNET= or DOTNET_ROOT=)" >&2; exit 2; }
STATE="$(mktemp -d /tmp/atf-web-XXXXXX)/state"   # short path: Unix socket limit
"$ATF_DEMO_BIN" init --state-dir "$STATE" --test-profile >/dev/null
# The daemon reads the console port from the launch mode (what `atf setup --web-port` writes).
(umask 077; printf '{"mode":"headless","web_port":%s}\n' "$PORT" > "$STATE/launch-mode.json")
"$ATF_DEMO_BIN" daemon --state-dir "$STATE" 2>"$STATE.daemon.log" &
DAEMON=$!
trap 'kill "$DAEMON" 2>/dev/null || true' EXIT INT TERM
for _ in $(seq 50); do [[ -S "$STATE/daemon.sock" ]] && break; sleep 0.1; done
# `atf client` starts a daemon when none answers; never leave one behind that this script does not own.
kill -0 "$DAEMON" 2>/dev/null || { echo "FAIL: daemon exited; see $STATE.daemon.log" >&2; exit 1; }
"$ATF_DEMO_BIN" client submit --state-dir "$STATE" --key demo-1 --instruction "hello from the web demo" --backend fake --cwd "$ROOT"
echo "state dir: $STATE"
echo "open this link (the token is in the fragment; the page keeps it in memory only); Ctrl+C stops the daemon:"
"$ATF_DEMO_BIN" web --state-dir "$STATE"
wait "$DAEMON"
