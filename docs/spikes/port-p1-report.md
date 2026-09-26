# Port P1: job inspection, deterministic paging, post-commit response

Implementation report for slice **P1** of
wave 2 (`docs/spikes/wave2-slices.md` on branch `docs/plan-wave2`). This is a port of already
reviewed legacy code into the canonical root layout. It is not a review approval;
independent opposite-family (Codex) code review of this commit is still required.

## Base and ancestry

- Branch `feature/port-p1-inspection`, created from accepted
  `integration/canonical-wave` commit `2d6d0c9`.
- Import source: `fb53199` (`integration/m0-e2e` tip, which is `cd60824` plus the
  composition build fix). The reviewed features are inspection repair `9fb08b6`,
  deterministic paging `e821e8f` and post-commit response `d714348`, plus the
  original inspection files covered by `git diff 81a11b27 cd60824`.
- Import method: `git show <commit>:spikes/m0-durable-core/<path> > <path>` per
  file. No merge or cherry-pick was used. `git log 2d6d0c9` contains none of
  `fb53199`, `cd60824`, `d714348`, `9fb08b6` or `e821e8f`.
- Pre-condition check: the canonical blob of every modified file at `2d6d0c9` was
  identical to the legacy base blob at `81a11b27`, so no canonical-only change is
  overwritten. The five new files did not exist in the canonical layout.

## Provenance per file

Each file below is byte-identical to `fb53199:spikes/m0-durable-core/<path>`.
The last column gives the version this port reproduces as red before applying the fix.

| Canonical path | Imported blob | Note |
| --- | --- | --- |
| `src/AgentTeamForge.Business/Features/Jobs/ListJobs.cs` | `fb53199` (= `cd60824`) | new |
| `src/AgentTeamForge.DAL/Features/Jobs/JobRecords.cs` | `fb53199` (= `cd60824`) | modified |
| `src/AgentTeamForge.DAL/Features/Jobs/JobStore.cs` | `fb53199` (= `cd60824`) | modified |
| `src/AgentTeamForge.Host/Features/Jobs/ClientCommand.cs` | `fb53199` (= `cd60824`) | modified |
| `src/AgentTeamForge.Host/Features/Jobs/JobsEndpoint.cs` | `fb53199` (= `cd60824`) | modified, 4-arg constructor |
| `src/AgentTeamForge.Host/Features/Jobs/JobsMcpBridge.cs` | `fb53199` (= `cd60824`) | modified |
| `src/AgentTeamForge.Host/Hosting/DaemonCommand.cs` | `fb53199` (= `cd60824`) | modified |
| `src/AgentTeamForge.Host/Transport/IpcMessages.cs` | `fb53199` (= `cd60824`) | modified |
| `tests/AgentTeamForge.Tests/Features/Jobs/AcceptanceOutcomeTests.cs` | `fb53199` | new; `cd60824` version reproduced red first |
| `tests/AgentTeamForge.Tests/Features/Jobs/ListJobsTests.cs` | `fb53199` (= `cd60824`) | new |
| `tests/AgentTeamForge.Tests/Scenarios/AcceptanceOutcomeScenarios.cs` | `fb53199` (= `cd60824`) | new |
| `tests/AgentTeamForge.Tests/Scenarios/JobInspectionScenarios.cs` | `fb53199` (= `cd60824`) | new |
| `tests/AgentTeamForge.Tests/Support/JobFixture.cs` | `fb53199` (= `cd60824`) | modified, adds `List()` |

## TDD sequence

1. Red: after importing all 13 files at `cd60824`, `dotnet build -c Release
   -warnaserror` failed with `CS7036` at `AcceptanceOutcomeTests.cs(60,9)`: there was
   no argument for `checkpoints` in the four-argument `JobsEndpoint(AcceptJob,
   GetJob, ListJobs, DurabilityCheckpoints)` constructor.
2. Green: imported `AcceptanceOutcomeTests.cs` from `fb53199`. That version builds
   the endpoint as `new(f.Accept(), f.Get(), f.List(), checkpoints)`, where
   `f.List()` is `new ListJobs(Store, Operator)`. This was the only change between
   `cd60824` and `fb53199`.

## Preserved coverage

- Acceptance: two unit tests and two scenarios. The post-commit failure returns
  `outcome_unknown`, and a same-key retry returns `existing` with one dispatch
  intent and one accepted signal. A pre-commit failure is a definite error, stores
  nothing, and gets no false `outcome_unknown`.
- `ListJobsTests` is imported unchanged. It covers principal/team binding, bounded
  newest-first static paging, and concurrent-page changes with controlled IDs and
  no duplicates. It also covers rejection of out-of-contract
  status/limit/cursor values, committed-state status filtering, and the absence of
  mutation, dispatch or acknowledgment effects.
- `JobInspectionScenarios` keeps the MCP rejection of present-but-null or
  non-string `status`/`cursor`/`limit`, and `JobListFrameBoundTests`.
- No claim is made of snapshot paging or bounded DB work. The Host/DAL checkpoint
  exception remains **fake-only**, and P1 does not close it. D5 removes it before
  real endpoint promotion.

## Gates (Linux x64, SDK `11.0.100-rc.1.26425.128`, repository-local)

Commands, run from the worktree root:

```
DOTNET=<repo>/.tools/dotnet11/dotnet scripts/verify.sh
ATF_DEMO_BIN=artifacts/linux-x64-20260926T145107Z-HGB3WC/atf DOTNET=<repo>/.tools/dotnet11/dotnet scripts/demo.sh
```

| Gate | Result | Baseline `2d6d0c9` |
| --- | --- | --- |
| restore / format `--verify-no-changes` | Passed | Passed |
| Release build `-warnaserror` | Passed, 0 warnings | Passed |
| Full tests | **78/78**, 0 failed, 0 skipped | 61/61 |
| Native AOT publish | Passed; `atf` native, 9,859,120 bytes, sha256 `11d009e27b3eee2c621bef75132e8ff78d9ab4980bf08798ead008d67efeb259` | 9,788,576 bytes |
| Published process scenarios | **22/22** against the native binary, including `JobInspectionScenarios` (1) and `AcceptanceOutcomeScenarios` (2) | 19/19 |
| Published-binary demo | Passed, 1/1 selected scenario | 1/1 |

The demo reported one leftover `/tmp/atf-*` state directory. It was not deleted,
and its owner was not verified, because concurrent worktrees also run scenarios.

## Not run / open

- Independent Codex code review of this port has not been done yet.
- Windows and macOS were not run. This is Linux x64 evidence only.
- P2 (private reads) and P3 (provenance docs) are separate slices and were not touched.
