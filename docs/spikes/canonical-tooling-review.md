# Canonical tooling review

**Verdict: CHANGES-REQUIRED.** Independent Codex review of Claude-authored
`7bd020a` on `review/canonical-tooling`, against `main` `efcb374` and the
approved source lift `3f0b0c3` (source review `a86dbe4`). The format, build,
test, Native AOT, published-scenario and published-demo gates passed on a
scratch, uncommitted merge of `3f0b0c3` + `7bd020a`. One path-identity defect
blocks approval of the promised caller-cwd independence.

## Findings

### Blocking

1. `scripts/demo.sh:23-25,43-46` — A failed `realpath` of a caller-relative
`ATF_DEMO_BIN` or path-shaped `DOTNET` falls back to the original relative
string. The script then changes to the repository root, so a missing caller
path can resolve to a different executable in the repository. Reproduction
from `/tmp`: `realpath scripts/verify.sh` exits 1 because that caller path is
absent, while the scratch root's `scripts/verify.sh` is executable. Thus
`ATF_DEMO_BIN=scripts/verify.sh` from `/tmp` passes the executable guard for
the wrong file. The script should preserve the caller's absolute path even
when its target is missing, or fail before `cd`; it must never reinterpret
an override relative to the repository. Re-review the fix and its regression
evidence.

### Non-blocking

- No other findings in the reviewed delta. The demo reported one new
`/tmp/atf-*` directory immediately after its passing scenario; it had already
disappeared when inspected seconds later. No persistent leftover was observed.

## Independent gates

Scratch worktree:
`/home/mikael/code/github/agent-team-forge/.worktrees/tooling-review-scratch`,
created detached at `3f0b0c3`; `git merge --no-ff --no-commit 7bd020a`
completed without conflicts. No production source was edited. All .NET
invocations used only the isolated SDK
`/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`
(`11.0.100-rc.1.26425.128`), with command-scoped `DOTNET_ROOT` and `PATH`.

```bash
# From the scratch worktree:
bash -n scripts/verify.sh scripts/published-smoke.sh scripts/demo.sh
# From the review worktree:
git diff --check efcb374..7bd020a -- scripts docs/spikes/canonical-tooling-report.md
# From /tmp (unrelated caller cwd):
env DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet DOTNET_ROOT=/home/mikael/code/github/agent-team-forge/.tools/dotnet11 PATH=/home/mikael/code/github/agent-team-forge/.tools/dotnet11:$PATH /home/mikael/code/github/agent-team-forge/.worktrees/tooling-review-scratch/scripts/verify.sh
# From the scratch worktree artifacts/ directory:
env DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet DOTNET_ROOT=/home/mikael/code/github/agent-team-forge/.tools/dotnet11 PATH=/home/mikael/code/github/agent-team-forge/.tools/dotnet11:$PATH ATF_DEMO_BIN=./linux-x64-20260926T144501Z-1fmSKB/atf /home/mikael/code/github/agent-team-forge/.worktrees/tooling-review-scratch/scripts/demo.sh
```

| Gate | Result |
| --- | --- |
| `bash -n`; `git diff --check` | Passed |
| Restore; `dotnet format --verify-no-changes --no-restore` | Passed |
| Release build `-warnaserror` | Passed, 0 warnings and 0 errors |
| Full tests | 61 passed, 0 failed, 0 skipped |
| `linux-x64` Native AOT publish | Passed; new directory `artifacts/linux-x64-20260926T144501Z-1fmSKB/` |
| Published binary scenarios | 19 passed, 0 failed, 0 skipped; manifest identifies `binary_kind=native` |
| Published binary demo, caller-relative `ATF_DEMO_BIN` | 1 passed, 0 failed; exit 0 |

The published binary SHA-256 was
`273d6e15cedd613fcb253499c6ba58649939061d68c4167a3552aa192a702a58`.
The scenario manifest is in the scratch tree at
`evidence/published-20260926T144511Z-4vjsxU/published-manifest.txt`; demo
TRX and logs are at `.run/demo-20260926T144528Z-F68OMa/`. These gates
qualify only the Linux fake-core checkpoint, not other platforms or real agents.

The nonempty TRX guard was inspected in the diff but not independently
fault-injected; the positive published and demo runs each produced a nonempty
TRX with the counts above. `shellcheck` was not run because it is unavailable.

## Re-review of caller-cwd fix

**Verdict: APPROVED.** Independently reviewed Claude commit `68a5e6b` against
`7bd020a`, then merged it into this review branch at `5e43d73`. The blocking
fallback is removed: path-shaped `DOTNET`, `ATF_DEMO_BIN`, and the published
binary argument are anchored lexically to the caller's cwd before any `cd`.
Missing caller paths therefore remain missing and return exit 2 with the
caller-anchored path. The new `check-caller-cwd.sh` covers the original decoy
case in `demo.sh` and four corresponding script/override cases. I found no new
blocking or non-blocking issue in the fix. The earlier transient `/tmp/atf-*`
observation remains non-blocking; the re-review demo reported no leftovers.

I tested an uncommitted scratch merge of `68a5e6b` into
`integration/canonical-wave` base `2d6d0c9` at
`.worktrees/tooling-rereview-scratch`. All .NET commands used isolated SDK
`/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`
(`11.0.100-rc.1.26425.128`). I ran `verify.sh` and the published demo from
`/tmp`; the demo received both `DOTNET` and `ATF_DEMO_BIN` as caller-relative
paths. Results on Linux 7.2.5-3-omarchy x86_64:

| Gate | Result |
| --- | --- |
| `git diff --check 7bd020a..68a5e6b`; `bash -n` on all four scripts | Passed |
| Caller-cwd decoy regression script | 5/5 passed; all rejected missing caller paths with exit 2 |
| Restore and `dotnet format --verify-no-changes --no-restore` | Passed |
| Release build `-warnaserror` | Passed, 0 warnings and 0 errors |
| Full tests | 61/61 passed, 0 failed, 0 skipped |
| `linux-x64` Native AOT publish | Passed; binary is native, 9,788,576 bytes |
| Published-binary process scenarios | 19/19 passed, 0 failed, 0 skipped |
| Published-binary demo from unrelated caller cwd | 1/1 passed; no new state dirs reported |

The AOT binary SHA-256 was
`74817e19d0b37e96a323150551d0799d6149c09f9d4518dc5f9f0ff61f6b0f9f`.
The ignored scratch evidence is in
`evidence/published-20260926T145635Z-VeH8Ws/` and
`.run/demo-20260926T145649Z-200DLT/`. `shellcheck` remains unavailable.
These gates approve the Linux fake-core tooling delta only; they do not qualify
real-agent or other-platform behavior.
