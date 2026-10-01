#!/usr/bin/env bash
# Gate runner for the pinned .NET 11 SDK. Exits 2 (BLOCKED) if the pinned SDK
# is not the one `dotnet` resolves; never retargets or installs anything.
# Use DOTNET=/path/to/isolated/dotnet to select a project-local SDK; otherwise
# <repo>/.tools/dotnet11/dotnet (found via the Git common dir, so linked
# worktrees share it) is preferred over `dotnet` on PATH.
# The AOT binary is published to a new, unique directory under artifacts/.
set -euo pipefail
# Anchor caller-relative overrides to the caller's cwd (lexically, so a missing
# target stays missing) before changing to the repository root.
abs() { if [[ "$1" == /* ]]; then printf '%s\n' "$1"; else printf '%s\n' "$PWD/$1"; fi; }
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
PIN="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' global.json)"
if [[ -z "${RID:-}" ]]; then
  case "$(uname -s)" in Linux) os=linux ;; Darwin) os=osx ;; *) os= ;; esac
  case "$(uname -m)" in x86_64|amd64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; *) arch= ;; esac
  [[ -n "$os" && -n "$arch" ]] || { echo "BLOCKED: no default RID for $(uname -s) $(uname -m); set RID=" >&2; exit 2; }
  RID="$os-$arch"
fi

actual="$("$DOTNET" --version 2>/dev/null || true)"
if [[ "$actual" != "$PIN" ]]; then
  echo "BLOCKED: pinned SDK $PIN is not active (dotnet --version: '${actual:-none}')" >&2
  exit 2
fi

step() { echo "== $*"; }
MSBUILD_ARGS=(-m:1 -nodeReuse:false -p:UseSharedCompilation=false)
PUBLISH_ARGS=()
# On a Mac with only the Command Line Tools, the ILCompiler's `xcodebuild -version`
# probe prints an error that fails the publish; skip it (it only selects -ld_classic for Xcode 15/16).
if [[ "$RID" == osx-* ]] && ! xcodebuild -version >/dev/null 2>&1; then PUBLISH_ARGS=(-p:UseLdClassicXCodeLinker=false); fi
step "sdk $actual rid $RID dotnet $DOTNET"
step restore;  "$DOTNET" restore AgentTeamForge.slnx "${MSBUILD_ARGS[@]}"
step format;   "$DOTNET" format AgentTeamForge.slnx --verify-no-changes --no-restore
step build;    "$DOTNET" build AgentTeamForge.slnx -c Release --no-restore -warnaserror "${MSBUILD_ARGS[@]}"
ATF_TEST_TMP_ROOT="${ATF_TEST_TMP_ROOT:-$(mktemp -d /tmp/atf-verify-XXXXXX)}"
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
step test;     "$DOTNET" test AgentTeamForge.slnx -c Release --no-build --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type none
step publish-aot
mkdir -p artifacts
PUBLISH_DIR="$(mktemp -d "$ROOT/artifacts/$RID-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
"$DOTNET" publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -c Release -r "$RID" \
  --self-contained true -p:PublishAot=true -p:TreatWarningsAsErrors=true -o "$PUBLISH_DIR" "${MSBUILD_ARGS[@]}" ${PUBLISH_ARGS[@]+"${PUBLISH_ARGS[@]}"}
step "published $PUBLISH_DIR/atf"
step published-smoke
DOTNET="$DOTNET" "$ROOT/scripts/published-smoke.sh" "$PUBLISH_DIR/atf"
step "all gates passed; published binary: $PUBLISH_DIR/atf"
