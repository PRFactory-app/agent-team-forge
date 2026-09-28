#!/usr/bin/env bash
# Runs the C# process scenarios (daemon, MCP bridge, fake child, crash windows)
# against a supplied binary, then records size/checksum evidence. Uses only
# temporary private state; no credentials or model calls. Evidence goes to a
# new, unique 0700 directory under evidence/.
# DOTNET selects the SDK (default: <repo>/.tools/dotnet11/dotnet, else PATH).
set -euo pipefail
# Anchor caller-relative overrides to the caller's cwd (lexically, so a missing
# target stays missing) before changing to the repository root.
abs() { if [[ "$1" == /* ]]; then printf '%s\n' "$1"; else printf '%s\n' "$PWD/$1"; fi; }
BIN="$(abs "${1:?usage: published-smoke.sh path/to/atf}")"
if [[ -n "${DOTNET:-}" && "$DOTNET" == */* ]]; then
  DOTNET="$(abs "$DOTNET")"
  [[ -x "$DOTNET" ]] || { echo "BLOCKED: DOTNET not executable: $DOTNET" >&2; exit 2; }
  DOTNET="$(realpath "$DOTNET")"
fi
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
if [[ -z "${DOTNET:-}" ]]; then
  common="$(git rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)"
  local_sdk="${common:+$common/../.tools/dotnet11/dotnet}"
  if [[ -n "$local_sdk" && -x "$local_sdk" ]]; then DOTNET="$(realpath "$local_sdk")"; else DOTNET=dotnet; fi
fi
if [[ "$DOTNET" == */* ]]; then export DOTNET_ROOT="$(dirname "$DOTNET")"; fi
[[ -x "$BIN" ]] || { echo "not executable: $BIN" >&2; exit 2; }
BIN="$(realpath "$BIN")"
if file "$BIN" | grep -q 'ELF' && [[ ! -f "$(dirname "$BIN")/atf.dll" ]]; then kind=native; else kind=jit; fi
mkdir -p evidence
EVIDENCE_DIR="$(mktemp -d "$ROOT/evidence/published-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
ATF_TEST_TMP_ROOT="$(mktemp -d /tmp/atf-smoke-XXXXXX)"
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
ATF_HOST_BINARY="$BIN" "$DOTNET" test AgentTeamForge.slnx -c Release --no-build \
  --filter "Category=Scenario" --logger "trx;LogFileName=published-scenarios.trx" \
  --results-directory "$EVIDENCE_DIR/test-results"

# An empty filter match must not look green.
counters="$(grep -o '<Counters [^>]*>' "$EVIDENCE_DIR/test-results/published-scenarios.trx" 2>/dev/null || true)"
attr() { sed -n "s/.* $1=\"\([^\"]*\)\".*/\1/p" <<<"$counters"; }
total="$(attr total)"; passed="$(attr passed)"; failed="$(attr failed)"; skipped=$(( ${total:-0} - ${passed:-0} - ${failed:-0} ))
allowed_skipped=0
if [[ "${GITHUB_ACTIONS:-}" == true ]]; then allowed_skipped="$skipped"; fi
if ! [[ "$total" =~ ^[0-9]+$ && "$total" -ge 1 && "$passed" =~ ^[0-9]+$ && "$skipped" =~ ^[0-9]+$ && "$failed" == 0 ]] ||
   (( passed + allowed_skipped != total )); then
  echo "FAIL: scenario counters total=${total:-none} passed=${passed:-none} failed=${failed:-none} skipped=${skipped:-none}" >&2
  exit 1
fi

{
  echo "binary_kind=$kind"
  echo "binary=$BIN"
  echo "sdk=$("$DOTNET" --version)"
  echo "uname=$(uname -srm)"
  echo "scenarios_total=$total scenarios_passed=$passed scenarios_skipped=$skipped"
  ls -l "$(dirname "$BIN")" | awk 'NR>1{print "file", $5, $9}'
  sha256sum "$BIN" "$(dirname "$BIN")"/*.so 2>/dev/null | sed 's#  .*/#  #'
} > "$EVIDENCE_DIR/published-manifest.txt"
echo "published scenarios passed ($kind, $passed/$total, $skipped skipped); manifest: $EVIDENCE_DIR/published-manifest.txt"
