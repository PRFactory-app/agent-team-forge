# Codex queue fake conformance (W4): offline test slice

## Scope and status

This is an **offline, test-only** slice. It adds no production code and changes
no interface, schema, package, daemon wiring or transport. It does not run
native Codex, a model, the network, Herdr or any service. It is **not** a native
probe and **not** a production queue adapter. Independent Codex review of legacy
commit `52648dd` returned APPROVED-WITH-CONDITIONS
(`docs/spikes/codex-queue-conformance-review.md` on the review branch). This root
port and its interrupted-status fix (see "Canonical port" below) need a new
opposite-family review.

Inputs:

- Base `81a11b2` (approved fake core), path `spikes/m0-durable-core/`.
- Research `docs/research/codex-queue-admission.md`, read from `main` as Git blob
  `866ed48cc23dc429f454ca2a2d77a205744d20d6`. It pins public `openai/codex`
  `rust-v0.157.1` at `36650394c5b38c2990ccf2a3457165ca3e9d9726`.
- Lane W4 of the parallel implementation wave plan.

Deliverables (the only files changed):

| File | Git blob at legacy `52648dd` |
| --- | --- |
| `tests/AgentTeamForge.Tests/Support/CodexQueueModel.cs` | `57f0825fe7465f660a830f25ff83918980a74f77` |
| `tests/AgentTeamForge.Tests/Features/Agents/Backends/CodexQueueConformanceTests.cs` | `942f4e42ab234f329108259ec6eb9b5d737635d3` |
| `docs/spikes/codex-queue-fake-conformance.md` | this report |

The test paths were originally relative to `spikes/m0-durable-core/`; they now
live under the canonical root `tests/`. Do not
merge this branch's ancestry.

## What the model is

`CodexQueueModel` is a small deterministic model of the **queue** (`thread/queue/add`
and automatic dispatch). It does not model direct `turn/start` dispatch. It encodes
the research findings, not observed native behavior:

- Add is accepted while busy. It persists a row with a fresh server queue ID,
  then wakes dispatch before replying. A dropped reply loses only the reply.
- Dispatch is atomic start-if-idle under a lock owned by one process, and deletes
  the row only after the start. A fault flag can crash the runtime between the
  start and the delete.
- A human start is start-or-steer: it joins any active turn.
- A completed or failed turn drains the next row. An interrupted turn does not,
  and the runtime retains interrupted status: later adds and change wakes do not
  drain until a new turn starts (human or explicit queue start) or a real resume.
- Explicit queue start returns busy and keeps the row.
- `Wake` stands in for a change wake or external-change retry and skips an
  interrupted runtime. `Resume` is a real thread resume: it clears interrupted
  status, then dispatches if idle. Several runtimes can load
  one store. Their locks and active turns are local to each process.
- `StrictHumanPauseViolated` checks a trace against the **application**
  requirement. It is not a native capability, and the model deliberately offers
  no hold or reconciliation primitive, because the pinned source has none.

The model covers only these behaviors. It has no capacity limits, validation
rejections, hook rejections, notifications, pagination, cold/unloaded threads or
TUI binding.

## Tests

### Real core, through existing seams (`CodexQueueCoreQuarantineTests`)

These tests use the real `DispatchJob`, `JobStore` (on-disk SQLite through
`JobFixture`) and `RecoverOnStartup`. The backend is the existing
`ScriptedBackend`. Its lazy evidence script performs a model queue add after
delivery, and the core correlation serves as the client message ID. The
assertions target **current core behavior**: an uncertain queue attempt goes to
`needs_reconciliation` with one attempt and no remaining intent, and it is never
redispatched. The model only shows that the native side can still execute
afterward.

| Case | Core outcome asserted | Native side (model) afterwards |
| --- | --- | --- |
| Add during human turn, connection lost; theory: reply received (acked) / reply lost | `backend_eof`, acked flag matches, one attempt, no redispatch | Human turn completes, queued input auto-runs once; core unchanged |
| Timeout while queued behind a human | `backend_timeout`, owned-child termination once, no redispatch | Row still pending (termination is not revocation), runs once later |
| Daemon stop while waiting, then restart recovery | Running, then `daemon_restart_uncertain` via `RecoverOnStartup`, no redispatch | Row survives and runs once later |
| Native crash between start and row delete, reported as EOF | `backend_eof`, one attempt | Restarted runtime re-executes the surviving row (two executions); core does not add a third |

The core cannot prevent the native duplicate or the late execution. These tests
show only that the core does not make things worse: it neither replays blindly
nor reports failure or success.

### Model only (`CodexQueueModelOnlyCounterexampleTests`)

These tests are **protocol counterexamples in the model only**. They show which
application fences are needed. They prove nothing about native Codex.

- Human first: add persists, does not steer, explicit start is busy, the input
  auto-drains after completion, and **strict human pause is violated**.
- Queue first: the input runs before the add reply is used, and a later human
  start steers that queued turn, which also violates strict pause.
- Completed or failed turns drain; an interrupted turn leaves the row pending.
- Lost add reply plus blind retry with the same client ID runs twice under two
  queue IDs. A client ID is correlation, not idempotency.
- Crash between start and delete: after restart, `deleted: true` can follow an
  earlier execution, or the row can run a second time. Neither delete result
  proves non-execution.
- An empty queue, or a delete, does not fence a delayed add or a stale re-add.
- Two runtimes on one store both start the same row, and delete returns
  true/false. The dispatch lock is local to each process.

## Gates preserved

- **Strict human pause stays blocked.** The current core has no human-observation,
  foreign-activity, queue-revocation or strict-pause capability, and `IJobBackend`
  was not extended. Correlated completion after a human steers the turn is
  documented only as a model counterexample; it is not asserted as core behavior.
- A delete result, an empty queue, termination, an interrupt and a client ID are
  never treated as cancellation or deduplication proof.
- Visible-TUI binding, outage receipts, the native order of add and reply,
  cross-process consumers and power-loss behavior still need the separately
  authorized native probe. A production adapter needs a reviewed decision on
  strict pause, binding, revocation and receipts.

## TDD and evidence

1. Baseline at `81a11b2` with the pinned SDK `11.0.100-rc.1.26425.128`, from the
   project-local `.tools/dotnet11`: 61/61 unit tests passed.
2. Red: tests were written against a model stub whose members throw
   `NotImplementedException`. All 15 new cases failed and the 61 existing tests
   passed.
3. Green: implementing the minimal model made 76/76 pass.
4. Mutation sanity check (scratch copy, reverted): draining after an interrupted
   turn made 2 of the model tests fail.
5. The 15 queue cases passed in each of five repeated filtered runs (about 0.4 s
   each).
6. `scripts/verify.sh` with `DOTNET` pointing at the pinned SDK: restore, format
   `--verify-no-changes`, Release build `-warnaserror`, unit tests **76/76**,
   Linux x64 Native AOT publish, and published-binary scenarios **19/19**. All
   passed. The production source did not change, so the AOT result only confirms
   that nothing regressed; it adds no new evidence.

Synchronization uses deterministic barriers: the order of the synchronous
scripts, and a `TaskCompletionSource` that is signaled when the dispatcher
reaches the waiting state before the test cancels the daemon lifetime. There are
no sleeps. The timeout case relies on the dispatcher's existing 300 ms runtime
deadline, as `DispatchFaultTests` already does; that is a timer inside the code
under test, not a test sleep.

Limits: these results come from Linux x64 only, with no Windows or macOS runs,
no native Codex, no concurrency stress and no power-loss testing. Passing model
tests establish neither native behavior nor any missing runtime control.

## Canonical port and review condition

The slice was ported to the root layout (`tests/AgentTeamForge.Tests/...`, same
namespaces) by copying blob contents from `52648dd`, without its ancestry. The
review's non-blocking condition is resolved in the model: `EndTurn(Interrupted)`
now retains interrupted status, so a later `Add` or `Wake` does not drain, and a
new `Resume` distinguishes a real resume from a change wake (pinned source:
`service.rs` lines 218-225, 472-482, 549-564). Restart cases now call `Resume`.

TDD: `Interrupted_status_holds_later_adds_and_change_wakes_until_a_real_resume`
was added with `Resume` as a plain drain stub and failed (an `Add` after the
interruption started a queue turn); retaining the status made it pass. Root gates
with the pinned SDK via `scripts/verify.sh`: format, Release `-warnaserror` build,
unit tests **77/77**, Linux x64 Native AOT publish and published scenarios
**19/19** passed. A first run had one unrelated timeout in
`CrashBoundaryScenarios.Crash_after_acceptance_commit_before_response_is_recovered_by_same_key`
(30 s, machine load average about 8); the immediate full rerun passed. Linux x64 only.
