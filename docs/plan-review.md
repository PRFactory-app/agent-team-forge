# AgentTeamForge — independent plan review

> Editorial note: Source references and comparison terminology were sanitized
> for public publication. Findings, dispositions, and verdicts remain historical;
> this edit is not a new review or approval.

## 1. Scope and basis

- **Reviewer:** Claude Opus, in a separate session from the plan's authors.
- **Reviewed:** `README.md`, `HANDOFF.md`, `docs/plan.md`, `docs/poc.md`,
  `docs/roadmap.md`, `docs/terminal-modes.md` (English revision, including the
  late interactive-terminal requirement). `AGENTS.md` and the `spikes/`
  directory were read only for context.
- **Nature:** a design review of planning documents. Nothing was built,
  executed, or tested for this review. None of the findings below are runtime
  evidence, and none of them say that any capability works or fails.
- **Fixed requirements, treated as out of scope for deletion:**
  - The explicit, persisted interactive/headless setup choice.
  - A real agent TUI in Herdr on Linux, a visible terminal tab on Windows, and
    an equivalent on macOS.
  - No log-tail substitute and no silent fallback.
  - Interactive mode is first-class in the PoC.
  - The daemon is local .NET/AOT with SQLite.
  - The external orchestrator connector is opt-in.

  Findings only say how to make these requirements precise and testable.

## 2. Overall assessment

The document set is coherent and unusually honest about evidence. It clearly
separates planned work from verified work. It keeps client crash, daemon crash,
and agent/machine crash apart (README "Three different guarantees"; plan §6).
It rejects exactly-once side effects (plan §1 non-goals, poc §3). It keeps
managed interactive sessions separate from attaching arbitrary Desktop sessions
(plan §4.1, terminal-modes §4). It says that an MCP notification is not a model
wake (plan §4.5, poc L07). It also has a sound Linux-reference versus
full-PoC gate structure (terminal-modes §7, poc §2/§9, roadmap M1 exit gate).
The superseded headless-first scope is not reintroduced anywhere.

The main gaps come from the late interactive requirement. The durable-state and
recovery model (plan §5–§6) was designed around a daemon-owned headless runner.
It does not yet say:

- What counts as authoritative evidence for an interactive TUI.
- How turns started by a human are recorded.
- What a daemon restart should do to a live terminal session a human may be
  using.

There is also one ambiguity in the core accept/dispatch contract. Each of these
gaps can be fixed in the documents without implementation evidence.

## 3. Critical findings (fix before M0 work starts)

### C1. Accepted-and-queued and dispatch-in-progress are not separable durable states

- **Where:** plan §5.3 bullet 5 ("Write the assignment, idempotency record, and
  dispatch intent in the same transaction before acknowledgment"); plan §6.1
  (`accepted` vs `dispatching`); plan §6.3 rows 2–3; poc C07 vs C08.
- **Problem:** Acceptance writes the "dispatch intent" in the same transaction.
  After a restart, the daemon therefore cannot tell these two cases apart:
  - A job that was committed but never attempted. §6.3 row 2 says "dispatcher
    can continue".
  - A job whose external delivery may be underway. Row 3 says "blind automatic
    retry is forbidden".

  C07 and C08 expect opposite recovery for these two cases. Under the current
  wording, recovery must either treat every queued job as uncertain (stuck) or
  risk re-sending a delivered prompt.
- **Fix:** Define two separate durable writes:
  1. Acceptance commits the job, the idempotency record, and a *queued*
     (not-yet-attempted) outbox row.
  2. The dispatcher commits a *delivery attempt started* marker (run
     generation, correlation ID) **before** any external side effect. That
     side effect is a process spawn, a terminal launch, or a prompt write.

  Recovery then works like this:
  - A queued job with no attempt marker may be dispatched.
  - An attempt marker without backend evidence becomes `needs_reconciliation`.

  State this in plan §5.3 and §6.3, and map C07/C08 to it explicitly.

### C2. The interactive control/result contract has no acceptance criteria and no place in the domain model

- **Where:** terminal-modes §2 (human input), §4 (control path, "authoritative
  status/result signal", keystroke clause); plan §4.2–§4.4, §5.1–§5.2, §6.1;
  poc P0 last bullet, T05/T06/T15; roadmap M0 exit gate ("authoritative final
  results in both Linux reference modes").
- **Problem:** The M0 exit gate requires "authoritative final results" for
  interactive TUIs, but no document defines what qualifies. Three gaps follow.
  1. **Evidence hierarchy.** The documents do not rank the possible completion
     signals:
     - Backend-emitted lifecycle signals, such as a documented hook or event,
       or a transcript/session record.
     - A model-invoked tool call ("report result"). This depends on the model
       cooperating and is not authoritative.
     - Terminal text or silence. The documents already exclude these.

     Without this ranking, a model self-report could be accepted as
     `completed`.
  2. **Human-originated turns.** In a TUI, a human can start turns. The domain
     model (plan §5.1 Job/Run, §6.1 states, §3.4 "one active turn per
     conversation; FIFO") only knows daemon jobs. It says nothing about how a
     turn or completion not started by the daemon is recorded, or how it is kept
     from being attributed to a queued job. T15 needs a policy, and the schema
     needs somewhere to record foreign activity (for example a
     `foreign_turn`/observed-activity event and an agent busy state that is not
     owned by a job).
  3. **Delivery mechanism status.** terminal-modes §4 says "Do not *silently*
     substitute simulated keystrokes…". It is unclear whether injecting
     keystrokes into a terminal (for example Herdr send-keys or WT input) is:
     - forbidden, or
     - allowed as an explicit adapter decision (ADR) that always yields
       unacknowledged/uncertain delivery under plan §6.1.

     This may be the only follow-up path for some backend × provider pairs, so
     the rule must be unambiguous before M0.

     Related: native wake ownership constraints imply that follow-ups to an
     interactive *child* may need an AgentTeamForge bridge inside that child's
     process tree. That is a child
     bridge, not only the lead's bridge (HANDOFF §6, plan §2.2). Plan §3.1 and
     §8 describe bridges and child permissions mainly from the lead's side.
- **Fix:**
  - Add a short "interactive evidence contract" to terminal-modes §4 with:
    - the evidence hierarchy;
    - the rule that a model self-report alone cannot set `completed`;
    - how foreign turns are recorded;
    - whether explicit keystroke delivery is allowed and at what evidence level.
  - Add an observed-activity/foreign-turn concept to plan §5.1/§6.1.
  - Name the child-side bridge as a candidate component with its own capability
    scope.

### C3. Daemon-crash policy contradicts itself for interactive sessions and can lose results produced while the daemon is down

- **Where:** terminal-modes §5 ("After a daemon restart, the same reconciliation
  rules apply as for headless mode"); plan §6.4 steps 4–5 ("stop a provably
  owned surviving runner before allowing a new run"; "Kill-on-close may be the
  intended daemon-crash policy"); README guarantee 2; poc T10, L06.
- **Problem:**
  - **Termination policy.** An interactive TUI is owned by the terminal server,
    not by the daemon (plan §3.1). Applying the headless rules unchanged means
    one of two things. Either a daemon restart kills a live session that a human
    may be typing in, or the rule cannot be applied at all. The documents never
    say which behavior is intended. The likely intent is that interactive
    sessions survive daemon death and are re-bound or marked uncertain. That
    matters for the user.
  - **Lost results.** If the result/control channel ends at the daemon (a
    socket or pipe), completions that happen while the daemon is down are lost.
    A lost completion turns a normal restart into `needs_reconciliation` for
    every in-flight interactive job.
- **Fix:**
  - State an interactive-specific daemon-crash policy in terminal-modes §5 and
    plan §6.4. The default should be: never terminate a live interactive session
    during recovery; re-bind only after verified identity; otherwise mark it
    uncertain and leave it to the human/lead.
  - Scope kill-on-close/Job Object semantics to headless runners.
  - Require the interactive result path to be replayable after a daemon outage.
    It could be a durable spool written by the backend hook or child bridge, or
    re-reading the backend's session record. Add the "completion while daemon
    down" case to T10/L06.

## 4. Moderate findings (fix in the documents; may be refined in M0)

### M1. `needs_reconciliation` has no exit, and FIFO turns can block an agent permanently

- **Where:** plan §3.4 (one active turn per conversation, FIFO), §6.4 step 5,
  §7 tool table; poc C08, C09, C15; README guarantee 2.
- **Problem:** The plan routes several crash windows to `needs_reconciliation`,
  but no tool, CLI command, or policy resolves that state. Later jobs for the
  same conversation queue behind it indefinitely.
- **Fix:**
  - Define who may resolve it (the lead, the human through the CLI, or both).
  - Define the allowed outcomes, for example:
    - mark abandoned/failed;
    - accept an observed result;
    - release the conversation lock;
    - allow a new attempt with a *new* idempotency key.
  - Add one tool or CLI command to plan §7 and a C-test for resolution
    (resolution is itself idempotent and never auto-resends).

### M2. Event consumer identity across lead reconnection is undefined

- **Where:** plan §5.4 (cursor "bound to the consumer"), §5.2
  `consumer_cursors`, §7 `team_attach`; poc C11/C12, demo step 6.
- **Problem:** Demo step 6 relies on a *new* lead process retrieving results
  after a crash. It is not stated whether the cursor belongs to the connection,
  the bridge process, or a durable role/principal (for example "lead of team
  X"). If it belongs to a connection, the new lead sees either everything or
  nothing. It is also unstated what happens when two bridges for the same role
  are live at once, for example a lead that restarted before the old bridge
  died.
- **Fix:**
  - Bind cursors to a durable principal/role.
  - State the policy for concurrent consumers of the same role: reject the
    second one, fence it by generation, or let both share the cursor with
    at-least-once delivery.
  - Add a negative test.

### M3. Credential and correlation delivery into terminal-launched processes, and reattach authority, are not planned

- **Where:** plan §3.3 ("Secrets must not appear in argv or logs… Set the exact
  bootstrap protocol… in M0"), §8; terminal-modes §4–§6; T05/T07.
- **Problem:**
  - **Getting credentials in.** Interactive children are started through a
    terminal provider, not as daemon children, so the daemon's environment is
    not simply inherited. Whether Herdr or a Windows/macOS provider passes the
    caller's environment variables into a new tab/pane is a provider capability
    to verify. It is not guaranteed; for example, a new tab in an existing
    Windows Terminal window may take its environment from the terminal process.
    Since argv is ruled out for secrets, the plan needs a stated mechanism, for
    example a one-time nonce file in a private runtime directory that the child
    bridge exchanges for a capability.
  - **Reattach authority.** After a lead crash, the plan does not say what
    authorizes `team_attach` as lead. The PoC answer may honestly be "same OS
    user". Children must still not be able to attach as lead.
- **Fix:**
  - Add "environment/correlation passing" to the per-provider questions in
    terminal-modes §6.
  - Add a lead-reattach rule to plan §3.3/§8. State plainly that capability
    tokens prevent confused-deputy mistakes between agents; they are not a
    security boundary against the same OS user.

### M4. Execution and authentication context per launch path is not a tracked risk

- **Where:** plan §3.2 (mentions "CLI login" only for Windows), §4.2–§4.3;
  roadmap M0 work items 5–8; terminal-modes §3 step 1.
- **Problem:** The two modes and launch paths can start agents in different
  contexts:
  - Headless children inherit the daemon's environment.
  - Interactive children inherit the terminal's environment.
  - A service or autostart daemon inherits a minimal one.

  These contexts can differ in PATH (CLIs installed through version managers),
  HOME/config, MCP configuration, and credential stores (macOS Keychain from a
  non-GUI context, a Linux secret service without a D-Bus session, Windows
  profile loading). The same backend could then authenticate in one mode and
  fail, or run with different config, in the other. That would silently break
  mode parity.
- **Fix:**
  - Add an M0 matrix row for "backend auth + config resolution" per platform ×
    mode × daemon launch context.
  - Add a setup check (terminal-modes §3 step 5) that proves the chosen mode
    authenticates in the context the daemon will actually use.

### M5. Cancel and hard timeout are undefined for interactive sessions

- **Where:** plan §3.4 (`job_cancel`), §8 ("A hard time limit must be able to
  stop a job without model cooperation"); terminal-modes §5, T08; poc C21/C22.
- **Problem:** In a TUI, "cancel the job" could mean interrupting the current
  turn and leaving the session open, or ending the agent session and closing the
  tab. A hard timeout that kills a session a human is actively using may not be
  what the user wants. T08 covers neighbouring tabs but not this choice.
- **Fix:** Split *interrupt turn* from *stop agent* in the API semantics
  (plan §7). Define the default interactive behavior for cancel and timeout
  (for example: interrupt the turn, keep the tab, report the verified outcome).
  State what happens if the interrupt is not observed.

### M6. Approval visibility in interactive mode is not planned

- **Where:** plan §8 (approval as an explicit blocking state), §12 ("Agent waits
  on a hidden approval"); poc C23, L05 (mode unspecified); terminal-modes §8
  (no approval T-case).
- **Problem:** In interactive mode, approval prompts show up in the TUI and the
  human is a legitimate decision maker. If the adapter cannot observe that
  state, the daemon reports `running` while the agent is blocked. This is
  exactly the risk listed in §12.
- **Fix:**
  - Require each interactive adapter to either surface "waiting for approval"
    or declare the capability `unsupported` explicitly (plan §4.4).
  - State that L05 runs in both modes on Linux.
  - Add a T-case for an approval raised in a TUI with no lead connected.

### M7. The GUI/service launch boundary is decided in M0 but first tested in M3

- **Where:** terminal-modes §6 (session launcher), §8 closing paragraph (T13–T14
  only before platform MVP); plan §3.2 (PoC uses a foreground daemon); roadmap
  M0 item 8, M3 exit gate.
- **Problem:** Ownership, binding, and crash semantics are all validated with a
  foreground daemon started from the user's desktop. It is possible that a
  Windows or macOS autostart context needs a separate session launcher. If that
  launcher ends up holding the terminal binding, the model in C3 changes late,
  in M3.
  - Linux has a related wrinkle. A foreground PoC daemon started inside a Herdr
    pane is tied to that pane or server. Demo step 4 requires the daemon to be
    outside the killed client's process group, and this setup could make that
    look true or false for the wrong reason.
- **Fix:** Pick one of these and record the choice:
  - **Option A:** Have the full PoC run T04/T07/T10 at least once per platform
    with the daemon started in its intended production context (systemd user
    unit, logon task/autostart, LaunchAgent).
  - **Option B:** Keep M1 as planned, but add an ADR that accepts this risk and
    names the re-plan trigger.

  In both cases, state in poc §5 how the Linux PoC daemon is started, so it is
  not inside a Herdr pane it depends on.

### M8. Platform gate lists disagree

- **Where:** terminal-modes §8 last paragraph ("T01–T12 and T15 apply to the
  PoC/spike on each tested platform"); poc L10/L11 ("T04–T11 and T15"); poc
  §2 Linux bullet ("applicable T01–T12 and T15"); poc L03–L06 (mode not
  stated, although poc §9 requires both modes).
- **Problem:**
  - The Windows/macOS gates leave out T01–T03 and T12. Those cover setup choice,
    persistence, missing provider, and mode change, which are part of the user's
    installer requirement.
  - The L03–L06 rows do not show the both-modes requirement from §9.
- **Fix:** Align the lists: either include T01–T03/T12 in L10/L11, or explain
  why they are not platform-specific. Add "both modes on Linux" to the gate
  column of L03–L06.

### M9. Status statements do not mention the existing spike code

- **Where:** README "Status" ("No daemon, CLI… has been implemented"); HANDOFF
  §1 and §8 ("documentation only"); `spikes/m0-interactive/atf_spike/`.
- **Problem:**
  - The directory contains Python spike code (a Codex app-server JSON-RPC client
    over WebSocket-on-UDS). Its docstrings record an observation ("Codex 0.157
    … observed: HTTP/1.1 101 upgrade"), and a `__pycache__` directory is
    present. The documents say nothing about this directory.
  - The observation is not captured under the planned `evidence/poc/` structure
    (poc §10), and it is not stated that the spike is throwaway, non-.NET code.

  I did not run or validate this code.
- **Fix:**
  - Mention the spike in README/HANDOFF status as exploratory, non-product code.
  - Either record its observations as M0 evidence with version and date, or
    mark them unverified.
  - Remove `__pycache__` or ignore it.

## 5. Optional suggestions (non-blocking)

- **S1 — Sequencing within M1.** The Linux reference PoC is already a separate
  decision point. Consider stating that two items are not blocking for the
  *Linux* go/no-go: the comparison baseline (poc §8.1 item 1) and the
  fake connector seam (C28). They would still be required for the full PoC.
  This bounds effort without removing any requirement.
- **S2 — Duplicate tabs on retry.** Extend C01 or add a T-case: an
  `agent_start` retried after a lost response must not open a second terminal
  tab (plan §6.2, terminal-modes §8).
- **S3 — macOS provider selection criteria.** List the M0 selection criteria up
  front:
  - per-tab stable identity;
  - closing one tab only;
  - environment/argv passing (see M3);
  - required automation/TCC permissions;
  - scriptability without Accessibility access.

  This makes the spike outcome auditable (terminal-modes §1, §6).
- **S4 — AOT bridge budget.** The bridge is started once per host session.
  Consider giving it its own startup and memory budget (poc §8.3 covers daemon
  startup only). Also check in the M0 AOT slice whether the chosen MCP SDK tool
  registration path is trim/AOT-clean or needs source-generated registration
  (plan §9). Do not assume either way.
- **S5 — Human/lead concurrency options.** Pre-list two or three candidate
  policies for P0 so the decision is quick. Examples:
  - Lead follow-ups are queued until the observed state is idle.
  - Human input marks the session foreign-busy.
  - The human always wins.

  (poc P0; terminal-modes §2.)
- **S6 — Lead host coverage.** The wake spike covers only a Claude lead on
  Linux (poc L07). If Codex is expected to act as a lead host, list it
  explicitly as unverified in the M3 support matrix (roadmap M3 deliverables).
- **S7 — Reading order.** README and HANDOFF §3 list the documents in slightly
  different orders. Harmless, but one canonical order would be clearer.

## 6. Checked and found consistent

- Opt-in external orchestration: no network, credentials, or real connector in
  the PoC
  (plan §10, poc §3, roadmap M4 exit gate).
- No silent fallback to headless, tmux, or another terminal; no log tail
  presented as interactive (terminal-modes §1–§3, poc §9, roadmap M0/M3).
- The Linux reference PoC is reported separately; missing platforms are marked
  blocked, not waived; Linux-limited preview wording is explicit
  (terminal-modes §7, poc §2, roadmap M1/M3).
- Generation fencing is correctly limited to DB updates and does not stop an
  orphan from writing files (plan §6.4, HANDOFF §6).
- Read/ack is receipt by the client, not proof that the model acted
  (plan §5.4).
- AOT is treated as a goal; a JIT fallback needs an explicit decision
  (poc §9, plan §9).
- `synchronous=FULL`, no DB transaction across a CLI call, and DB failure is
  never treated as empty state (plan §5.3, poc C19/C30).
- Evidence claims are appropriately hedged. No document claims tests or builds
  have run.

## 7. Recommendation

**Conditional approval.** The direction, scope boundaries, and gate structure
are sound and respect every fixed user requirement. Before M0 implementation
begins:

- Resolve **C1–C3** in the documents.
- Disposition **M1–M9**. Each can be fixed with a document edit or recorded as
  an explicit M0 decision item.

None of these findings needs implementation evidence to fix, and none calls for
reducing the interactive, multi-platform, or opt-in-connector scope. The
optional suggestions may be accepted or declined at the main agent's
discretion.

## 8. Re-review of documented resolutions

- **Reviewer:** Claude Opus, separate session from the resolution author.
- **Reviewed:** `docs/review-resolutions.md` and the revised `README.md`,
  `HANDOFF.md`, `docs/plan.md`, `docs/poc.md`, `docs/roadmap.md`, and
  `docs/terminal-modes.md`, checked against sections 3–5 above and `AGENTS.md`.
- **Nature:** document review only. Nothing was built, run, or tested. The
  `spikes/` tree was deliberately not inspected. This re-review does not
  approve any runtime implementation and is not evidence that any capability
  works.

### 8.1 Critical findings

- **C1 — Resolved.** Plan §5.3 now separates the acceptance transaction
  (job, idempotency record, queued *unattempted* outbox, with no delivery
  claim) from an attempt-start transaction (new generation and correlation ID).
  The attempt-start transaction commits before any spawn, tab launch, or prompt
  write. Plan §6.3 rows 2–3 map to C07 (dispatch allowed) and C08 (uncertain,
  no blind retry), and poc C07/C08 and HANDOFF §6 match.
- **C2 — Resolved.**
  - terminal-modes §4 "Interactive evidence contract" sets the evidence
    hierarchy. Only a run-correlated documented lifecycle signal or a verified
    durable backend record can set `completed`. Model self-report is
    informational only. Terminal text, silence, and tab presence are
    non-authoritative.
  - Foreign/human activity has a home in the model: `observed_activity` table,
    `foreign_busy` state, and a human-wins pause until explicit reconciled
    idle. It is never attributed to a queued job.
  - Keystroke injection is unambiguously not a v1 delivery transport and
    cannot acknowledge delivery.
  - The child-side host bridge is named with a narrow, non-administrative
    capability (plan §3.1).
  - Plan §4.2–§4.4 and §6.1, and T15/T16, are consistent with this.
- **C3 — Resolved.**
  - terminal-modes §5 and plan §6.4 step 5 forbid automatic termination of a
    live interactive TUI. Recovery either verifies and rebinds the session, or
    leaves it live, blocks machine work, and marks it `needs_reconciliation`.
    Stopping the session requires explicit human intent.
  - Kill-on-close, process-group, and Job Object teardown are scoped to
    explicitly approved headless policy.
  - Completion during an outage must be replayable, preferring the backend's
    durable record and otherwise using a bounded private spool. If neither
    works, the pair is unsupported or reconciliation is explicit, with no
    zero-loss claim.
  - T10, T17, poc L06, and the roadmap M0 exit gate cover this.

### 8.2 Moderate findings

- **M1 — Resolved.** `run_reconcile` (plan §6.4, §7) defines authority,
  idempotency, the allowed outcomes, a new key for any new attempt, and the
  rule that a DB status flip does not release physical ownership. C31 covers
  it.
- **M2 — Resolved.** Cursors bind to a durable team-role principal with one
  fenced active consumer generation, and stale read/ack is rejected (plan §5.4,
  `consumer_cursors`, C32).
- **M3 — Resolved as an M0 candidate.**
  - Credentials/correlation: the private single-use nonce-file bootstrap is
    defined, and environment inheritance is not assumed (plan §3.3,
    terminal-modes §6, C25).
  - Lead reattach: it uses authenticated local delegation, never a
    self-asserted role name. The same-OS-user limitation is stated.
- **M4 — Resolved.** The platform × mode × production-launch-context matrix
  and a setup doctor that runs in the daemon's real context are in
  terminal-modes §3, plan §3.2, poc P0, and roadmap M0 item 13.
- **M5 — Resolved.** `job_interrupt` and `agent_stop` are separate. The
  interactive default keeps the tab. Termination on deadline requires an
  accepted escalation policy. Unverified outcomes are reported as
  blocked/unconfirmed (plan §7, §8; C21/C22; T08).
- **M6 — Resolved.** Approval observation is an explicit adapter capability
  or `unsupported` (plan §4.4, §8). L05 now covers both Linux modes, and T18
  was added.
- **M7 — Resolved (Option A).** T04/T07/T10 run per platform from a temporary
  production-style context. The Linux daemon runs outside Herdr and outside
  the killed client tree (terminal-modes §6, plan §3.2, poc §2 and demo
  step 1).
- **M8 — Resolved.** L03–L06 state both Linux modes. L10/L11 and all gate
  lists now read T01–T12/T15–T19 consistently, with C01–C32 across plan, poc,
  and roadmap. T13–T14 remain pre-MVP service gates, which is consistent.
- **M9 — Resolved.** README, HANDOFF §1/§8, and roadmap §1 acknowledge
  exploratory non-.NET spike code as non-production and unverified. No runtime
  absence is claimed for that tree.

### 8.3 Optional suggestions

- **Adopted as recorded:** S2–S7.
- **S1:** declined as a scope cut, with an honest measurement-gap rule
  instead. Acceptable.

### 8.4 New observations (non-blocking)

None of these contradicts a fixed user requirement or reopens C1–C3.

1. **Timing of production-context runs.** Roadmap M0 item 13 says to run
   T04/T07/T10 per platform from a production-style context. Plan §3.2 and
   poc §2/§9 place the same runs in the *full PoC*. State whether M0 only
   probes this (with blocked platforms allowed) and M1 is the gate, so the two
   exit gates cannot be read differently.
2. **`foreign_busy` table placement.** Plan §6.1 lists `foreign_busy` in the
   job evidence-level table, but §5.2 and §3.4 define it as an agent/session
   state. Mark it as a session state in §6.1 so the M0 enum does not treat it
   as a job status.
3. **Precision of C01.** The C01 expectation "at most one attempt-start marker
   per generation" is weaker than intended. The invariant is that a retried
   acceptance creates no additional attempt: at most one attempt per job until
   explicit reconciliation.
4. **No test that a child cannot reattach as lead.** No named negative test
   shows that a child-scoped capability cannot call `team_attach` as the lead
   role. C24 covers mismatched IDs, not this case. Consider extending C24 or
   C32.
5. **Status of the spike tree under the language rule.** The status notes
   could add one sentence that, under the `AGENTS.md` C#/.NET rule, the non-.NET
   spike code cannot be promoted and does not satisfy an M0 spike deliverable.
   At most it can be an unverified observation. This is a clarification, not a
   policy change.

### 8.5 Result

- **Resolved:** C1, C2, C3, M1–M9. S1–S7 are dispositioned.
- **Pending:** none blocking. Observations 1–5 in 8.4 are recommended editorial
  follow-ups and may be handled during M0 ADR work.

**Verdict: approved for M0 investigation.** The documents may be used as the
plan for M0 contracts and risk spikes under `AGENTS.md`. This approval does not
cover runtime implementation, production code, or any support claim. It is not
evidence that any build, test, or live gate has passed. The M0 exit gate still
requires its own independent review of the resulting ADRs and revised plan.
