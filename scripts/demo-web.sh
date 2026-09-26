#!/usr/bin/env bash
# FAKE-BACKEND web console demo (Linux; no models). Builds the Release apphost
# (or uses ATF_DEMO_BIN), creates a NEW private state dir, starts a daemon it
# owns by PID, submits one fake job, then runs `atf web` in the foreground.
# Ctrl+C stops the web role and the demo-owned daemon; the state dir is kept.
#
# Usage: scripts/demo-web.sh [PORT]        (default 18080; binds 127.0.0.1 only)
#        ATF_DEMO_BIN=path/atf scripts/demo-web.sh   use a published binary
set -euo pipefail
PORT="${1:-18080}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
if [[ -z "${ATF_DEMO_BIN:-}" ]]; then
  common="$(git rev-parse --path-format=absolute --git-common-dir)"
  DOTNET="${DOTNET:-$(realpath "$common/../.tools/dotnet11/dotnet")}"
  export DOTNET_ROOT="$(dirname "$DOTNET")"
  "$DOTNET" build src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -c Release -nologo -v q >/dev/null
  ATF_DEMO_BIN="$ROOT/src/AgentTeamForge.Host/bin/Release/net11.0/atf"
fi
STATE="$(mktemp -d /tmp/atf-web-XXXXXX)/state"   # short path: Unix socket limit
"$ATF_DEMO_BIN" init --state-dir "$STATE" --test-profile >/dev/null
"$ATF_DEMO_BIN" daemon --state-dir "$STATE" 2>"$STATE.daemon.log" &
DAEMON=$!
WEB=
trap 'kill $WEB "$DAEMON" 2>/dev/null || true' EXIT INT TERM
for _ in $(seq 50); do [[ -S "$STATE/daemon.sock" ]] && break; sleep 0.1; done
"$ATF_DEMO_BIN" client submit --state-dir "$STATE" --key demo-1 --instruction "hello from the web demo" --backend fake --cwd "$ROOT"
echo "state dir: $STATE"
echo "open the url below and paste the token (the page keeps it in memory only):"
"$ATF_DEMO_BIN" web --state-dir "$STATE" --port "$PORT" &
WEB=$!
wait "$WEB"
