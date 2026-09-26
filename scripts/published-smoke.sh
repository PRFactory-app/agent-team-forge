#!/usr/bin/env bash
# Runs the C# process scenarios (daemon, MCP bridge, fake child, crash windows)
# against a supplied binary, then records size/checksum evidence. Uses only
# temporary private state; no credentials or model calls.
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-dotnet}"
if [[ "$DOTNET" == */* ]]; then export DOTNET_ROOT="$(cd "$(dirname "$DOTNET")" && pwd)"; fi
BIN="$(realpath "${1:?usage: published-smoke.sh path/to/atf}")"
[[ -x "$BIN" ]] || { echo "not executable: $BIN" >&2; exit 2; }
if file "$BIN" | grep -q 'ELF' && [[ ! -f "$(dirname "$BIN")/atf.dll" ]]; then kind=native; else kind=jit; fi
mkdir -p evidence
ATF_HOST_BINARY="$BIN" "$DOTNET" test AgentTeamForge.slnx -c Release --no-build \
  --filter "Category=Scenario" --logger "trx;LogFileName=published-scenarios.trx" \
  --results-directory evidence/test-results
{
  echo "binary_kind=$kind"
  echo "sdk=$("$DOTNET" --version)"
  echo "uname=$(uname -srm)"
  ls -l "$(dirname "$BIN")" | awk 'NR>1{print "file", $5, $9}'
  sha256sum "$BIN" "$(dirname "$BIN")"/*.so 2>/dev/null | sed 's#  .*/#  #'
} > evidence/published-manifest.txt
echo "published scenarios passed ($kind); manifest: evidence/published-manifest.txt"
