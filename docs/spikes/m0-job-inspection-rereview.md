# Independent rereview: M0 `job_list` inspection fixes

2026-09-26. Claude-authored fix commit
`9fb08b6263a2e30e4eb04423e2ecababf99eb562`, reviewed against inspection
base `d54ff3a53e3550507d12e22bc02e04f240da0551` in a separate Codex
reviewer session. Inputs were `AGENTS.md`, the original J1–J3 review supplied
for this rereview, the actual diff, and
`spikes/m0-durable-core/JOB-INSPECTION-FIXES.md` and related source/tests.

**Verdict: approve the bounded `job_list` inspection checkpoint for later
integration.** J1 and J3 are addressed; J2 is explicitly retained as a
nonblocking limitation of this small fake-backend checkpoint. This verdict does
not approve scalable production inspection, the full phase, or the base-core
F1 work handled separately at `a8e4352`.

## Finding dispositions

- **J1 — resolved.** The report and Business/DAL comments now describe
  independent, committed, best-effort live keyset pages. They remove the
  start-of-scan completeness and later-insert exclusion promises. The query
  remains a strict `job_id < cursor` read; a stable, matching row present from
  the start is returned once in a completed scan, while concurrent acceptance
  and status changes can be included or missed. The static and concurrent-change
  tests exercise the stated behavior. There is no snapshot or high-water mark.
- **J2 — accepted, nonblocking for this checkpoint.** At most 50 summaries are
  returned, but the schema has no `(principal, team, job_id)` access path, so
  database scan/sort work and latency are not bounded. The report and fix notes
  say this plainly. The frame-size result is scoped to the 64-character
  reason-code fixture/current short fake-writer codes; `reason_code` and
  outgoing frame size have no general bound. No index, migration, scale test,
  or production throughput claim is included in this approval.
- **J3 — resolved.** The MCP bridge distinguishes absent `status`/`cursor` from
  present null or wrong JSON types and returns `invalid_request` for the latter
  without sending IPC. Present malformed `limit`, including null, maps to an
  invalid value rejected by Business. The published-process scenario calls the
  real MCP bridge with eight malformed/null field cases and verifies error
  responses with no page. Valid MCP listing is also exercised.

Nonblocking test caveat: the concurrent-change test asserts that its later
accepted job is absent. A same-millisecond UUIDv7 can sort below the cursor,
so inclusion is also permitted by the documented contract. This assertion
passed here but may be timing-sensitive; make that test condition deterministic
if it becomes unstable. It does not alter the contract or this checkpoint's
verdict.

## Independent gates and limits

Linux x86_64, pinned SDK `11.0.100-rc.1.26425.128` from the main checkout's
`.tools/dotnet11/dotnet`; no SDK or package install. In
`spikes/m0-durable-core`, `DOTNET=<main checkout>/.tools/dotnet11/dotnet
./scripts/verify.sh` passed restore, `dotnet format --verify-no-changes`,
Release build with `-warnaserror` (0 warnings, 0 errors), **50/50 tests**,
`linux-x64` Native AOT publish with warnings as errors, and **14/14 scenarios**
against the published native binary. An independent focused
`ATF_HOST_BINARY=artifacts/linux-x64/atf dotnet test ... --filter
FullyQualifiedName~JobInspectionScenarios` run against that binary passed
**1/1**. `git diff --check d54ff3a 9fb08b6` passed. Only this review document
was changed by the reviewer.

Not run or established: Windows/macOS, real agents or models, Herdr,
interactive terminals, service installation, large-database performance,
general outgoing-frame bounds, or F1 integration. The published tests cover
the Linux fake-backend process profile and this inspection slice only.
