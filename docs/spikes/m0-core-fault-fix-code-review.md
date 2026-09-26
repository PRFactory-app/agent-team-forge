# Independent re-review: M0 core fault fixes

## Snapshot and verdict

Reviewed exact Claude source commit
`1d3c409514dc30a774376d7cdc124a6069c843c4` on
`spike/m0-core-fault-fix`, parent
`55d3c054acf072f7d3da49c5f9bd20d9c3860058`. The author's
`spikes/m0-durable-core/CORE-FAULT-FIXES.md` is part of that commit. This is a
focused Codex/GPT re-review of B1/B2 in
[the original code review](m0-durable-core-code-review.md), with the approved
S1–S4 plan and architecture retained. The author worktree was read and tested
at this exact HEAD; no unapproved source was merged into integration.

**Verdict: CHANGES REQUIRED.** The full-effect deadline and conservative
uncertainty behavior substantially resolve B2 on the tested fake backend.
Profile validation, dispatcher fault containment and unhealthy exit improve B1,
but admission is not fenced at the instant dispatch halts. A concurrent submit
can still commit and be acknowledged after the dispatcher decides to stop.
This violates the fix's stated no-acknowledgment-without-dispatcher guarantee.
Return this semantic fix to a Claude writer, then re-review the pinned result.
Do not merge `1d3c409` or claim the bounded fake core accepted yet.

## Blocking finding F1 — halt/admission decision has no shared fence

At `spikes/m0-durable-core/src/AgentTeamForge.Business/Features/Jobs/DispatchJob.cs:221-224`,
`Halt` sets `HaltReason` locally. The loop then returns at `:53-89`. The host
waits for that task and only afterward cancels the IPC listener at
`spikes/m0-durable-core/src/AgentTeamForge.Host/Hosting/DaemonCommand.cs:93-106`.
Meanwhile an already accepted IPC connection can still invoke
`JobsEndpoint.Handle` → `AcceptJob.Execute` → `JobStore.AcceptOrGet` and write a
success response; neither endpoint nor acceptance checks a synchronized
admission state. `IpcServer` handles connections on independent tasks
(`spikes/m0-durable-core/src/AgentTeamForge.Host/Transport/IpcServer.cs:57-68,101-106`).
The new real-process test waits for daemon exit **before** submitting the next
job (`tests/AgentTeamForge.Tests/Scenarios/CoreFaultScenarios.cs:33-53`), so it
does not cover this interval or an in-flight request.

A job accepted in that interval remains durably queued, which prevents data
loss, but it may have been acknowledged while no worker will dispatch it until
someone restarts the daemon. It is therefore not the promised fail-stop admission
boundary. The interval is small, but there is no ordering guarantee between
connection tasks and the host continuation.

**Required:** Define one linearized admission-close boundary shared by
dispatcher halt and submission. Requests admitted before closure may complete
their durable transaction and remain queued for restart; requests beginning
after closure must get a stable unavailable/unhealthy outcome without a new
acceptance. Preserve the same-key recovery path for a response lost around
closure. Add a deterministic barrier test with an in-flight submit and a
post-halt submit; assert committed intent/event counts and responses, not a
sleep-based race. The host should still close the listener and exit unhealthy.
The fresh Claude writer owns any executable correction; Codex will re-review.

## B1/B2 behavior confirmed in the reviewed source

- `InitCommand.cs:14-20` and `SpikeProfileFile.cs:37-59` reject invalid queue
  and runtime overrides before daemon readiness. `DispatchJob.cs:25-38`
  independently rejects an invalid runtime at construction.
- `DispatchJob.cs:92-129` starts one deadline immediately after the durable
  attempt-start boundary, before backend start. Start is separated from
  `DeliverAsync`; a timed-out start is recorded as
  `needs_reconciliation/backend_start_timeout`, with no automatic retry.
  A late returned run is terminated by its held handle and never delivered to
  (`:180-190`) while the daemon remains alive.
- `DispatchJob.cs:128-176` bounds delivery and evidence reading with that
  deadline. Timeout or unexpected post-attempt failure cannot become
  `failed` or recreate an intent. The fake-only deadline kill targets its held
  direct child. `End` traps terminal-write failure and sets a halt reason
  (`:243-254`); `DaemonCommand.cs:93-117` stops serving and exits 70 after the
  dispatcher returns. The revised completion-write scenario checks restart
  quarantine and subsequent queued progress.
- `DispatchFaultTests` and `CoreFaultScenarios` add focused invalid-profile,
  late-start, stalled-delivery and fail-stop cases. The real child stalls before
  reading stdin, and the published scenario confirms its owned PID disappears.
  The author's note correctly limits that test: the short request does not
  force a physical pipe write stall; the uncooperative write is simulated in a
  deterministic in-process test.

## Remaining qualifications

1. `DispatchJob.cs:160-163` calls `TerminateOwnedChild` directly in the timeout
   catch. If termination itself throws, `End` is skipped and the task faults;
   the host exits unhealthy and restart quarantines the started attempt. This
   remains conservative but does not immediately persist `backend_timeout`.
   Use the existing guarded termination helper before claiming that failed
   termination always writes that reason while the daemon is alive.
2. The late-start continuation at `DispatchJob.cs:180-190` is in-process and
   unawaited. It cannot promise child cleanup if the daemon itself exits while
   a blocking OS start is still pending. That is an honest uncertainty/recovery
   limitation, not proof of process adoption or seamless daemon-crash recovery.
3. The four non-blocking findings in the [original review](m0-durable-core-code-review.md)
   remain: total IPC-client connect/write deadline, post-commit acceptance-read
   ambiguity, bounded private-file reads, and binding reported artifact hashes
   to the particular tested build. None supplies the missing admission fence.

## Independent gates

From the exact author worktree's `spikes/m0-durable-core/`, selected the main
checkout's isolated SDK with command-scoped environment:

```bash
main_checkout="$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)"
DOTNET="$main_checkout/.tools/dotnet11/dotnet" \
  DOTNET_ROOT="$main_checkout/.tools/dotnet11" ./scripts/verify.sh
```

SDK `11.0.100-rc.1.26425.128`: restore, format verification and Release build
passed with zero warnings/errors; **47/47** tests passed; Linux x64 Native AOT
publish passed; **19/19** published native process scenarios passed. These are
one independent run, not reproduction of the author's repeated-run claim.
This run's native `atf` was 9,784,352 bytes with SHA-256
`b00256bbab0734eea289dad5c0af4ae8adedddbb5ae346723b73a1ac87aaff0d`;
it differs from the author's reported artifact hash and makes no byte-for-byte
reproducibility claim.
The script's local artifact and scenario files are ignored; no raw evidence was
committed. The author worktree remained clean. No real agent/model/Herdr call,
system change, main merge or push occurred. Windows/macOS/Pi, wake, service
launch, power-loss and the real Codex adapter remain unrun or separately gated.
