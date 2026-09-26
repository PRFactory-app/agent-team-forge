#!/usr/bin/env bash
# Gate runner for the pinned .NET 11 SDK. Exits 2 (BLOCKED) if the pinned SDK
# is not the one `dotnet` resolves; never retargets or installs anything.
# Use DOTNET=/path/to/isolated/dotnet to select a project-local SDK.
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-dotnet}"
if [[ "$DOTNET" == */* ]]; then export DOTNET_ROOT="$(cd "$(dirname "$DOTNET")" && pwd)"; fi
PIN="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' global.json)"
RID="${RID:-linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')}"

actual="$("$DOTNET" --version 2>/dev/null || true)"
if [[ "$actual" != "$PIN" ]]; then
  echo "BLOCKED: pinned SDK $PIN is not active (dotnet --version: '${actual:-none}')" >&2
  exit 2
fi

step() { echo "== $*"; }
step "sdk $actual rid $RID"
step restore;  "$DOTNET" restore AgentTeamForge.slnx
step format;   "$DOTNET" format AgentTeamForge.slnx --verify-no-changes --no-restore
step build;    "$DOTNET" build AgentTeamForge.slnx -c Release --no-restore -warnaserror
step test;     "$DOTNET" test AgentTeamForge.slnx -c Release --no-build
step publish-aot
"$DOTNET" publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -c Release -r "$RID" \
  --self-contained true -p:PublishAot=true -p:TreatWarningsAsErrors=true -o "artifacts/$RID"
step published-smoke
DOTNET="$DOTNET" ./scripts/published-smoke.sh "artifacts/$RID/atf"
step "all gates passed"
