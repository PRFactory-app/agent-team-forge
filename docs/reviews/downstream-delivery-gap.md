# Downstream delivery and startup diagnosis: ATF gap audit

2026-09-26. ATF baseline: `308572e0377ff89d71093150aee6b9edcf0a7144`.
Reference: read-only `/home/mikael/code/github/agentic-coder-teams-mcp`;
local `main` is `c2868be1ca5c84bf6dc0b0b222be7d935671e7ef`, downstream tip
`origin/feat/native-downstream-delivery` is
`e44055ca7a8aae7b160e2caabff7acf05b161462`. Separately inspected merged
[PR #72](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/72),
head `3548cab9a9157bded2266991ad4ba5466de53cf6`.

**ATF already prevents the Linux Codex trust screen and can deliver follow-ups
into retained live TUIs. It does not need the reference's new mailbox/lease
system to obtain those benefits.** Remaining priorities are reliable native
transcript binding, managed-child communication context, and startup visibility.
Notice-only wake is already implemented, with narrower registration guarantees.

## Method and citation key

Read `AGENTS.md` and `reference-lessons-gap.md` first. Read PR #72's description,
diff and checked-in implementation review/dispositions. `gh pr view --comments`
returned no comments; JSON and the review-comment API confirmed empty comments,
reviews and inline comments. Every GitHub call used `GH_TOKEN` obtained explicitly
with `gh auth token --user mikaelliljedahl` (installed binary, avoiding the local
wrapper). Inspected reference `log/diff main...origin/feat/native-downstream-delivery`
and focused source/tests at its tip; no checkout, fetch or reference writes.

`R:` means downstream-tip paths; `P:` means PR #72 head. Both use paths relative
to the reference root. ATF abbreviations: `B:` =
`src/AgentTeamForge.Business/Features/`, `D:` =
`src/AgentTeamForge.DAL/Features/`, `H:` =
`src/AgentTeamForge.Host/Features/`, `T:` =
`tests/AgentTeamForge.Tests/Features/`. Line numbers are pinned to these snapshots.
Verdicts concern source/test coverage, not a new live reproduction. No build,
tests, binary, setup, agent launches, push or merge were run; only this report
is changed. Reference smoke claims are not ATF validation.

## Problem-by-problem verdicts

### 1. A trust/login screen looks like an idle, unresponsive worker

**Reference:** PR #72 records launch time *before* spawn/resume, actual hook
capability and interactive mode. A shared helper reports missing post-launch
markers after 45 seconds, with a hedged backend-specific hint. It changes neither
state nor stall semantics. See `P:src/claude_teams/server_simple.py:297`, `:3521`,
`:4856`, `:6364`; `P:tests/test_startup_diagnosis.py:89` covers the predicate,
legacy records, clock changes and consistent status surfaces. Review fixes include
raising optional capabilities and pre-resume timestamp ordering. **PR #72 does
not implement its planned Part B trust override.**

**ATF: Solved for the Linux Codex folder-trust cause; Partially for diagnosis.**
`B:Agents/Terminals/HerdrAgentControl.cs:185` already passes a per-invocation TOML
`projects={...trust_level="trusted"}` override, with escaping at `:235`.
`T:Agents/Terminals/HerdrInteractiveBackendTests.cs:343` tests a quoted/dotted cwd.
Readiness requires an actual input editor, not idle classification
(`HerdrAgentControl.cs:54`, `:147`; tests `:354`). Headless Codex uses `exec`
(`B:Agents/Backends/CodexExecBackend.cs:55`).

Other startup failures receive only a generic readiness/delivery reason after
bounded waits (`HerdrAgentControl.cs:68`,
`B:Agents/Terminals/HerdrInteractiveBackend.cs:131`, `:198`;
`InteractiveStartup.cs:5`). `B:Jobs/ListJobs.cs:45` exposes activity and durable
status, not startup progress or a hint. Copy the diagnostic principle, not hook
files: ATF's Linux path does not use those markers. Slice 3 below.
Windows's separate builder lacks the trust override
(`B:Agents/Terminals/WtTabControl.cs:290`); do not claim cross-platform closure.

### 2. Follow-up unnecessarily kills/restarts an otherwise usable live session

**Reference:** select `codex_queue` for a verified idle interactive Codex thread,
or `claude_mailbox` for a verified interactive Claude child; Pi/headless/ineligible
targets retain resume. Eligibility is rechecked under the lease. See
`R:src/claude_teams/server_simple.py:3315`, `:3461`, `:5560`, `:6069`, `:6401`;
`R:tests/test_native_codex_dispatch.py:189` and
`R:tests/test_native_claude_dispatch.py:145` exercise in-place delivery.

**ATF: Solved for retained Linux interactive sessions.**
`B:Agents/Terminals/HerdrInteractiveBackend.cs:47` takes the retained session;
`:272` retains settled TUIs. Matching model/effort uses the existing pane;
changed selection deliberately starts a replacement. Prompt delivery verifies
ownership and readiness (`HerdrAgentControl.cs:47`). Tests cover interrupt reuse,
selection changes and settled reuse
(`T:Agents/Terminals/HerdrInteractiveBackendTests.cs:106`, `:135`, `:310`).
This also works for Pi; no new Codex queue carrier is required.
Retention is bounded and in-memory (`RetainedSessions.cs:9`, `:40`), not a promise
to reuse every tab across daemon restart. Uncertain restart work is quarantined
(`D:Jobs/JobStore.cs:421`), not silently replayed.

### 3. A busy Claude child cannot receive deferred work without polling/interruption

**Reference:** a private durable mailbox lets its *own* MCP poster wait for an
idle transition, then post into its own Claude channel. Atomic take/begin/finish
states distinguish no-write from possible-write; only unbegun offers can be
withdrawn safely. See `R:src/claude_teams/delivery_mailbox.py:644`, `:678`,
`R:src/claude_teams/delivery_poster.py:99`, `:259`, and recovery tests
`R:tests/test_native_claude_dispatch.py:365`, `:385`, `:532`.

**ATF: Not solved as a feature; intentional current busy refusal.**
`B:Jobs/FollowUpJob.cs:43` accepts a running parent only with explicit interrupt;
`T:Jobs/FollowUpJobTests.cs:120` verifies refusal. `H:Jobs/JobsMcpBridge.cs:185`
routes `send_message` only to external members, not managed children. This is
not lost accepted work. If deferred follow-up is wanted, optional slice 5 can
use existing queued jobs; do not import a child mailbox/poster service.

### 4. An uncertain native send is repeated through another transport

**Reference:** persist method and frozen carrier before sending; a native queue
may survive process death/reboot. N5 blocks later sends across keys, senders,
flag changes and same-name replacement until the original receipt resolves it.
Only proven non-enqueue permits normal resume fallback. Operator release explicitly
does not cancel the queued message. See `R:src/claude_teams/delivery_store.py:286`,
`:404`, `R:src/claude_teams/server_simple.py:6069`, `:6162`, `:6196`, `:6212`;
tests `R:tests/test_native_selection.py:478`, `:534`, `:612`, and
`R:tests/test_native_codex_dispatch.py:474`, `:519`.

**ATF: Solved for its present job transports; durable native-queue recovery is
Not applicable today.** Claim commits precede effects, session-wide fences block
sibling dispatch, and restart never requeues attempted work
(`D:Jobs/JobStore.cs:180`, `:395`, `:421`). Herdr submits once and waits for the
correlated native user/completion record, never resending on silence
(`B:Agents/Terminals/HerdrInteractiveBackend.cs:122`, `:163`, `:184`;
tests `T:Agents/Terminals/HerdrInteractiveBackendTests.cs:470`, `:490`,
`T:Jobs/SessionFenceTests.cs:86`).
Do not reuse process-exit reconciliation for a future durable native queue:
`B:Jobs/FollowUpJob.cs:65` currently clears a fence on proven process absence.
If adding that carrier later, persist its identity/outcome in DAL and keep its
fence until receipt or explicit release. This is a condition of that feature,
not a separate delivery framework needed now.

### 5. Late hooks, repeated Stop events or inherited identity authorize the wrong turn

**Reference:** epoch/session-bound `idle_seq` counts transitions, not duplicate
Stop hooks. Epochs survive same-name reuse, exceed persisted marker epochs, and
are explicitly cleared when a child must not inherit its parent's epoch.
See `R:src/claude_teams/hooks.py:132`,
`R:src/claude_teams/server_simple.py:3048`,
`R:src/claude_teams/backends/process_base.py:117`; tests
`R:tests/test_hooks_idle_seq.py:50`, `:78`, `:100` and
`R:tests/test_native_selection.py:1279`, `:1368`.

**ATF: Solved for stale run writes; Partially for native transcript identity.**
SQL updates require run ID/generation/correlation and current state
(`D:Jobs/JobStore.cs:225`, `:239`, `:378`); mismatched evidence is ignored
(`B:Jobs/DispatchJob.cs:465`). No Linux hook-counter protocol needs porting.
But fresh and reused fresh-launch panes still rediscover transcripts using a
marker across at most 200 candidates; two matches yield no binding, and only
`launch.ResumeSessionId` constrains identity
(`B:Agents/Terminals/InteractiveTranscriptReader.cs:21`). Reuse passes the original
launch (`HerdrInteractiveBackend.cs:52`), whose resume ID can remain null even
after learning the native ID (`:178`). Native subagent history can therefore
invalidate attribution. Existing transcript tests cover turn boundaries and
partial lines, not proven parent/native-child identity
(`T:Agents/Terminals/InteractiveTranscriptReaderTests.cs:99`, `:135`). Slice 1.

### 6. A Codex lead misses child replies, or a stale registration wakes the wrong thread

**Reference:** explicit lead registration, host PID/creation identity,
generation tombstones, provisional spawned-lead registration until corroborated
by parent binding, and body-free notices. Revalidate and queue under the
registration lock. See `R:src/claude_teams/native_wake.py:445`, `:768`, `:853`,
`R:src/claude_teams/server_simple.py:4746`; tests
`R:tests/test_codex_lead_wake.py:130`, `:238`, `:318`.

**ATF: Solved for basic job/external wake; Partially for registration lifecycle.**
Automatic and explicit Codex registration already exist
(`H:Wake/HostSessionWake.cs:11`, `:72`; `H:Jobs/JobsMcpBridge.cs:241`). Durable
unread scans, coalescing, generation checks and backoff are implemented
(`B:Wake/WakeCoordinator.cs:54`, `:89`; `D:Wake/WakeStore.cs:102`). Late binding
and resumed-lead catch-up are tested
(`T:Sessions/LeadSessionTests.cs:72`, `:90`; `T:Wake/WakeTests.cs:49`, `:117`).

There is no host-incarnation or parent-bound-thread field in
`D:Wake/WakeStore.cs:5`; verification proves a thread exists, not that it belongs
to this spawned lead (`H:Wake/HostSessionWake.cs:71`). The explicit lead tool
cannot clear a registration. A routing change can also occur between
`WakeCoordinator.cs:89` and `:98`; its later generation CAS prevents stale
bookkeeping, not an already queued stale notice. External clear removes message
routing but does not invalidate an already captured target
(`B:External/ExternalTeam.cs:228`). Slice 4. Notices contain counts, not job bodies;
this is routing/reliability debt, not a claim of result disclosure.

### 7. Nested agents inherit the wrong endpoint, flags or tool family

**Reference:** explicitly propagate feature flags in per-child MCP configuration
and backend environment, sanitize inherited channel/epoch identity, and explain
native downstream versus upstream inbox semantics. See
`R:src/claude_teams/native_wake.py:61`,
`R:src/claude_teams/server_simple.py:396`, `:2867`,
`R:src/claude_teams/backends/process_base.py:105`.

**ATF: Partially.** The daemon owns launch policy; Herdr's denylist prevents
reference/session environment leakage (`B:Agents/Terminals/LaunchEnvironment.cs:20`).
But `B:Jobs/DispatchJob.cs:428` passes no authenticated parent context/endpoint,
and the child bridge creates a lead binding from parent/cwd
(`H:Jobs/JobsMcpBridge.cs:124`). There is no managed upstream route merely because
the daemon has a wake adapter. Slice 2 must establish that route before adding
tool hints or claiming nested wake is complete. Copying reference flags alone
would not solve it.

### 8. A queue exit code is mistaken for enqueue proof; transport errors are ambiguous

**Reference:** `QueueOutcome` separates process construction, started/uncertain
and parsed submission ID; payload preflight checks UTF-8/command budgets and
rejects unsafe shims. See `R:src/claude_teams/native_wake.py:962`, `:1013`, `:1083`,
`R:src/claude_teams/server_simple.py:3345`; tests
`R:tests/test_codex_queue_runner.py:66`, `:104`, `:113` and
`R:tests/test_native_codex_dispatch.py:627`.

**ATF: Partially for wake reporting; Not applicable to downstream task payloads.**
`B:Wake/CodexQueueWake.cs:85` bounds the command and closes stdin, but `:118`
accepts exit zero without parsing a submission ID. `IWakePoster` returns only
bool (`B:Wake/WakeCoordinator.cs:5`). A false success can suppress notices until
renotify; uncertain retries may duplicate notices, but never consume job results
or replay tasks. Slice 4 should validate enqueue proof. Keep the current
notice-only API out of task delivery; native task delivery would also need the
fences described in item 4. No large-message queue transport needs porting now.

### 9. Windows Claude pipe writes hang or outlive cancellation unsafely

**Reference:** verify server PID; use overlapped writes, bounded cancellation
drain, retained buffers for outstanding I/O and atomic per-pipe reservations.
See `R:src/claude_teams/winpipe.py:238`, `:259`, `:314` and
`R:tests/test_winpipe_logic.py` / `test_winpipe.py`.

**ATF: Not solved on Windows; Not applicable to the Linux transport.**
`B:Wake/ClaudeChannelWake.cs:13` explicitly supports Linux only; Linux socket
connect/write share a cancellable five-second deadline (`:20`). Its wire format
is tested at `T:Wake/WakeTests.cs:229`. Defer a Windows-only slice: add a C# named
pipe adapter in Business, nearest-host validation in Host, and stalled-reader,
cancellation and server-PID tests on Windows. DAL changes are unnecessary for
notice-only transport. Do not let this block Linux work.

## Ranked small slices

1. **P1 — Bind retained interactive turns to a verified native session.**
   Business: `InteractiveTranscriptReader`, `HerdrInteractiveBackend` and retained
   session metadata. Establish the parent from launch/native ancestry evidence,
   then preserve its ID/path for every follow-up; never choose the newest match.
   Tests: inherited-marker native child, child appearing after first binding,
   reused fresh-launch pane, >200 decoys and true ambiguity. No Host/DAL redesign.
   Direct overlap with earlier **slice 2**; incremental reads remain its separate
   P2 **slice 6**.

2. **P1 — Supply managed-child parent membership and accurate routing.**
   Host: bridge bootstrap/configuration; Business: dispatch/backend launch context;
   DAL: reuse existing external member tickets and lead-session relations.
   Inject a private endpoint/state-dir plus parent membership on spawn/resume,
   keeping nested-lead identity separate. Then provide qualified ATF tool names
   and the supported upstream/downstream behavior. Test report-to-parent and two
   independent nested leads without cross-routing. This is earlier **slice 3**,
   not a new message bus; corroborate spawned-lead wake with the verified binding
   once those identities exist.

3. **P2 — Expose startup progress before generic reconciliation.**
   Business: emit starting/ready/submitted evidence and a bounded, hedged startup
   hint; DAL: retain minimal per-run timing/phase alongside existing `started_at`
   and `acked`; Host: project it consistently in get/list and the console.
   Do not introduce hook installers or infer success/death from missing evidence.
   Tests: delayed readiness, trust/login-like screen, evidence emitted during
   Start, resumed turn and unsupported diagnostics. Keep the existing Linux trust
   override. Extends earlier **slice 5** inspection work; new startup-specific gap.

4. **P2 — Tighten notice registration and enqueue confirmation.**
   Host: explicit clear/replace and registration status; Business: serialize
   routing changes with posting for that target, validate Codex submission ID;
   DAL: generation-aware tombstone/invalidation and unread rebinding. Do not hold
   a database transaction during subprocess I/O. Test clear/replace during a
   delayed post, valid thread from the wrong spawned lead (after slice 2), exit
   zero without submission ID, and restart catch-up. Keep retries notice-only.
   New refinement of earlier covered wake mechanics, not a downstream queue.

5. **P2, optional — Queue follow-up behind a busy ATF turn.**
   Host: explicit deferred option on existing follow-up; Business: admit it only
   once the native session is known; DAL: reuse existing dispatch intent/session
   serialization. Define cancellation and uncertain-parent behavior by keeping the
   child queued/fenced, never interrupting implicitly. Test parent completion,
   parent uncertainty, TTL/stop and restart with one eventual dispatch. This
   deliberately extends the old report's busy-refusal contract; it is not required
   to fix that contract. No Claude mailbox or new MCP poster.

## Changes since the earlier audit and limits

Earlier **slice 1** is already fixed for Linux:
`B:Agents/Backends/OrphanedBackendProcess.cs:45` conservatively checks known PIDs;
`T:Jobs/SessionFenceTests.cs:15` covers unreadability across restart and proven
exit/reuse. Earlier **slice 4** also has a current implementation: SQL paging and
byte-budget selection precede cursor advancement
(`D:External/ExternalMemberStore.cs:448`, `:482`, `:503`). Do not reopen either
based on the older snapshot.

The downstream branch is substantial new machinery, not proof that ATF needs
the same machinery. Its Codex busy restriction remains explicit
(`R:src/claude_teams/server_simple.py:3351`); its native carriers do not include Pi.
Keep ATF's current SQLite job ownership, native turn evidence, explicit interrupt
and manual read fallback. If durable native task queues are later introduced,
the earlier report's advice against delivery barriers must be narrowed: process
death cannot discharge a message that survives in another durable queue.
