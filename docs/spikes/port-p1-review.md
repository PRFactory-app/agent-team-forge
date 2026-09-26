# Independent review: P1 root-layout port

2026-09-26. Claude-authored source commit `4d3fdfa9738cbb84c6494aa79355296793e882cd`, reviewed against its canonical base `2d6d0c900ff436efce0aa81b9069771f58f8913f` in a separate Codex session. The review covered the actual root-layout diff, the relevant Business/DAL/Host paths and tests, the earlier legacy reviews, and the published-binary gates below.

**Verdict: approve this port for integration; zero blocking findings.** This approves the reviewed Linux fake-core checkpoint, not full product or platform support. The merge with current `main` still needs its own combined gates.

## Port and coverage checks

- All **13 changed source/test files** have the same Git blob ID at `4d3fdfa:<root path>` as at `fb53199:spikes/m0-durable-core/<path>`. The legacy commits `9fb08b6`, `e821e8f`, `d714348`, and tip `fb53199` are absent from this branch's ancestry. The source/test import has no omitted or weakened test edit relative to that integrated legacy tip.
- `JobsEndpoint` takes four arguments: `AcceptJob`, `GetJob`, `ListJobs`, and `DurabilityCheckpoints`. Both daemon composition and `AcceptanceOutcomeTests.Endpoint` supply all four; the latter uses `f.List()` with the bound operator.
- All four acceptance checks are present: two unit tests and two published-process scenarios cover post-commit `outcome_unknown`, same-key `existing` recovery with one intent/signal or invocation, and definite pre-commit failure. `ListJobsTests` retains identity binding, static and concurrent keyset paging, invalid requests, committed-state filtering, and read-only behavior. `JobInspectionScenarios` retains eight malformed MCP argument cases (including present null or wrong-type fields), valid MCP/CLI listing, and invocation-count checks; the frame fixture test remains scoped to short fake reason codes.
- The listing query filters the daemon-bound principal and team and returns only summary fields in a read-only, limit-plus-one keyset query. Its contract correctly states live, best-effort paging rather than snapshot completeness.

## Main integration check

Current `main` is `d7d24ae`. Since the common base `2d6d0c9`, main's P2 private-read work changes `Native.cs`, `StateDirectory.cs`, and its dedicated tests; D2 changes Herdr characterization and fixture files. None overlap P1's source/test paths. `git merge-tree --write-tree main HEAD` succeeded without textual conflicts and produced tree `3cffc824c0c4f666b502c076010b7c3a309ce062`. That is a conflict check, not a combined build or test. The integrator should run the combined root gates after merging.

## Reproduced gates

Linux x86_64, repository-local .NET SDK `11.0.100-rc.1.26425.128` selected with `DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`:

| Gate | Independent result |
| --- | --- |
| `scripts/verify.sh`: restore, format `--verify-no-changes` | Passed |
| Release build `-warnaserror` | Passed, 0 warnings / 0 errors |
| Full tests | **78/78**, 0 failed, 0 skipped |
| Linux x64 Native AOT publish | Passed; native `atf`, 9,859,120 bytes; SHA-256 `a28caf2e6b80ef8f66a7909e0833602c8e6ee15e158e49ca9f9475e616ea7800` |
| Published-binary process scenarios | **22/22**, 0 failed, 0 skipped; manifest in ignored `evidence/published-20260926T150726Z-vFlmet/` |
| Published-binary `scripts/demo.sh` | **1/1** selected client-lifetime scenario; evidence in ignored `.run/demo-20260926T150737Z-h2M29P/` |
| `git diff --check 2d6d0c9...HEAD` | Passed |

The demo reported three newly observed `/tmp/atf-*` directories. Their ownership was not established, so they were left untouched. They did not fail the selected scenario.

## Accepted limits and follow-up

- P1 bounds output rows, but the schema has no `(principal, team, job_id)` access path, so total database work is not bounded. The 64-character reason-code frame test does not prove a general outgoing-frame bound. These are the previously accepted nonblocking fake-core limits from `m0-job-inspection-rereview.md`.
- The post-commit checkpoint tests classify a failure *after a successful commit*. They do not establish the outcome of every SQLite `COMMIT` error; a same-key retry remains the recovery path for an uncertain commit. This is the earlier nonblocking qualification from `m0-acceptance-outcome-review.md`.
- Windows/macOS, real agents, interactive terminals, native wake, and the merged P1+P2+D2 tree were not tested in this review. No claim is made for those gates.
