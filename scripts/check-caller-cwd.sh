#!/usr/bin/env bash
# Regression check: caller-relative overrides must resolve against the caller's
# cwd, never against the repository root. From a fresh caller directory, each
# override names a repo-relative path that exists and is executable in the
# repository but is absent at the caller; every script must refuse it (exit 2)
# and report the caller-anchored absolute path. Nothing is built or run beyond
# the SDK version probe. The caller directory is a new, empty dir under .run/
# (gitignored); nothing is deleted.
# DOTNET must be an absolute path to the pinned SDK (demo.sh probes it first).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[[ "${DOTNET:-}" == /* && -x "$DOTNET" ]] || { echo "BLOCKED: set DOTNET to the absolute pinned SDK path" >&2; exit 2; }
mkdir -p "$ROOT/.run"
CALLER="$(mktemp -d "$ROOT/.run/caller-cwd-XXXXXX")"
DECOY=scripts/verify.sh
[[ -x "$ROOT/$DECOY" && ! -e "$CALLER/$DECOY" ]] || { echo "BLOCKED: decoy precondition failed" >&2; exit 2; }

fails=0
expect() { # name, expected-substring, command...
  local name="$1" want="$2"; shift 2
  local out status=0
  out="$(cd "$CALLER" && "$@" 2>&1 >/dev/null)" || status=$?
  if [[ "$status" == 2 && "$out" == *"$want"* ]]; then
    echo "ok   $name"
  else
    echo "FAIL $name: exit=$status stderr=$out" >&2; fails=$((fails + 1))
  fi
}
expect "demo ATF_DEMO_BIN" "$CALLER/$DECOY" env DOTNET="$DOTNET" ATF_DEMO_BIN="$DECOY" "$ROOT/scripts/demo.sh"
expect "demo DOTNET" "$CALLER/$DECOY" env DOTNET="$DECOY" "$ROOT/scripts/demo.sh"
expect "verify DOTNET" "$CALLER/$DECOY" env DOTNET="$DECOY" "$ROOT/scripts/verify.sh"
expect "smoke DOTNET" "$CALLER/$DECOY" env DOTNET="$DECOY" "$ROOT/scripts/published-smoke.sh" "$DECOY"
expect "smoke binary" "$CALLER/$DECOY" env DOTNET="$DOTNET" "$ROOT/scripts/published-smoke.sh" "$DECOY"
[[ "$fails" == 0 ]] || { echo "caller-cwd check: $fails failed" >&2; exit 1; }
echo "caller-cwd check: 5 passed"
