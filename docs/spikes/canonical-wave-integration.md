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
