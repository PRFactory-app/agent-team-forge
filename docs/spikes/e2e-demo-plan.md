# Near-term Linux end-to-end demo

## Priority and scope

The user prioritizes a testable end-to-end path within a few hours over a full
PoC. This is a delivery target, not a verified completion estimate. Parallelize
independent review, safety fixes, and build preparation; do not bypass blocking
correctness findings to meet a clock.

The first usable demo uses a **real Codex TUI in Herdr**, not just a fake backend:

1. Explicitly select interactive Herdr mode in a disposable test profile.
2. Start an independent .NET 11 daemon and an owned agent terminal.
3. Connect a thin MCP client/bridge and submit a bounded, harmless job.
4. Observe durable acceptance before external dispatch.
5. Abruptly kill that client/bridge while the job is active.
6. Connect a new client and retrieve the correlated, committed result.
7. Send a follow-up to the same verified conversation; reject duplicate retries.

A fake-backend pass is the first engineering checkpoint, not the final real-agent
claim. Native AOT is a separate published-binary gate: label a JIT-only demo as
such if AOT is blocked, never imply the AOT goal passed. .NET 11 source/API
research is available; the actual SDK/package path must be built and tested.

## Parallel work and gates

- **Track A: adapter repair.** Address the blocking findings in the existing
  interactive spike's `CODE-REVIEW.md`, using pragmatic TDD. Keep live sessions
  untouched unless proven test-owned and explicitly selected. Independent GPT
  re-review is required before promoting the adapter path.
- **Track B: plan review.** Review the durable-core plan and this demo increment
  together. Focus on acceptance/attempt atomicity, process ownership, real-agent
  binding, and bounded scope. Do not expand this into the entire M1 matrix.
- **Track C: preparation.** Verify a project-local .NET 11 toolchain and build
  prerequisites; do not modify global SDK selection, desktop configs, services,
  or reference repositories. No Git initialization/publication is implied.
- **Track D: core implementation after plan review.** Implement the smallest
  vertical slice from `m0-durable-core-plan.md`, Host → Business → DAL. Fake tests
  establish crash/idempotency behavior without model cost. Core implementation
  can proceed while adapter fixes/re-review continue.
- **Track E: integration after both prerequisites.** Bring the reviewed Codex
  control path into Business agent-execution features. Do not shell out to the
  old spike CLI as an unowned second scheduler/state authority. Use the same
  Business acceptance/dispatch/result use cases for fake and real adapters.

Independent code review may start on stable completed slices while the next
slice is developed. Record reviewed file snapshots and re-review changed safety
paths. Never claim a later revision inherits an earlier approval automatically.

## Minimal real adapter contract

One dedicated test team, one agent, one active turn, and an explicit terminal
profile suffice. One app-server per TUI is acceptable; process count optimization
is not this demonstration's purpose.

- Commit launch intent and job attempt generation before terminal/process or
  prompt effects. Existing uncertain attempts never relaunch automatically.
- Use a unique, proven-owned terminal session; reject reuse or teardown of an
  unverified existing session. Preserve unrelated user tabs/processes.
- Verify app-server/process/socket identity and live TUI association before
  dispatch or interrupt; stale binding blocks the operation.
- Use native Codex message/turn IDs and run correlation, never a text tag as
  proof of machine origin. Record authoritative terminal turn evidence before
  marking a job completed. Process exit is not turn completion.
- Atomically claim a job and conversation so concurrent clients cannot dispatch
  twice. Pause on unknown history, foreign/human activity, or unavailable binding.
  Do not add an unsafe busy override to make the demonstration work.
- If foreign activity occurs, require explicit verified-idle reconciliation;
  a fresh isolated session is also acceptable for the first demo. Human input
  capability still needs its separately labelled manual test.
- Persist evidence/status independently of bridge requests. Bound buffers,
  deadlines, credential handling, and diagnostics. Do not auto-answer approval.
- Surface approval observation as blocked/unsupported if it is not yet reliable;
  use a harmless no-tool task for the initial demo, not permission bypass.
- Keep interrupt and agent stop separate. Neither optional demo control may
  weaken ownership checks or imply an unverified capability is supported.

## Deliberate limits

Claude launch/follow-up feasibility remains useful, but its authoritative cancel
and origin-correlation gaps are not a prerequisite for the first **Codex-only
Linux demo**. This is an explicit demonstration limit, not removal of Claude,
Windows/macOS, interactive setup, real service context, or full PoC requirements.

No installer/autostart, dashboard, external connector, generalized multi-team
control, full event read/ack API, broad performance study, or guaranteed survival
of arbitrary daemon/backend crashes is included. Daemon restart may honestly
quarantine uncertain attempts. Full M0/M1 exit gates remain unpassed.

## User handoff

Provide short tested commands to start the daemon, open the owned TUI, submit,
kill/reconnect the bridge, read the result, and safely clean up proven-owned demo
resources. Include exact versions, JIT versus AOT status, code-review outcome,
known limitations, and one place to read diagnostics. Use a disposable workspace
and tiny model tasks. Never include private paths/credentials in public docs or
publish raw evidence without a separate artifact review.
