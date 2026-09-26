# Canonical source and tooling integration

## Scope and inputs

This is the combined Linux fake-core checkpoint on `integration/canonical-wave`,
created from `main` at `efcb374809e1ed51d84b8d5c7a5cbbc1308c7005` in the
dedicated `.worktrees/canonical-wave` worktree. It does not promote the result
to `main` or claim a real-agent, interactive-terminal, native-wake, Windows, or
macOS capability.

| Input | Source commit | Integration merge commit |
| --- | --- | --- |
| `feature/canonical-core-source` | `3f0b0c390a3ca3b519bbac095739b0b8ca315adf` | `4603b040194cf2d929005a42d741b321e2742a4b` |
| `feature/canonical-core-tooling` | `7bd020a86d46a5d4e96760c4a8246ca01ba812c9` | `29bc27d61be018b45e39899221342a1259bb4e9d` |

Both merges used `git merge --no-ff --no-edit`. They completed without textual
or semantic conflicts. No legacy spike or `integration/m0-e2e` branch was merged.
The source lift was independently approved at `a86dbe4`; the tooling review
was still in progress at integration time. These passing gates are **not**
tooling review approval. Promotion must wait for that review and any required
fixes and re-review.

## Fresh combined gates

Run from `.worktrees/canonical-wave` on Linux 7.2.5-3-omarchy x86_64 with the
isolated SDK `11.0.100-rc.1.26425.128`. The exact commands were:

```bash
DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet ./scripts/verify.sh
DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet ATF_DEMO_BIN=/home/mikael/code/github/agent-team-forge/.worktrees/canonical-wave/artifacts/linux-x64-20260926T144521Z-4AJmJo/atf ./scripts/demo.sh
git diff --check
bash -n scripts/verify.sh scripts/published-smoke.sh scripts/demo.sh
```

`verify.sh` ran root solution restore, `dotnet format --verify-no-changes
--no-restore`, Release build with `-warnaserror`, full tests, Linux x64 Native
AOT publish with warnings as errors, and published-binary process scenarios.

| Gate | Actual result |
| --- | --- |
| Restore | Passed, four projects restored |
| Format check | Passed |
| Release build | Passed, 0 warnings, 0 errors |
| Full tests | Passed, **61/61**, 0 failed, 0 skipped |
| Native AOT publish | Passed; published `atf` is native, 9,788,576 bytes |
| Published process scenarios | Passed, **19/19**, 0 failed, 0 skipped |
| Published-binary demo | Passed, **1/1** selected scenario |
| `git diff --check`; `bash -n` | Passed |

The published binary was
`artifacts/linux-x64-20260926T144521Z-4AJmJo/atf` (SHA-256
`866841a6936380bb2f06c7d7865ec34b1d23b318a482ae1c55adf59a03aeeb22`).
The published-scenario manifest is in the ignored
`evidence/published-20260926T144532Z-pNfzKv/` directory. The demo TRX and logs
are in the ignored `.run/demo-20260926T144542Z-WBil6q/` directory. The demo
printed a warning about a new `/tmp/atf-409c33debe` state directory; it no
longer existed on immediate read-only inspection. No cleanup was performed.

These checks establish this combined fake-core checkpoint on Linux only.
Legacy interactive tests, real backends, Windows/macOS behavior, and publication
gates were not run or approved by this integration.

## Tooling fix integration after independent re-review

The Claude-authored caller-cwd fix `68a5e6b` was independently approved by
Codex in `review/canonical-tooling` at `74f0f53`. The fix branch merged into
`integration/canonical-wave` at `b547d99`; the review branch merged at
`9d8d783`. Both merges were conflict-free. The review records the resolved
blocking finding and the scratch-merge gate evidence.

I reran all combined gates from this integration worktree on Linux
7.2.5-3-omarchy x86_64 with the isolated
`/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`
(`11.0.100-rc.1.26425.128`). `verify.sh`, `check-caller-cwd.sh`, and the
published demo were invoked from `/tmp`; the demo used caller-relative `DOTNET`
and `ATF_DEMO_BIN` paths.

```bash
# From the canonical-wave worktree:
git diff --check 2d6d0c9..HEAD
bash -n scripts/verify.sh scripts/published-smoke.sh scripts/demo.sh scripts/check-caller-cwd.sh
# From /tmp:
DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet /home/mikael/code/github/agent-team-forge/.worktrees/canonical-wave/scripts/check-caller-cwd.sh
DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet /home/mikael/code/github/agent-team-forge/.worktrees/canonical-wave/scripts/verify.sh
# Still from /tmp, using caller-relative SDK and binary paths:
DOTNET=../home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet ATF_DEMO_BIN=../home/mikael/code/github/agent-team-forge/.worktrees/canonical-wave/artifacts/linux-x64-20260926T145755Z-0IzbQH/atf /home/mikael/code/github/agent-team-forge/.worktrees/canonical-wave/scripts/demo.sh
```

| Gate | Actual result |
| --- | --- |
| `git diff --check`; `bash -n` | Passed |
| Caller-cwd decoy regression | 5/5 passed; missing caller paths rejected |
| Restore and format check | Passed |
| Release build `-warnaserror` | Passed, 0 warnings and 0 errors |
| Full tests | 61/61 passed, 0 failed, 0 skipped |
| Linux x64 Native AOT publish | Passed; native `atf`, 9,788,576 bytes |
| Published-binary process scenarios | 19/19 passed, 0 failed, 0 skipped |
| Published-binary demo | 1/1 passed; no new state dirs reported |

The new AOT binary is in ignored directory
`artifacts/linux-x64-20260926T145755Z-0IzbQH/` (SHA-256
`b79406d24060b3f13ef23f2a7d5fda18b7e2e61008467e1a50833d020c022893`).
The scenario manifest is in ignored
`evidence/published-20260926T145807Z-mNsOnP/`; demo TRX and logs are in
`.run/demo-20260926T145817Z-hDN7yh/`. `shellcheck` was unavailable. This
requalification covers the Linux fake-core checkpoint only.

## P2 private reads and D2 Herdr characterization

The two Claude-authored slices and their independent Codex review records were
merged without squash or conflicts, in this order:

| Input | Source commit | Integration merge commit |
| --- | --- | --- |
| `feature/port-p2-private-reads` | `a7fbea1` | `970ac18` |
| `review/port-p2` (approval) | `5033256` | `c0c3896` |
| `feature/char-d2-herdr-launch` | `fc5e37f` | `30ac16d` |
| `review/char-d2` (approval) | `d2ef9cc` | `40d7ebb` |

The P2 review approved Linux x64 descriptor-bound private reads with two
non-blocking source limitations recorded in `port-p2-review.md`. The D2 review
approved only offline, test-only Herdr launch characterization; no live Herdr
adapter or platform behavior was approved. The combined diff from `c39124e`
adds these slices and their review records without altering the reviewed
tooling fix. `git diff --check c39124e..40d7ebb` and `bash -n` on all four root
scripts passed.

On Linux 7.2.5-3-omarchy x86_64, with
`DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`
(`11.0.100-rc.1.26425.128`), I ran root `scripts/verify.sh`, then
`ATF_DEMO_BIN=<published atf> scripts/demo.sh` using the same SDK. The first
`verify.sh` run passed restore, format, warning-free Release build, all **76/76**
managed tests, and Native AOT publish, but one of **22** published scenarios
failed: `Dispatcher_fault_stops_admission_instead_of_leaving_a_ready_daemon`
received a null outcome where it expected `accepted`. Its result was **21/22**.
I did not change source or tests. A direct rerun of `published-smoke.sh` against
that same binary passed **22/22**, and its published-binary demo passed **1/1**.

A fresh, complete `scripts/verify.sh` then passed restore, format, Release build
with 0 warnings and 0 errors, **76/76** full tests (0 failed/skipped), Linux x64
Native AOT publish, and **22/22** published process scenarios (0 failed/skipped).
The published-binary demo against this second binary passed **1/1**. This
intermittent published-scenario failure remains a test reliability follow-up;
the successful reruns do not erase the first result.

The final native `atf` is in ignored
`artifacts/linux-x64-20260926T150211Z-EXAuQJ/` (9,792,752 bytes; SHA-256
`16804f207f70a783ecb253d4d7d25d35295979d040fb2532c8d4c1384cbef61b`).
Its scenario manifest is in ignored
`evidence/published-20260926T150222Z-c7246Z/`; final demo logs and TRX are in
ignored `.run/demo-20260926T150233Z-WpVrX8/`. The failed first run's TRX is in
`evidence/published-20260926T150113Z-vqfU6A/`. All of this qualifies only the
Linux fake-core checkpoint.
