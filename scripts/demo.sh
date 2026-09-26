#!/usr/bin/env bash
# One-command FAKE-CORE demo (test checkpoint, not a real-agent or product demo).
#
# Runs the existing C# process scenario
#   ClientLifetimeScenarios.Killed_bridge_does_not_stop_work_and_a_fresh_bridge_gets_the_result
# which drives real processes: MCP bridge job_submit (held fake child) -> bridge
# SIGKILLed -> daemon and held fake survive -> fresh bridge job_get returns the
# committed result -> same-key submit is `existing` with 1 attempt/1 invocation
# -> result survives a daemon SIGKILL + restart.
#
# Safety: the scenario harness (SpikeRig) creates its own unique 0700 state dir
# (/tmp/atf-<random>), starts/kills only processes it holds by handle, bounds
# every wait, and deletes only that dir. This script deletes nothing: it writes
# evidence to a new run dir under .run/ (gitignored) and only *reports* any new
# /tmp/atf-* leftovers. No models, Herdr, services, or uploads.
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
list_tmp_state() { find /tmp -maxdepth 1 -name 'atf-*' -user "$(id -u)" 2>/dev/null | sort; }
list_tmp_state > "$RUN_DIR/tmp-before.txt"

echo "== AgentTeamForge fake-core demo (TEST CHECKPOINT: fake backend, not a real-agent/product approval)"
echo "   sdk $actual | binary ${ATF_DEMO_BIN:-built apphost (JIT)} | evidence $RUN_DIR"

echo "== build (Release)"
if ! timeout --kill-after=10 "$BUILD_TIMEOUT" "$DOTNET" build AgentTeamForge.slnx -c Release \
    > "$RUN_DIR/build.log" 2>&1; then
  echo "FAIL: build failed or timed out; see $RUN_DIR/build.log" >&2
  exit 1
fi

echo "== run scenario (bounded ${TEST_TIMEOUT}s)"
status=0
env "${binary_env[@]}" timeout --kill-after=10 "$TEST_TIMEOUT" "$DOTNET" test AgentTeamForge.slnx \
  -c Release --no-build --filter "FullyQualifiedName=$SCENARIO" \
  --logger "trx;LogFileName=demo.trx" --results-directory "$RUN_DIR" \
  > "$RUN_DIR/test.log" 2>&1 || status=$?

list_tmp_state > "$RUN_DIR/tmp-after.txt"
leftovers="$(comm -13 "$RUN_DIR/tmp-before.txt" "$RUN_DIR/tmp-after.txt")"

# Require exactly one executed, passed test: an empty filter match must not look green.
trx="$RUN_DIR/demo.trx"
counters="$(grep -o '<Counters [^>]*>' "$trx" 2>/dev/null || true)"
attr() { sed -n "s/.* $1=\"\([^\"]*\)\".*/\1/p" <<<"$counters"; }
total="$(attr total)"; passed="$(attr passed)"; failed="$(attr failed)"
duration="$(grep -o 'duration="[^"]*"' "$trx" 2>/dev/null | head -1 | cut -d'"' -f2 || true)"

if [[ "$status" == 0 && "$total" == 1 && "$passed" == 1 && "$failed" == 0 ]]; then
  cat <<EOF
== PASS  ${SCENARIO##*.} (${duration:-?})
   [ok] MCP bridge #1: initialize, tools/list, job_submit key=demo-1 (fake child held) -> accepted
   [ok] bridge #1 SIGKILLed; daemon alive; job still running
   [ok] barrier released; fresh bridge #2 job_get -> completed "fake-result: say hi ✓"
   [ok] same-key job_submit -> outcome=existing, same job, attempts=1, invocations=1
   [ok] daemon SIGKILLed and restarted -> job still completed, invocations=1
   (each line is an assertion in tests/AgentTeamForge.Tests/Scenarios/ClientLifetimeScenarios.cs)
EOF
  result=0
else
  echo "== FAIL  exit=$status total=${total:-none} passed=${passed:-none}; see $RUN_DIR/test.log" >&2
  [[ "$status" == 124 || "$status" == 137 ]] && echo "   test run hit the ${TEST_TIMEOUT}s bound" >&2
  result=1
fi

if [[ -n "$leftovers" ]]; then
  echo "WARN: new state dirs remain (not deleted; inspect manually):" >&2
  sed 's/^/   /' <<<"$leftovers" >&2
fi
echo "   evidence: $RUN_DIR (trx, build/test logs)"
exit "$result"
