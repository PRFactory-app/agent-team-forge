#!/usr/bin/env bash
# One-command FAKE-CORE demo (test checkpoint, not a real-agent or product demo).
#
# Runs the existing C# process scenario
#   ClientLifetimeScenarios.Killed_bridge_does_not_stop_work_and_a_fresh_bridge_gets_the_result
# which drives real processes: MCP bridge submit_job (held fake child) -> bridge
# SIGKILLed -> daemon and held fake survive -> fresh bridge get_job returns the
# committed result -> same-key submit is `existing` with 1 attempt/1 invocation
# -> result survives a daemon SIGKILL + restart.
#
# Safety: the scenario harness (SpikeRig) creates disposable state dirs under
# this run's unique temp root. This script removes only that root at exit,
# including after a bounded test failure. No models, Herdr, services, or uploads.
#
# Usage: scripts/demo.sh            build (Release) and run against the JIT apphost
#        ATF_DEMO_BIN=path/atf scripts/demo.sh   run against a published binary
#        DOTNET=/path/to/dotnet selects the SDK (default: <repo>/.tools/dotnet11/dotnet)
# Exit: 0 pass, 1 scenario failed/timed out, 2 blocked (SDK/binary missing).
set -euo pipefail
# Anchor caller-relative overrides to the caller's cwd (lexically, so a missing
# target stays missing) before changing to the repository root.
abs() { if [[ "$1" == /* ]]; then printf '%s\n' "$1"; else printf '%s\n' "$PWD/$1"; fi; }
if [[ "${DOTNET:-}" == */* ]]; then DOTNET="$(abs "$DOTNET")"; fi
if [[ -n "${ATF_DEMO_BIN:-}" ]]; then ATF_DEMO_BIN="$(abs "$ATF_DEMO_BIN")"; fi
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SCENARIO="AgentTeamForge.Tests.Scenarios.ClientLifetimeScenarios.Killed_bridge_does_not_stop_work_and_a_fresh_bridge_gets_the_result"
TEST_TIMEOUT="${ATF_DEMO_TEST_TIMEOUT:-180}"
BUILD_TIMEOUT="${ATF_DEMO_BUILD_TIMEOUT:-600}"

if [[ -z "${DOTNET:-}" ]]; then
  common="$(git rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)"
  DOTNET="${common:+$common/../.tools/dotnet11/dotnet}"
fi
[[ -n "${DOTNET:-}" && -x "$DOTNET" ]] || { echo "BLOCKED: SDK not found or not executable: ${DOTNET:-unset} (set DOTNET=)" >&2; exit 2; }
DOTNET="$(realpath "$DOTNET")"
export DOTNET_ROOT="$(dirname "$DOTNET")" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
PIN="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' global.json)"
actual="$("$DOTNET" --version 2>/dev/null || true)"
[[ "$actual" == "$PIN" ]] || { echo "BLOCKED: pinned SDK $PIN is not active ('${actual:-none}')" >&2; exit 2; }

binary_env=()
if [[ -n "${ATF_DEMO_BIN:-}" ]]; then
  [[ -x "$ATF_DEMO_BIN" ]] || { echo "BLOCKED: not executable: $ATF_DEMO_BIN" >&2; exit 2; }
  binary_env=(ATF_HOST_BINARY="$ATF_DEMO_BIN")
fi

mkdir -p .run
RUN_DIR="$(mktemp -d "$ROOT/.run/demo-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"

echo "== AgentTeamForge fake-core demo (TEST CHECKPOINT: fake backend, not a real-agent/product approval)"
echo "   sdk $actual | binary ${ATF_DEMO_BIN:-built apphost (JIT)} | evidence $RUN_DIR"

echo "== build (Release)"
if ! timeout --kill-after=10 "$BUILD_TIMEOUT" "$DOTNET" build AgentTeamForge.slnx -c Release \
    > "$RUN_DIR/build.log" 2>&1; then
  echo "FAIL: build failed or timed out; see $RUN_DIR/build.log" >&2
  exit 1
fi

echo "== run scenario (bounded ${TEST_TIMEOUT}s)"
ATF_TEST_TMP_ROOT="$(mktemp -d /tmp/atf-demo-XXXXXX)"
export ATF_TEST_TMP_ROOT
cleanup_tmp() {
  local status="$1"
  if [[ "$status" != 0 && "${ATF_KEEP_TMP:-}" == 1 ]]; then
    echo "   kept temp state: $ATF_TEST_TMP_ROOT" >&2
  else
    rm -rf -- "$ATF_TEST_TMP_ROOT" || status=1
  fi
  exit "$status"
}
trap 'cleanup_tmp $?' EXIT
status=0
env "${binary_env[@]}" timeout --kill-after=10 "$TEST_TIMEOUT" "$DOTNET" test AgentTeamForge.slnx \
  -c Release --no-build --filter "FullyQualifiedName=$SCENARIO" \
  --logger "trx;LogFileName=demo.trx" --results-directory "$RUN_DIR" \
  > "$RUN_DIR/test.log" 2>&1 || status=$?

# Require exactly one executed, passed test: an empty filter match must not look green.
trx="$RUN_DIR/demo.trx"
counters="$(grep -o '<Counters [^>]*>' "$trx" 2>/dev/null || true)"
attr() { sed -n "s/.* $1=\"\([^\"]*\)\".*/\1/p" <<<"$counters"; }
total="$(attr total)"; passed="$(attr passed)"; failed="$(attr failed)"
duration="$(grep -o 'duration="[^"]*"' "$trx" 2>/dev/null | head -1 | cut -d'"' -f2 || true)"

if [[ "$status" == 0 && "$total" == 1 && "$passed" == 1 && "$failed" == 0 ]]; then
  cat <<EOF
== PASS  ${SCENARIO##*.} (${duration:-?})
   [ok] MCP bridge #1: initialize, tools/list, submit_job key=demo-1 (fake child held) -> accepted
   [ok] bridge #1 SIGKILLed; daemon alive; job still running
   [ok] barrier released; fresh bridge #2 get_job -> completed "fake-result: say hi ✓"
   [ok] same-key submit_job -> outcome=existing, same job, attempts=1, invocations=1
   [ok] daemon SIGKILLed and restarted -> job still completed, invocations=1
   (each line is an assertion in tests/AgentTeamForge.Tests/Scenarios/ClientLifetimeScenarios.cs)
EOF
  result=0
else
  echo "== FAIL  exit=$status total=${total:-none} passed=${passed:-none}; see $RUN_DIR/test.log" >&2
  [[ "$status" == 124 || "$status" == 137 ]] && echo "   test run hit the ${TEST_TIMEOUT}s bound" >&2
  result=1
fi

echo "   evidence: $RUN_DIR (trx, build/test logs)"
exit "$result"
