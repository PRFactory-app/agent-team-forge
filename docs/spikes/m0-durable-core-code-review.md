# Independent code review: M0 durable fake core

## Snapshot, scope, and verdict

**Reviewed source commit:** `55d3c054acf072f7d3da49c5f9bd20d9c3860058`
on `spike/m0-durable-core` (parent `0e71ef22aad008961c2eb3504f6e2cc07734468d`).
The reviewed `spikes/m0-durable-core/REPORT.md` blob is
`9049a6c6f3084d4710d05d97ce00ce700eb2a711`. This is an independent
Codex/GPT review of Claude-written code, against the approved bounded S1–S4
[plan](m0-durable-core-plan.md), its [plan review](m0-durable-core-plan-review.md),
and [architecture](../architecture.md). It covers only the Linux fake backend,
not real Codex/Herdr, Claude, Pi, wake, F27/web, or full phase exits.

**Verdict: CHANGES REQUIRED before integration/promotion.** The core implements
the main durable transaction order and passes its local gates, but the dispatcher
can silently stop after a routine invalid test-profile deadline or an unexpected
backend failure while the daemon continues accepting work. The maximum fake
runtime also starts after process spawn and instruction delivery, leaving that
external-effect boundary unbounded. These are blocking S2/S4 correctness gaps.
Return the executable changes to a fresh Claude writer, then re-review the exact
fix commit and rerun gates. Do not merge this commit into `integration/m0-e2e` as
an accepted core. The unsolved real-adapter TUI/thread and human-race gates are
separate and do not by themselves block this fake-core review.

## Blocking findings

### B1 — Dispatcher failure is hidden while acceptance continues

`spikes/m0-durable-core/src/AgentTeamForge.Business/Features/Jobs/DispatchJob.cs:55,64-68,79-83`
lets an exception from `backend.Start`, `CancelAfter`, or evidence reading escape
the dispatch loop. It catches only `BackendNotStartedException` around start and
`OperationCanceledException` around read. The host then attaches an empty
continuation at
`spikes/m0-durable-core/src/AgentTeamForge.Host/Hosting/DaemonCommand.cs:93-95`,
which discards a faulted dispatcher task and keeps the IPC server running.
`AcceptJob` can continue acknowledging new queued jobs, but no worker remains to
run them. This breaks the daemon-owned progression claim and leaves no explicit
failed/blocked health signal. The corresponding attempted run may stay `running`
until a restart that the still-live daemon does not initiate.

A reachable input is `init --max-runtime-seconds -2`: the CLI accepts and writes
it (`.../Features/Setup/InitCommand.cs:14,33-34`), profile loading does not
validate it (`.../Features/Setup/SpikeProfileFile.cs:21-30,34-44`), and
`CancellationTokenSource.CancelAfter` then throws for the negative duration.
This is source-path analysis; the current scenarios do not exercise that value.
Unexpected process/pipe failures have the same task-fault consequence.

**Required:** Validate positive, bounded profile values before the daemon
accepts work. Make unanticipated post-attempt failures conservatively terminal
or explicitly quarantined/blocked, and surface a dispatcher fault instead of
ignoring it. If a terminal write fails, halt new claims as the approved plan
requires. Add one focused regression proving that a bad deadline or backend
fault cannot leave a ready-looking daemon accepting jobs with no dispatcher.
Do not turn an ambiguous started attempt into `failed` or requeue it.

### B2 — Maximum runtime excludes spawn and instruction delivery

`spikes/m0-durable-core/src/AgentTeamForge.Business/Features/Jobs/DispatchJob.cs:64-80`
creates its deadline only after synchronous `backend.Start` returns.
`spikes/m0-durable-core/src/AgentTeamForge.Business/Features/Agents/Backends/FakeProcessBackend.cs:35-47,59-69`
starts the child and synchronously writes/flushes the request before returning.
A stalled child read, pipe write or process start can therefore keep the only
dispatcher blocked indefinitely after the durable attempt-start commit. The
declared `MaxFakeRuntime` never fires, and queued work cannot progress. Existing
tests cover a child that hangs **after** receiving and acknowledging the request,
not this pre-read/write boundary.

**Required:** Bound the full effect operation, including process start and
delivery, with an explicit uncertain-write classification. A timeout after a
possible spawn/write must not be labelled `backend_not_started` or retried.
For this fake-only policy, termination is limited to its held direct-child
handle; any possible surviving child or failed termination stays uncertain.
Add a deterministic fake-child stalled-before-read/delivery scenario with a
bounded test supervisor. No real agent process is needed.

## Non-blocking qualifications and follow-ups

1. `spikes/m0-durable-core/src/AgentTeamForge.Host/Transport/IpcClient.cs:14-20,28-40`
   applies timeouts to reads but not to connect or writes. The operator CLI
   supplies `CancellationToken.None` (`.../Features/Jobs/ClientCommand.cs:32`).
   A stalled local endpoint can hold a client indefinitely. Add a total client
   operation deadline and preserve the lost-response/idempotent-retry contract;
   this should be completed before claiming generally bounded IPC or using the
   client path for a live adapter. It does not invalidate the reproduced fake
   process scenarios under their test-owned endpoint.
2. `spikes/m0-durable-core/src/AgentTeamForge.DAL/Features/Jobs/JobStore.cs:55-57`
   reads the accepted row after commit. If that read fails, the caller gets an
   error even though acceptance succeeded. Same-key retry is designed to
   recover that ambiguity, so this is not an atomicity failure. Document the
   response as uncertain, never proof that no job was accepted. The storage
   exception comment at `.../Sqlite/StorageException.cs:10` should not be read
   as a no-commit guarantee for a post-commit read failure.
3. `spikes/m0-durable-core/src/AgentTeamForge.Host/Hosting/StateDirectory.cs:57-71`
   checks mode and symlink before an unbounded `ReadAllBytes`; a malformed owner
   profile/credential can consume excess memory. Bound both file lengths and
   validate credential/profile content before startup when hardening this path.
   The trusted local-operator profile and 0700 parent directory limit this
   finding's current scope; it is not a child-capability isolation claim.
4. The author's report records a native `atf` hash beginning `2821ff3c`.
   This independent rebuild produced SHA-256
   `55cd6b2d98f23f4e1fe39bc4eddad019f5b2e955f02d2d3f8d39bcbc01289bcd`.
   Both runs report 9,767,760 bytes; AOT output is not established as
   byte-reproducible. Bind each future report to its actual tested artifact
   hash and source commit, without implying the author's earlier artifact was
   tested here.

## Supported observations

The solution has the required name and three runtime projects plus tests,
targets pinned `net11.0`, and the Host/Business/DAL project references follow
the selected direction. Bridge and client roles use IPC without opening DAL.
The reviewed SQLite path uses `BEGIN IMMEDIATE` for accept, attempt, terminal
write and recovery, with a unique principal/team/operation/key constraint.
Acceptance commits job, intent and event together; attempt generation and
correlation commit before backend start; completion conditionally writes run,
job result and event together. Unknown and cross-principal job IDs both map to
not-found. Existing tests cover concurrent same-key requests and stale evidence.

The daemon holds a kernel `flock` descriptor before database open/socket unlink;
the state directory and credential are mode-checked, the UDS uses a peer-UID
check and private permissions, and inbound frames/read time are bounded.
The fake adapter reads bounded output lines, drains stderr without storing it,
and uses a held direct-child `Process` for its deadline kill. Recovery leaves
started attempts in `needs_reconciliation`, while unattempted intents can run.
These observations do not qualify real interactive process ownership or
power-loss durability.

## Independent gates and limits

Executed from the pinned author's clean worktree, without source edits. The
actual SDK was the main checkout's isolated `.tools/dotnet11/dotnet`; the
portable command below selects that same executable from any worktree:

```bash
main_checkout="$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)"
DOTNET="$main_checkout/.tools/dotnet11/dotnet" \
  DOTNET_ROOT="$main_checkout/.tools/dotnet11" ./scripts/verify.sh
```

Result: SDK `11.0.100-rc.1.26425.128`; restore and format passed; Release build
passed with zero warnings/errors; 37/37 .NET 11 tests passed; Linux x64 Native
AOT publish passed; 13/13 process scenarios passed against the native binary.
The native SQLite asset SHA-256 was
`eddcd4aa561d5b8f252db77e8272e7d1aed96bcab9fda3f177ca542f916290bf`.
The script used temporary private test state, fake children and held-handle
cleanup. No model/Herdr calls, system changes, main merge or push occurred.

Those gates establish the tested paths, not the missing stalled delivery and
dispatcher-fault paths above. The report's earlier three-repeat claim was not
repeated independently here. Windows/macOS/Pi, real backend, service launch,
wake, power-loss and full product/phase checks remain unrun. This review grants
no approval to inherited legacy interactive source.
