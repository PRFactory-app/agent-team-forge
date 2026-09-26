# Independent plan review: M0 durable-core spike

## Review metadata

- **Reviewed:** [m0-durable-core-plan.md](m0-durable-core-plan.md), with
  [e2e-demo-plan.md](e2e-demo-plan.md), [architecture](../architecture.md),
  [cross-phase contracts §6](../planning/full-product/contracts.md#6-waterfall-path-versus-early-experimental-preview),
  and the recovery semantics in [plan.md §6](../plan.md#6-delivery-idempotency-and-recovery).
- **Plan author family:** GPT. **Reviewer family:** Claude (independent session).
- **Scope of this review:** only the next isolated Linux fake-backend experiment
  (S1–S4). It does not review or gate P01–P08, the real Codex/Herdr demo
  (Track E), Windows/macOS, Pi, wake, or setup.
- This review changes no plan, source, or Git state and ran no agents or builds.

## Verdict

**APPROVED WITH CONDITIONS for a bounded Linux fake-core implementation.**

No design flaw was found that makes the durable/IPC contract unsafe. The
acceptance, attempt-before-effect, completion, and recovery boundaries are
correctly ordered and match `plan.md` §6.3. The conditions below are small plan
clarifications, not new process. C1 is an environment gate; C2–C4 must be
resolved in the plan (or recorded in the spike README as the implemented
decision) before the slice they name is coded.

Live-agent, AOT-release, and full-platform claims remain gated exactly as the
plan already states. Full P01–P08 completion is **not** required for this
isolated experiment (consistent with contracts §6).

## Conditions

### C1 — Toolchain gate (blocks S1 build, not design)

- **Location:** plan §3 precondition 3; §7 commands.
- **Problem:** Only a .NET 10 SDK is installed. The plan requires `net11.0`
  pinned to `11.0.100-rc.1.26425.128`, and AGENTS.md forbids silent retargeting
  or installing an SDK without authorization.
- **Minimal fix:** The user chooses one of: (a) authorize a project-local
  (non-global) .NET 11 RC1 SDK provisioning, then pin it in `global.json`; or
  (b) explicitly approve a labelled **net10 interim** for the fake core, with
  every report marking results as .NET 10 evidence and retargeting as a
  required follow-up gate. Without either decision, S1 is **blocked**, not
  implemented against a guessed target. Skeleton scaffolding that does not
  depend on the target framework may proceed.

### C2 — Define non-success outcomes while the daemon is alive (before S2)

- **Location:** plan §4 "Fake backend and recovery"; S2 bullet 4; matrix row
  "Atomic correlated completion".
- **Problem:** The plan defines restart recovery but not the terminal outcome
  when, with the daemon still running, the fake child hits EOF, exits without a
  correlated result, emits malformed/oversized output, or exceeds the maximum
  fake runtime. It says only that these never count as success. An implementer
  must then invent a state, and a "failed → requeue" choice would be a blind
  retry of an attempted effect.
- **Minimal fix:** State that after attempt-start commit, every non-success
  outcome transitions atomically (same completion-style conditional write with
  generation/correlation check) to a non-retryable state: `needs_reconciliation`
  when the effect may have occurred (any output/ack seen, or unknown), and at
  most a distinct `failed` only where the fake protocol proves the instruction
  was never read. Neither re-creates a dispatch intent. On deadline, the daemon
  may terminate **only its own direct child via the held process handle**
  (spike-only policy, recorded as such), then applies the same rule. Add one
  test for EOF-before-result and one for deadline expiry.

### C3 — Serialize write transactions explicitly (before S1 acceptance code)

- **Location:** plan §4 "Small storage schema" and "Job acceptance and
  idempotency" ("Queue admission and acceptance must be atomic, including
  concurrent submissions").
- **Problem:** Accept-or-get does read-then-write (key lookup, queue-length
  check, insert). In WAL mode a deferred transaction that upgrades from read to
  write can fail with a busy-snapshot error that busy timeout does not retry,
  and a capacity check done in a separate read can admit past the bound.
- **Minimal fix:** Specify that accept-or-get, begin-attempt, and complete each
  run as one `BEGIN IMMEDIATE` transaction containing the lookup, the capacity
  check, and the writes, with the unique constraint on the scoped key as the
  final arbiter. A busy/locked failure returns a stable retryable error and no
  acceptance. (Microsoft.Data.Sqlite's default `BeginTransaction()` is
  immediate; the requirement is that nothing opts into deferred mode or splits
  the check.) The existing concurrent-equal-requests test covers it.

### C4 — Single-instance lock must be kernel-released (before S1 lifecycle code)

- **Location:** plan §4 "Process and security contract", lock bullet; S3
  second-daemon test.
- **Problem:** "Exclusive daemon lock" is unspecified. A PID file or
  existence-based lock either blocks restart after an abrupt kill (every S3
  crash scenario) or tempts PID-based staleness checks, which the plan
  otherwise forbids.
- **Minimal fix:** Use an OS advisory lock held on an open file descriptor in
  the private state directory (released automatically on process death). Only
  after acquiring it may the daemon unlink a stale socket and bind. A second
  daemon that fails to acquire the lock exits without touching the socket, DB,
  or lock file. Add a restart-after-`SIGKILL` assertion to an existing S3
  scenario rather than a new test.

## Areas assessed with no blocking finding

- **Acceptance + unattempted intent transaction:** one commit for job, scoped
  key/fingerprint, intent, and acceptance event; response only after commit;
  capacity rejection stores no key. Correct (subject to C3).
- **Attempt-before-effect:** begin-attempt conditionally claims the intent and
  stores generation/correlation before spawn or instruction write; crash after
  that commit quarantines as `needs_reconciliation`. Correct and conservative;
  receipt-with-lost-ack uses the same policy.
- **Completion fencing:** expected job/run/generation/correlation and allowed
  state in one write with result and event; ack/EOF/stale evidence cannot
  complete. Correct (subject to C2 for the non-success side).
- **Daemon lifetime independent of clients:** supervisor starts daemon and
  bridges as siblings; daemon lifetime tokens drive work; client cancellation
  only stops waiting. Queued work dispatches with no client. Correct.
- **Private IPC and principal-scoped idempotency:** owner-private directory and
  socket, symlink/ownership rejection, bounded frames and read time, credential
  from a permission-checked file never in argv/logs, daemon-bound principal,
  key scoped to principal + team + operation, fingerprint over validated
  semantic fields. Adequate for a trusted-local-operator spike; the plan
  correctly disclaims C24/C25 child isolation. Optional hardening (non-blocking):
  also check the Unix-socket peer UID.
- **Fake child crash tests:** real child process, explicit barriers, abrupt
  termination at the three §6.3 windows, test-owned receipt to count
  invocations, cleanup only via supervisor-held handles. Sufficient. Test hooks
  compiled into the published binary are acceptable for the spike if gated by
  the explicit test profile, as planned.
- **Native AOT / MCP / SQLite assumptions:** the plan correctly treats MCP SDK
  tool registration, source-generated JSON, and native SQLite loading as
  unproven and requires an early AOT publish with a stop-on-failure rule. A
  native SQLite library shipped beside the executable is acceptable; the gate is
  AOT execution, not a single file. A JIT-only fake-core result is acceptable
  as an interim checkpoint only if labelled JIT-only.
- **Host → Business → DAL vertical slices:** exactly three runtime projects plus
  tests, DAL-owned transactions with no transaction handle leaked upward, bridge
  mode composes no DAL, no repository-port inversion. Consistent with the
  architecture.

## Non-blocking notes (accept or defer)

1. If the in-daemon completion write fails, the dispatcher should stop claiming
   new intents until restart rather than continue with an unrecorded run; the
   restart path then quarantines it. The plan implies this; stating it avoids
   ambiguity.
2. `job_get` for an unknown job ID and for a job of another principal should
   return the same not-found error, so IDs cannot be probed. With one bound
   principal this is a one-line rule, not a test burden.
3. Keep `needs_reconciliation` without an exit command in this spike, as
   planned; the operator reconcile path remains a later slice.

## Gates that remain open after this approval

- Toolchain decision (C1).
- Opposite-family (GPT/Codex) code review of the implementation and re-review
  of fixes.
- Published Native AOT equivalence on the tested Linux architecture.
- Real Codex/Herdr adapter integration (Track E), Claude, Pi, Windows/macOS,
  wake, setup, and all full-phase exits. None are implied by this approval.
