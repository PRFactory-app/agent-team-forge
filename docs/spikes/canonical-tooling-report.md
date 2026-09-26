# Canonical tooling relocation report (wave 1B)

**Scope.** Root `scripts/` for the canonical layout approved by
[the consolidation plan](product-core-consolidation-plan.md) (`bf4ffc1`) and its
independent review (`e1d77ea`). This lane owns only `scripts/**` and this
report. Root solution, configs, `src/` and `tests/` belong to the separate
source-lift lane; README/HANDOFF/status pages are not changed here. This is a
tooling input for integration, **not** a final combined candidate: the gates
below ran against a scratch fixture, not against the source lane's tree.

## Provenance

Frozen source: `81a11b27fb67d2ec600ace67586c092829927f8f`,
`spikes/m0-durable-core/scripts/`. The first commit on this branch copies the
three blobs verbatim with mode `100755`; the second applies the relocation
edits, so the review diff is exactly the path/robustness delta.

| Path | Source blob (81a11b2) | Relocated blob | Mode |
| --- | --- | --- | --- |
| `scripts/verify.sh` | `b5115574fb5e9f7423f080b8d7038ffbc1931929` | `89b6a3e92c33a8f8399e02e49d15eb6836d3eb20` | 100755 |
| `scripts/published-smoke.sh` | `48f67a2aa31398a6c9df397cc96056cf0b564e10` | `6b97a7a6bf960b550f92625dc4008ecba2296662` | 100755 |
| `scripts/demo.sh` | `760fe371a366baa042835a6ea4561fc6f79c3ae9` | `b36607a3a74075a95a937979d68cc42e25e961ab` | 100755 |

The canonical root keeps the same relative layout as the durable-core subtree
(`AgentTeamForge.slnx`, `global.json`, `src/AgentTeamForge.Host/...`), so no
project or solution path inside the scripts changed.

## Changes

All three scripts:

- Root is computed from `${BASH_SOURCE[0]}` and the scripts `cd` there, so
  they work from any caller working directory.
- Caller-relative overrides are resolved **before** changing directory:
  `DOTNET` (when it is a path), `ATF_DEMO_BIN`, and the `published-smoke.sh`
  binary argument. Previously a relative value was silently reinterpreted
  against the repository root.
- `DOTNET`, `ATF_HOST_BINARY` (set by smoke/demo for the C# `SpikeRig`),
  `ATF_DEMO_BIN`, `RID`, `ATF_DEMO_TEST_TIMEOUT` and `ATF_DEMO_BUILD_TIMEOUT`
  keep their meanings. The exact-SDK pin check and BLOCKED exit 2 are unchanged.

`verify.sh` and `published-smoke.sh`:

- With `DOTNET` unset they now prefer `<git-common-dir>/../.tools/dotnet11/dotnet`
  (the same discovery `demo.sh` already used; linked worktrees share it) and
  otherwise fall back to `dotnet` on PATH, as before. The pin check still
  rejects any other SDK.
- `verify.sh` publishes to a new `mktemp -d` directory
  `artifacts/<rid>-<utc>-XXXXXX/` instead of reusing `artifacts/<rid>/`, so a
  stale `atf.dll` or older binary cannot contaminate the published gate. It
  prints the binary path for `ATF_DEMO_BIN`. Nothing is deleted.
- `published-smoke.sh` writes TRX and manifest to a new 0700 directory
  `evidence/published-<utc>-XXXXXX/` instead of fixed shared paths, and now
  fails unless the TRX counters show at least one scenario with
  `passed == total` and `failed == 0` (an empty filter match exits 0 from
  `dotnet test`). The manifest also records the scenario counts.

`demo.sh`:

- Run directory is created by `mktemp -d` (unique, 0700) instead of
  `date`+PID with `mkdir -p`.
- The exactly-one guard additionally requires `failed == 0` alongside
  `total == 1`, `passed == 1` and a zero test exit status.

No script uses `rm`, `git clean`, process kills or fixed-path deletion; test
state remains owned by the C# harness. No `.gitignore` change was needed: root
rules already ignore `**/artifacts/`, `**/evidence/`, `**/.run/` and `.tools/`.

## Fixture validation

A scratch fixture under the ignored `.run/canonical-fixture/` of this worktree
was populated with `git archive 81a11b2 spikes/m0-durable-core`, stripping the
subtree prefix and excluding its Markdown reports and old scripts; the
relocated root scripts were then copied in. All 58 extracted files matched
their source blob IDs and modes. The fixture is not committed and imports no
other ancestry. Its Git common directory resolves to the main checkout, which
exercises linked-worktree SDK discovery.

Environment: Linux x86_64, SDK `11.0.100-rc.1.26425.128` (project-local),
PATH `dotnet` is 10.0.401 (would be rejected by the pin).

| Gate | Invocation | Result |
| --- | --- | --- |
| Full verify | `verify.sh` from an unrelated cwd, `DOTNET` unset | exit 0: restore, format, `-warnaserror` Release build (0 warnings), 61/61 tests, linux-x64 Native AOT publish, 19/19 published scenarios |
| Published binary | manifest | `binary_kind=native`, `atf` sha256 `347256621204e37b37f4dbc2be44755521d532009f4fff1af89514535bb8042d`, `libe_sqlite3.so` sha256 `eddcd4aa561d5b8f252db77e8272e7d1aed96bcab9fda3f177ca542f916290bf` |
| Demo, published | `ATF_DEMO_BIN=<relative path>` from `artifacts/` | exit 0, exactly one scenario passed |
| Demo, JIT | `demo.sh` from unrelated cwd | exit 0, one scenario passed |
| Empty-match guard | fixture-only copy of `demo.sh` with a nonexistent scenario | `dotnet test` exit 0, total=0 → script exit 1 FAIL |
| Wrong SDK | `DOTNET=dotnet` (10.0.401) for `verify.sh` / `demo.sh` | exit 2 BLOCKED |
| Missing binary | `ATF_DEMO_BIN=nope` | exit 2 BLOCKED, path resolved against caller cwd |
| Relative overrides | `DOTNET=.tools/...` and relative binary for `published-smoke.sh` from the main checkout | exit 0, 19/19 |
| Leftover state | demo before/after listing | no new `/tmp/atf-*` directories |

The 61/19 counts reproduce the recorded `81a11b2` checkpoint; they are
evidence for that source only, not expected totals for another candidate.

## Not run or not established

- `shellcheck` is not installed; only `bash -n` syntax checks and
  `git diff --check` were run. No package was installed.
- Clean (non-worktree) checkout discovery was not exercised; it relies on the
  same `git-common-dir` expression resolving to `<repo>/.git`.
- The scripts have not run against the source lane's canonical root. The
  integrator must rerun `scripts/verify.sh` and a published-binary
  `scripts/demo.sh` on the combined tree, and check legacy config inheritance.
- Job inspection (post-`81a11b2`) is excluded; its gates remain pending.
- Linux fake-core results do not qualify real agents, interactive terminals,
  native wake, Windows or macOS.
- Opposite-family (Codex) review of this diff is still required.
