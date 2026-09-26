# Independent review: M0 `job_list` inspection

2026-09-26. Source snapshot `d54ff3a53e3550507d12e22bc02e04f240da0551`,
diffed against base `55d3c054acf072f7d3da49c5f9bd20d9c3860058`.
Claude-authored slice, GPT/Codex reviewer. Report reviewed:
`spikes/m0-durable-core/JOB-INSPECTION-REPORT.md`. Base-core B1/B2 review at
`1d3c409` is independent; this review does not approve that base.

**Verdict: changes required before advertising bounded, complete inspection.**
The authorized read-only CLI/IPC/MCP path works in the tested single-operator
fake core, and its positive and negative tests pass. Findings J1–J3 separate
overstated documentation from executable contract gaps. No production or test
source was changed by the reviewer.

## Findings

**J1 — Contract overstatement; decide snapshot versus best-effort paging.**
`JOB-INSPECTION-REPORT.md:45-49` says a cursor scan returns every job present
at its start exactly once and later jobs appear only on a new first page.
`src/AgentTeamForge.DAL/Features/Jobs/JobStore.cs:246-253` instead pages each
independent current `SELECT` by `job_id < cursor`, with no snapshot/high-water
token. `JobStore.cs:43` constructs `job_` + UUIDv7. A bounded .NET 11
`Guid.CreateVersion7()` sample in ignored scratch artifacts found a later ID
lexically lower than its predecessor in the **same millisecond after two
generated IDs**. Such an insertion can appear on a later page. Concurrent
status changes also alter filtered membership: a job initially matching can
leave the filter before its page, or a previously nonmatching job can enter.
The test at `tests/AgentTeamForge.Tests/Features/Jobs/ListJobsTests.cs:27-48`
tests only a static dataset.

This is **a documentation/contract fix if the intended API is best-effort live
keyset paging**: say results reflect each page's committed read, stable rows
are not duplicated by the strict cursor, and concurrent acceptance/status
changes may be included or missed. Remove the complete-scan and “later only
on first page” promises. If an exhaustive start-of-scan result is required,
the implementation needs an explicit snapshot or stable membership contract
and a deterministic concurrency test; wording alone cannot supply it.

**J2 — Bounded response count, unbounded database work.**
`JobStore.cs:245-254` filters principal/team/status and sorts by job ID with
`LIMIT 51`, but schema `src/AgentTeamForge.DAL/Migrations/Schema.cs:15-30`
has no `(principal, team, job_id)` access path. A read-only SQLite
`EXPLAIN QUERY PLAN` on the equivalent schema selected the existing
principal/team unique-index prefix and a **temporary B-tree for ORDER BY**.
Thus one request may scan/sort all jobs for the bound operator; the 50-row
response cap does not bound DB work or latency. The correlated run count is
per returned row. The author's report acknowledges the missing index, but
its “bounded” headline and `JobStore.cs:239` should be qualified as
**output-bounded only** until a reviewed migration/index or another work bound
is provided. For a deliberately small fake checkpoint this may be an accepted
nonblocking limitation if explicitly stated; it is not a scalable operator
inspection guarantee. No large-database latency test was run.

`JOB-INSPECTION-REPORT.md:56-58` also generalizes a synthetic frame-size
test beyond what schema enforces: `reason_code` is SQLite `TEXT` with no length
constraint, and `Host/Transport/Frames.cs:11-19` has no outgoing frame cap.
The current fake dispatcher writes short fixed reason codes, so this does not
break the demonstrated profile. Keep the `< 1/4 frame` claim scoped to that
tested fixture/current writers; a general response guarantee would need a
field/serialization bound.

**J3 — Malformed MCP string fields can be silently omitted.**
`src/AgentTeamForge.Host/Features/Jobs/JobsMcpBridge.cs:77-83,103-105`
maps a present non-string `status` or `cursor` to `null`. Business then treats
null as “no filter” or “first page” (`ListJobs.cs:18-30`). A malformed
`limit` is correctly mapped to 0 and rejected; CLI invalid limits are also
rejected. The advertised MCP input schema may cause some clients to reject
bad types, but this handler does not enforce it; if such arguments reach it,
it returns a broader/different successful list instead of an error. The
published scenario tests valid MCP inputs and invalid CLI limits only
(`JobInspectionScenarios.cs:35-58`). Add direct MCP malformed-type cases and
reject present invalid values at the bridge or Business boundary. This is an
executable mapping defect, not an authorization bypass: every result still
passes the bound principal/team filter.

## Positive review and reproduced gates

- `Host/Transport/IpcServer.cs:73-109,130-147` authenticates an owner-UID
  client with the private credential before dispatch. `JobsEndpoint.cs:22-29`
  maps `job_list` to Business, which supplies the daemon-configured principal
  and team; SQL filters both (`JobStore.cs:248`). Request-supplied identity
  cannot broaden this query. The Business test excludes other principal/team
  rows. A foreign cursor only changes the caller's bound keyset and does not
  return or count the foreign row.
- `JobStore.cs:245-264` is one SELECT with parameters and no writes; the
  Business result carries summaries without instruction/result payload.
  Tests snapshot jobs, runs, events and unattempted intents before/after
  listing; the real scenario preserves fake invocation counts. `has_more`
  and `next_cursor` are consistent for the page's own read.
- CLI, IPC response/source-generated JSON, endpoint, MCP registration and
  daemon composition are wired end to end. Valid status/limit/cursor paths
  and invalid CLI limits were exercised with the published binary.

Scratch worktree: exact base + demo cherry-pick `9ce61e7` + inspection
cherry-pick `cde9d20`. Pinned .NET 11 RC1 SDK
`11.0.100-rc.1.26425.128`, Linux x86_64. `scripts/verify.sh` passed restore,
format, warnings-as-errors Release build (0 warnings), **49/49 tests**,
Native AOT publish and **14/14 published process scenarios**. A separate
published `JobInspectionScenarios` run passed **1/1**. No real models, Herdr,
Windows/macOS, service installation or performance qualification occurred.
These green gates establish the tested static and process paths; they do not
establish the complete-scan or DB-work claims above.
