# AgentTeamForge — architecture and implementation plan

## 1. Purpose and decision status

Build a small local execution engine for agent teams, controlled by a lead
agent through MCP but independent of that agent's process. An optional future
connector should let an external orchestrator submit assignments and receive
results through the same core.

This document describes a **proposed design**, not implemented features. The
PoC must challenge risky assumptions before product development continues.
Decisions needing verification are identified as preliminary or risk spikes.

### Product goals

1. A client crash must not destroy accepted assignments or completed results.
2. A lead agent should control several Claude/Codex agents through small,
   understandable tool calls and durable identities.
3. Orchestration should work without an external orchestrator, network access
   to a central service, or model-based status polling.
4. .NET should provide a maintainable codebase for a developer familiar with
   .NET.
5. SQLite should replace custom transaction handling across many small state
   files.
6. Native AOT should be tested early, not become a late distribution surprise.
7. An external orchestrator should be able to connect later without replacing
   the local domain model.
8. Setup must require a choice of interactive or headless launch mode.
   Interactive means Herdr on Linux, a visible terminal tab on Windows, and
   the equivalent on macOS. This is a fixed user requirement; see the
   [terminal requirements](terminal-modes.md).

### Not goals for the first release

- Recreating all of Claude Code's team features or building a custom model
  agent loop.
- Replacing Claude/Codex tools, authentication, sandbox, or context management.
- Distributed scheduling, multi-tenant hosting, or remote access to the
  daemon's administrative API.
- Full compatibility with other orchestration tools and their disk formats.
- Guaranteeing exactly-once execution of arbitrary agent actions.
- Requiring the Desktop app or attaching every agent to an arbitrary, already
  open session. Visible terminal sessions launched by AgentTeamForge are in
  scope.
- Making an external orchestrator, EF Core, a message broker, or a web UI mandatory.

## 2. Evidence and current state

This plan defines local runtime contracts, not conclusions established by
another project's implementation. Public official protocol and SDK references
are listed in [Sources](#14-sources). M0 must record exact versions and verify
the proposed behavior on the actual platform and launch path.

### 2.1 Durable coordination requirements

A permanent dispatcher must own accepted work independently of tool-call time
budgets and client lifetimes. SQLite transactions coordinate job state,
idempotency, queued dispatch, and results; backend-owned files remain separate.

A PID is not sufficient identity, a timeout is not evidence of failed delivery,
and a missing acknowledgment must not automatically trigger redelivery. Encode
these invariants in tests rather than preserving another system's file formats.

### 2.2 Native wake and interactive control

Treat wake, message storage, backend control, and authoritative completion as
separate capabilities. **Native session wake is the standard mechanism**, based
on public [PR #70](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70):
Claude owning-session inbox notices and registered Codex thread wake through
`codex queue`. Unlike that reference's opt-in rollout, verified native wake is
our normal path. Its reported Linux evidence is not proof that an independent
daemon can control every interactive host or that Windows/macOS/Pi transports
exist. Those require separate implementation and native-platform qualification.

A notifier may need to remain within the host's own process tree. Test whether
a lead-side or child-side bridge is necessary and how that bridge receives its
scoped authority. Environment/socket availability must be verified rather than
assumed to be a stable public contract. Linux observations do not establish
Windows or macOS support.

### 2.3 Local-first implementation constraints

Use thin MCP-to-local-IPC bridges and daemon-owned dispatch, process supervision,
and recovery. Only the daemon owns structured state. Local recovery must not
require a central ownership service or account; optional remote-job rules stay
behind the connector boundary.

A hosting framework does not by itself establish OS-service installation, GUI
access, or client-independent process lifetime. Self-contained/single-file
publication does not establish Native AOT compatibility. Prove each property
through the corresponding platform and published-binary gates.

## 3. Architecture

```text
Claude / Codex / other lead agent             Local CLI / future UI
               │                                   │
        thin stdio MCP bridge                      │
               │                                   │
               └──────── authenticated local IPC ──┘
                                  │
                     AgentTeamForge daemon
                     ├── API and identity
                     ├── Team / Agent / Job / Run
                     ├── Scheduler and dispatcher
                     ├── Inbox / events / approvals
                     ├── SQLite and delivery journal
                     ├── Process supervision
                     └── Backend and launch adapters
                          ├── Interactive Claude/Codex + terminal provider
                          └── Headless: Codex app-server / Claude stream-json
                                  │
                         worktrees / logs / files

      Future optional external orchestrator connector ──► same application API
```

### 3.1 Process boundaries

- **Daemon:** one instance per user and machine. Owns the queue, DB, scheduler,
  and agent process I/O. Holds an exclusive OS lock for the active instance.
- **MCP bridge:** a small client process per host where stdio is required.
  Translates tools, binds identity, and adapts events/wake; it has no job queue.
- **Child-side host bridge (candidate):** a small helper inside an interactive
  child's host/process context when native own-child rules require it. It has a
  child/session-scoped capability for lifecycle, follow-up, result, interrupt,
  and approval signals only; it cannot administer teams or other agents.
- **Backend process:** Claude/Codex performs its own agent work outside .NET.
  The daemon's runner directly owns a headless process. A terminal server may
  own an interactive process; the daemon then owns the job and a verified
  lifecycle binding, but not necessarily stdin/stdout or the OS parent role.
- **Terminal provider/session launcher:** Herdr on Linux, a visible terminal
  tab on Windows, and the selected equivalent on macOS. Separate from the
  backend adapter. A launcher in the desktop session may be needed; it must
  not become the durable job owner.
- **Connector:** separate logical module; decide its process boundary later.
  It must never import an external orchestrator's domain model into the daemon core.

One binary with subcommands is a possible distribution, not a requirement
that everything run in one process. Working names: `atf daemon`, `atf mcp`,
`atf status`. None of the example commands in this plan exist yet.

### 3.2 Service lifecycle

Run the product under the user's account for the correct HOME, authentication,
and repository access. Do not default to root/LocalSystem.

- Linux target: `systemd --user`; document logout behavior and any linger
  requirement separately from the client-crash survival promise.
- Windows target: choose user context and autostart/service approach after the
  PoC. Test Session 0, profile access, CLI login, and visible-tab launch.
- macOS target: launch tied to the user/GUI session, perhaps a LaunchAgent
  after verification. Select terminal provider and any automation permissions
  in M0.
- PoC: a standalone foreground daemon suffices for early client-crash tests.
  The full PoC also runs T04, T07, and T10 once per platform from the intended
  production launch context; a temporary user service unit, logon task, or
  LaunchAgent is sufficient, and no full installer is required. The Linux
  reference daemon runs outside Herdr and outside the killed client's process
  tree. Minimal setup with a persisted mode is included; full service
  installation remains M3.

Drain headless output even with no clients. Interactive output goes to the
real terminal; the daemon separately consumes the control/result channel. A
slow client must not block the backend or its UI. GUI unavailability produces
an explicit error/wait state, not a fallback to another mode.

M0 records a platform × launch mode × intended production launch-context matrix
covering `PATH`, home/profile, backend config, credential-store access, and
authentication. Setup includes a doctor that tests the chosen combination in
the context the daemon will actually use.

### 3.3 Local IPC

Preliminary choices:

- Unix domain socket on Linux and macOS.
- Named pipe with a user-restricted ACL on Windows.
- Versioned JSON messages with source-generated serialization.
- No open TCP ports in the first release.

The handshake must state protocol version, client role, and capabilities.
Reject incompatible versions with a concrete error. Read/write limits,
timeouts, and a maximum message size are mandatory. Do not send large
artifacts inside IPC messages.

OS user identity and private runtime directories provide transport access.
Bind team/agent permissions separately to a session/capability, not to a free
`agent_name` on each call. Secrets must not appear in argv or logs. Set the
exact bootstrap protocol and credential storage in M0.

For terminal-launched children, do not assume provider-specific environment
inheritance. The M0 bootstrap candidate is a private, short-lived, single-use
nonce file in the user's runtime directory, exchanged locally for a
child/session-scoped capability and then invalidated. Restarted operators or
leads reattach through authenticated local operator delegation, never by
presenting an arbitrary role name. No secret may appear in argv or logs.

The same OS user can often read process environments and files. Do not market
capability tokens as a sandbox against a malicious agent with that user's full
file and process access.

### 3.4 Scheduling and ownership

- One active turn per agent conversation in the PoC; queue later jobs FIFO.
- Human or otherwise uncorrelated activity is a distinct observed event and
  sets the interactive session `foreign_busy`. The conservative v1 policy is
  human-wins: pause automatic follow-ups until an authorized lead/operator
  explicitly reconciles verified idle. Database fencing cannot prevent
  physical terminal input.
- Global and per-team concurrency limits; at most two active agents in PoC.
- Stable `agent_id`, distinct from display name, PID, and backend session ID.
- The daemon first returns a durably accepted job, then dispatches it.
- Client disconnection is not a cancellation signal for an accepted job.
  Server jobs must not inherit the MCP request's cancellation token.
- Separate idempotent turn-interrupt and agent-stop calls are required. Calls
  whose responses are lost can be recovered with a caller-supplied idempotency
  key.
- The daemon executes only approved jobs under defined queue rules; it is not
  a model-based planner. A lead-agent crash does not secretly start a new lead.

Store agent parent/child relationships and team membership explicitly. The
PoC supports one lead and direct children. Defer unlimited nesting.

## 4. Backend strategy

### 4.1 Two integration modes

**Managed:** AgentTeamForge launches and manages the agent's lifecycle in the
selected setup mode. It may be a real interactive TUI in a terminal or
headless. Headless provides direct I/O; interactive mode needs separate
terminal binding and verified control/result transport. Both are first-class.

**Attached:** an independently launched terminal/Desktop session connects
later. This is a different, later feature. An external host that closes cannot
be guaranteed to keep working simply because the daemon remains alive.

Backend, launch mode, terminal provider, and control transport are separate
capabilities. See the [setup and terminal contract](terminal-modes.md); a log
tab does not count as an interactive session.

### 4.2 Codex

**Headless candidate:** the documented app-server protocol, with explicit
thread and turn IDs, events, turn completion, interrupt, and resume.

**Interactive candidate:** Codex TUI in the selected terminal, with explicit
session binding, verified native follow-up, and a result channel. `codex queue`
is a candidate, not proof of full delivery/completion semantics. Do not assume
the app-server can take over an arbitrary TUI already running.

Keystroke injection/raw terminal input simulation is not a supported v1
delivery transport and cannot acknowledge delivery. A backend × provider pair
without a verified native/control path remains blocked unless a later explicit
ADR and user scope decision changes this rule.

M0 must verify against the exact installed version:

- Startup/handshake, persistence, and session identification.
- Streaming output, completion, and backend errors.
- Approval and user questions.
- Closed transport, interrupted turn, and restart/resume.
- Multiple threads and concurrency behavior.
- Whether another UI client can access sessions; do not assume this.

The PoC favors an isolated runner per active agent if that simplifies ownership
and failure isolation. Multiplexing in one app-server is a later optimization
driven by measurements. Test `codex queue` for interactive managed wake and
later attached wake; it is not the default headless transport.

### 4.3 Claude

**Headless candidate:** documented CLI with `-p`, `--input-format stream-json`,
`--output-format stream-json`, explicit session ID, and resume.

**Interactive candidate:** real Claude TUI in the selected terminal, native
messages, and, if needed, a host-local bridge for own-child wake. Status and
results need their own verified signal. `-p` plus a log view does not meet the
interactive requirement.

Verify real support for multiple turns, user-message acknowledgments, final
results, interruption, and approval behavior. JSON accepted on stdin does not
make the entire Agent SDK internal control protocol a public contract.

If a necessary feature has no documented protocol:

1. Document the exact gap and version.
2. Test a limited CLI-based variant.
3. Consider a separate official SDK adapter only after cost/AOT assessment.
4. Stop or explicitly change scope if safe permission handling is unavailable.

Do not make a large .NET port of an undocumented SDK or quietly depend on
Python or Node while claiming this remains a single AOT binary.

### 4.4 Backend capabilities

Adapters expose capabilities rather than pretend their functionality is
identical:

- Start/resume a session.
- Start a turn/observe the final result.
- Interrupt a turn.
- Stop an owned agent/process under an explicit policy.
- Delivery acknowledgment and correlation.
- Approval visibility and user-input support. An interactive adapter must
  expose a normalized approval-blocked state or declare approval observation
  `unsupported`; it must not fabricate `running` or bypass approval.
- Continue a session after process restart.
- Interactive launch, verified terminal binding, and human input.
- Possible control during an active turn and attached wake.

A missing capability yields explicit `unsupported`, never simulated success.
Record versions in diagnostics. Give each adapter contract tests with
normalized fixtures.

For interactive completion, authoritative evidence is a documented backend
lifecycle signal or a verified durable backend session/transcript record,
correlated to the run generation and backend turn. A model-invoked self-report
is informational only. Terminal output, silence, or tab presence cannot set
`completed`.

### 4.5 Waking the lead agent

An MCP notification does not necessarily cause a host to start a new model
turn. The daemon stores results/events regardless of wake capability.

The standard path uses a thin host-local bridge subscribed to durable unread
state and a verified native host transport. Claude's own-child requirement must
be verified against the nearest owning host; inherited socket variables are
scrubbed for spawned/resumed children. Codex wake registrations bind authorized
principal, thread/home and generation. Never redirect to an inherited foreign
session or accept a request ID as host ownership.

One fenced notifier per reader sends short notice-only wakes without message
bodies or credentials. Coalesce bursts, bound subprocess/I/O time and retries,
back off after failures and catch up on reconnect. Commit durable messages
before wake; a wake error cannot fail an already committed send. Transport
success neither acknowledges messages nor proves a model turn occurred.

If the lead agent is disconnected, the result remains stored. The next
connection can read it even if no wake was sent. Do not silently fall back to
tight model polling.

The PoC includes a separate Linux wake spike. Full-product P01 freezes and
proves required host/platform transports; P04/P06 implement native integration;
P05/P07 qualify installed support, including Windows and separate Pi host evidence.
Manual read/catch-up is explicit degraded recovery, not a silent replacement
for required native wake. Watchers and repeated model polling are not the
standard mechanism. Missing required transport blocks its support cell, not
removed by declaring SQLite/MCP sufficient.

## 5. Domain model and SQLite

### 5.1 Concepts

- **Team:** shared work and permission boundary.
- **Agent:** stable participant with a backend, parent, and conversation binding.
- **Job:** accepted assignment; may be an initial prompt or follow-up.
- **Run:** concrete execution attempt for a job, with its own generation and
  backend turn ID where available.
- **Message:** communication between participants, separate from job status.
- **Event:** ordered change notice that clients can read again.
- **Observed activity:** human-origin or otherwise foreign activity correlated
  to an interactive session but not attributed to a daemon job/run.
- **Approval:** explicit wait for permission or a user decision.
- **Artifact:** file reference and metadata, not the whole file in normal tool
  responses.

### 5.2 Proposed tables

| Table | Important data / invariants |
| --- | --- |
| `teams` | ID, name, workspace policy, creation time. |
| `agents` | ID, team, parent, backend, session ID, generation, desired/observed state, including `foreign_busy`. |
| `jobs` | ID, sender, target, payload, policy, idempotency key, payload hash, status. |
| `runs` | Job ID, generation, backend thread/turn ID, start/end, result, and error. |
| `deliveries` | Attempts to deliver an instruction to a backend, correlation, acknowledgment, and uncertainty. |
| `messages` | Stable ID, team, sender, recipient, text, creation time. |
| `events` | Monotonic sequence number, type, relevant IDs, compact payload. |
| `observed_activity` | Session, origin classification, backend correlation if any, time, and reconciliation state; never silently attributed to a job. |
| `consumer_cursors` | Durable team-role principal, one active consumer generation, and explicitly acknowledged sequence. |
| `approvals` | Run ID, request, status, decision identity, and expiry. |
| `artifacts` | Run ID, file reference, type, size, and optional checksum. |
| `schema_migrations` | Schema number and applied migration. |

Keep the exact schema small. Add tables as needed; the PoC needs no future
connector or fleet tables.

### 5.3 Transaction rules

- SQLite on local disk. No shared DB on NFS/SMB.
- The daemon alone owns the DB; clients read and write through its API.
- WAL, foreign keys, bounded busy timeout, and short transactions.
- `synchronous=FULL` is the preliminary level for durable acceptance. Do not
  use a lower level without openly changing the power-loss guarantee.
- Acceptance transaction: write the assignment, idempotency record, and a
  queued, **unattempted** outbox row in the same transaction before
  acknowledgment. It contains no claim that external delivery began.
- Attempt-start transaction: immediately before any external side effect,
  atomically create the run/delivery attempt with a new generation and
  correlation ID and mark it started. External side effects include process
  spawn, terminal/tab launch, and prompt/follow-up delivery. No such effect may
  precede this commit.
- Write final status, result metadata, and completion event atomically.
- Never hold a DB transaction across a CLI call or model wait.
- DB failure, full disk, and corruption must not be treated as empty state.
- A failed durable write means no newly accepted job and no new external
  dispatch. Already started runs may need reconciliation.

Background dispatch with a transactional outbox can use `jobs` and
`deliveries`; no extra broker is needed. A queued row with no attempt marker is
safe to dispatch. An attempt marker without authoritative backend evidence is
uncertain and is never blindly replayed.

### 5.4 Messages and reading

Reads return stable message/event IDs and a batch cursor. A separate ack
updates the consumer's cursor. Losing a read response must not consume data.
Bind ack to the consumer, the read batch, and its preceding cursor; an
arbitrary future sequence number must not skip unread events. Concurrent
readers and filtering need an explicit sequence contract and negative tests.

Reconnection provides at-least-once display of unacknowledged events. The
cursor belongs to a durable team-role principal, not a socket or bridge
process. Exactly one fenced consumer generation may be active for that role;
a reconnect advances the generation and stale reads/acks are rejected. The
client deduplicates by ID. This does not make the model's subsequent actions
exactly once. Read/ack means received by the client, not that the model
definitely executed the instruction.

Do not mix job commands with informational messages such that the same
instruction both starts as a job and is executed again from an inbox.

### 5.5 Files that remain

- Backend sessions and transcripts: owned by each backend.
- AgentTeamForge logs: rotated, private, and limited retention.
- Worktrees: separate from application state.
- Artifacts and large results: files with DB references.
- Prompt/config files: only where a documented backend need justifies them.

Token deltas need not each become a DB transaction. Buffer within limits and
batch output; final results and lifecycle events are durable checkpoints.
Document that the last unsaved streaming fragment can be lost in a crash.

## 6. Delivery, idempotency, and recovery

### 6.1 Separate evidence levels

| State | What it proves |
| --- | --- |
| `accepted` | The assignment is committed locally and can be found after reconnection. |
| `dispatching` | An execution attempt exists; an external effect may be underway. |
| `backend_acknowledged` | The backend protocol acknowledged it according to the adapter's documented meaning. |
| `running` | The backend signaled an active turn. |
| `foreign_busy` (agent/session state, not job status) | Human/foreign activity is observed; automatic follow-ups are paused. |
| `completed` | A run-correlated authoritative lifecycle signal or verified durable backend record was observed and stored. |
| `needs_reconciliation` | The system cannot determine whether the instruction arrived or how the run ended. |

These are semantics; M0 will define the final API enum/schema. A backend ack
never proves a file change or the user's goal is complete.

### 6.2 Idempotency

Mutating create/job/interrupt/stop/reconcile calls should have a caller-supplied
idempotency key.
Scope uniqueness to principal/team/operation. The same key and semantic
payload return the same operation; a changed payload yields a conflict without
side effects.

The durable payload fingerprint must include target, instruction, and relevant
options. Retain keys and operations longer than the supported retry period.
Retention must not silently let an old retry start a new job; use a tombstone
or reject expired keys under a documented contract.

### 6.3 Crash windows

| Window | Recovery |
| --- | --- |
| Before DB commit | No durable acceptance; the same key may be sent again. |
| After acceptance commit, before response; queued outbox has no attempt marker | Retry finds the same job; dispatcher may create one attempt and continue. This is C07. |
| After attempt-start commit, before/during process spawn, terminal launch, or prompt delivery | Generation/correlation proves an attempt was authorized, but its external effect is uncertain until evidence exists; blind automatic retry is forbidden. This is C08. |
| Backend received work, acknowledgment lost | Reconcile backend session/turn/correlation; otherwise `needs_reconciliation`. |
| Backend finished, DB final write failed | Reread authoritative result if possible; do not guess success or failure. |
| Result stored, client disconnected | Client can read later from its durable cursor. |

The PoC must inject crashes at these boundaries using a fake backend before
testing real agents.

### 6.4 Process recovery

Identify a process with PID plus start identity/creation token and local
generation. Never kill or adopt a reused PID based on PID alone.

A new daemon cannot automatically reopen an old process handle or old
stdin/stdout pipes. PoC policy for managed runners:

1. On restart, mark nonterminal attempts as recovery candidates.
2. Verify whether an old runner exists and can still make external side effects.
3. Reconnect only where the backend has a verified protocol for doing so.
4. For headless runners only, an explicitly approved policy may stop a provably
   owned survivor before allowing a new run.
5. Never automatically kill a live interactive TUI during recovery. Verify and
   rebind it; otherwise leave it live, block future machine work for that
   session/workspace, and report `needs_reconciliation` with an actionable
   reason. Explicit human intent is required to stop it.
6. Resume a conversation only with verified session binding and explicit
   policy. This does not mean automatically rerunning an interrupted command.
7. Reconcile interactive completions produced during the outage from verified
   durable backend session records. If those are insufficient, a bounded,
   private, correlated helper spool may be specified. Without replayable
   evidence, mark the pair unsupported or require reconciliation; never claim
   zero result loss.

Windows Job Objects and Linux process groups/cgroups have different semantics.
Kill-on-close may be an approved headless daemon-crash policy, but must be
tested and documented; it is not an interactive policy. Include grandchildren
and remaining shell commands in headless shutdown and recovery tests.

Generation fencing prevents late status updates in the DB. It does **not**
prevent an old process from writing files; prove physical exclusivity before
a new attempt in the same workspace/conversation.

`needs_reconciliation` is resolved only through an idempotent,
evidence-bound operator/authorized-lead operation. It may accept an observed
authoritative result, abandon the old run without enabling concurrent effects,
or request a **new** attempt with a **new** idempotency key only after safe old
run termination or verified idle. Merely flipping a DB status or releasing a
logical lock never releases physical ownership.

## 7. API and tool budget

Proposed small public tool set for the first usable release:

| Tool | Purpose |
| --- | --- |
| `team_create` / `team_attach` | Create or reconnect through authenticated local delegation to an explicit team-role principal; reconnect fences the prior consumer generation. |
| `agent_start` | Register an agent and accept its first job with idempotency. |
| `agent_send` | Accept a follow-up for an authorized recipient. |
| `agent_list` / `job_get` | Compact status without a transcript by default. |
| `events_read` / `events_ack` | Durable reading and acknowledgment. |
| `job_interrupt` | Explicit, idempotent request to interrupt a job turn; interactive default keeps the agent/tab alive and reports only verified outcomes. |
| `agent_stop` | Separately stop an owned agent when policy and authority permit; never implied by an interactive turn interrupt. |
| `run_reconcile` | Authorized, idempotent, evidence-bound resolution: accept observed result, abandon safely, or request a new-key attempt only after verified idle/termination. |

Keep exact names and the tool count small in the PoC. Add approval tools once
the backend contract is verified. Tool names must not promise push or delivery
if the tool only queues work.

An unknown interrupt outcome is blocked/unconfirmed, never false success.
Interactive deadlines default to interrupting the turn and keeping the session
open. A hard stop is promised only where the accepted policy explicitly permits
escalation to termination of the owned agent; otherwise the deadline can report
blocked/unconfirmed and is not a guaranteed hard cap.

Defaults: limited result count, short summaries, IDs, and file references. A
full transcript requires an explicit request. Tool descriptions should explain
guarantees without carrying a large internal disk protocol into every agent's
context.

## 8. Authorization, budgets, and local security

- Bind workspace and allowed backend/model/policy when creating an agent/job.
- Sender identity comes from the authenticated connection, not the payload.
- A lead may control its authorized children; children must not administer the
  daemon or other teams by changing an ID. Test these boundaries locally too.
- Limit concurrency, queue size, prompt size, output, and log retention.
- Headless hard limits may stop an owned process without model cooperation
  under an explicitly accepted escalation policy. Interactive limits default
  to a turn interrupt; without accepted termination escalation, they report an
  unconfirmed/blocked outcome rather than promising a hard cap.
- Cost/token budgets may only be advisory when the backend does not provide
  sufficiently frequent or precise usage data. Do not promise an unenforceable
  hard spending cap.
- A job may continue without a client only under its accepted policy.
- Approval or a user question becomes an explicit blocking state. Interactive
  adapters must surface it or mark that capability unsupported. With no
  connected decision maker, wait or time out; never report fabricated running
  progress or silently bypass.
- Coalesce and rate-limit wake to avoid feedback loops.
- Start processes with an argument list, not a shell string. Isolate Windows
  `.cmd` special cases; include prompts with newlines/metacharacters in
  contract tests.
- Default to private files and credential redaction. No tokens in tool
  responses, logs, process arguments, exceptions, or exported diagnostics.
- Repository access is not automatic trust in its hooks/plugins/MCP. Choose
  backend discovery and allowed configurations deliberately, not through a
  blanket `bypassPermissions`.

AgentTeamForge is not an OS sandbox. Retain backend security mechanisms and
supplement them with isolated worktrees and OS permissions as needed.

## 9. .NET, AOT, and proposed code structure

Selected baseline for upcoming work: **.NET 11**, superseding the original
.NET 10 LTS proposal. [RC1 research](research/net11-process-api.md) confirms the
public release and candidate SDK `11.0.100-rc.1.26425.128`, with process APIs
checked against tagged source. Installed-ref-pack, package, platform, and AOT
compatibility still require actual tests. Existing .NET 10 observations remain
tied to that version; no running experiment is silently retargeted.

The selected structure is **feature-first vertical slices in three production
projects**, documented in [architecture.md](architecture.md):

```text
src/
  AgentTeamForge.Host/       feature entry points, MCP/IPC, setup, daemon hosting
  AgentTeamForge.Business/   feature rules, orchestration, backend/terminal control
  AgentTeamForge.DAL/        feature storage, SQLite transactions and migrations
tests/
  AgentTeamForge.Tests/      feature tests and bounded process/AOT scenarios
```

Dependencies: **Host → Business → DAL**. Business calls DAL directly; DAL has
no Business/Host reference. Host may reference DAL only for composition, not
endpoint data access. This is not Clean Architecture: no mandatory inward
repository ports, mediator stack, or extra Domain/Application/Infrastructure
projects. Requests, results, helpers, and necessary interfaces stay beside their
feature. Implement each capability end to end rather than build one layer at a
time.

This replaces the earlier Core/Storage/Backends/Terminals/Daemon/MCP/CLI project
proposal. Those remain responsibilities, not separate required assemblies.
Host supplies separate process modes for daemon and thin bridges; three
projects do not imply three processes. Only daemon mode opens the job database.
A future connector or child helper does not automatically justify another
production project.

Technical options to test:

- `Microsoft.Extensions.Hosting` for lifecycle and background work.
- Official MCP C# SDK with the smallest necessary packages.
- M0 must verify whether MCP tool registration is trim/AOT-clean or needs an
  explicit source-generated registration path; do not infer this from JIT use.
- `Microsoft.Data.Sqlite` and parameterized SQL, without requiring a heavy ORM
  at the start.
- `System.Text.Json` source generation for protocol, DB payloads, and MCP data.
- Early AOT publication; no general reflection/assembly scanning for plugins.
- Published platform packages with a verified SQLite native dependency. Do
  not promise “one binary” before packaging and external CLI requirements are
  understood.

Business and DAL must not depend on billing or web UI frameworks, external
orchestrator work-item types, or another system's session files. Official backend
CLIs are external installation requirements. The next bounded experiment is specified in the
[durable-core spike plan](spikes/m0-durable-core-plan.md); it does not replace
the interactive-terminal spike.

## 10. Optional external orchestrator — without owning the local core

Planned future connector:

1. The user enables and pairs the connector with a specified external
   orchestrator instance.
2. The connector receives authorized assignments through an outbound
   connection or polling.
3. External command/work-item IDs map to local jobs and idempotency keys.
4. Backend work uses the same API and policy as locally initiated jobs.
5. Status and approved artifacts return under an explicit data-sharing policy.

Before implementation, establish an ownership matrix:

- Which commands require a valid remote lease?
- Should an already-started remote job continue, pause, or stop on lease loss?
- Who can cancel/resume when both a local lead and an external orchestrator
  are present?
- How is an old remote generation detected and rejected after reconnection?
- Which prompts, logs, and artifacts may leave the machine?

Starting rule: no remote job may have two concurrent execution owners. Local
jobs are unaffected when the connector is off. Set a remote job's offline
policy before acceptance; it is not automatically identical to the local
client-crash policy.

The PoC has only a fake external command-source test against the application
API. It includes no network connection, credential, or real external
orchestrator installation.

## 11. Performance and token economics

Hypotheses, not verified savings:

- One daemon instead of a full server per agent may reduce orchestration memory.
- Events and direct backend status may reduce status polling and file scanning.
- Smaller tool descriptions and brief results may reduce context cost.
- AOT may reduce startup and memory for the .NET portion, but must be compared
  with both the JIT variant and a documented comparison workflow.

Measure separately:

1. Daemon and all bridges, including a separate startup and steady-state memory
   budget for each per-host or child-side bridge.
2. Whole process tree, including Claude/Codex and shell children.
3. Idle CPU and wakeups with zero, one, and two agents.
4. Local acceptance/event latency, separate from model response time.
5. Tool calls, model wakes, and reported tokens per completed task.
6. Disk I/O, DB/log growth, and recovery time.

Compare equivalent versions, models, instructions, and cache conditions. On
Linux use PSS where possible; report Windows metrics separately rather than
mixing them with PSS. Report median, spread, and raw data. Do not hide agent
process costs behind a small daemon RSS.

Preliminary budgets and exact experiments are in the [PoC](poc.md).

## 12. Risk register

| Risk | Consequence | Mitigation / decision point |
| --- | --- | --- |
| Backend protocol changes | Wrong status or delivery | Version pin, capability probe, contract fixtures, and live smoke. |
| Wake works only in some hosts | Lead agent must read manually | Thin host bridge, capability matrix, separate hands-free gate. |
| Dispatch and DB are not atomic | Duplicate work after a crash | Commit queued acceptance separately; commit generation/correlation attempt-start before every external effect; never blind retry. |
| Orphan process keeps writing | Two attempts change the same files | Verified process ownership and physical stop/idle confirmation before resume; DB reconciliation alone is insufficient. |
| AOT dependency problem | Large side process or blocked publication | Vertical AOT spike before broad implementation. |
| SQL becomes file chaos in another syntax | Too much state and duplication | Small invariants, transactions, and clear source of truth. |
| Output or queue grows without bound | Full disk or memory exhaustion | Bounded buffers, retention, quotas, and fault injection. |
| Agent waits on a hidden approval | Job appears to run but stalls | Surface explicit blocked status or mark adapter unsupported; timeout, never bypass. |
| Human input overlaps machine work | A queued job is misattributed or conflicts | Record foreign activity, human-wins pause, explicit idle reconciliation. |
| Interactive result occurs during outage | Completion is lost or guessed | Replay verified backend records or bounded private helper spool; otherwise unsupported/reconciliation. |
| External orchestrator concepts leak in | Local product requires central app | Port/adapter boundary, fake connector test before integration. |
| Memory gains are overstated | Poor decision evidence | Measure daemon, bridges, and whole agent tree separately. |
| Compatibility scope is unbounded | Compatibility work overwhelms the local runtime | Managed modes per setup choice; legacy/attached separate. |
| Terminal TUI lacks safe control/result path | Visible tab but broken orchestration | Backend × mode × provider matrix and real M0 spikes. |
| Background service cannot reach GUI session | Interactive launch fails | Session launcher if needed; no silent headless fallback. |
| Name/license conflict | Distribution needs changes | Check before public release and before code reuse. |

## 13. Verification and development order

See the [roadmap](roadmap.md) for milestones and the [PoC](poc.md) for test cases.

Basic order:

1. Establish contracts and run technical risk spikes.
2. Build SQLite and a fake backend vertically through the daemon and thin MCP
   bridge.
3. Establish red/green tests for every crash window and lost response.
4. Add one real backend at a time and run actual CLI smoke tests.
5. Measure and decide go/no-go before service installation and more integrations.

Major changes require independent plan review; all implementation changes require
the opposite-model-family code review defined in `AGENTS.md`.
Run the entire repository's format, build, test, and AOT gates; report platform
limits openly. A confirmed PoC is not the same as a production-ready release.

## 14. Sources

- [Codex app-server](https://developers.openai.com/codex/app-server/)
- [Claude programmatic execution](https://code.claude.com/docs/en/headless)
- [Claude CLI reference](https://code.claude.com/docs/en/cli-reference)
- [Claude cross-session messaging](https://code.claude.com/docs/en/cross-session-messaging)
- [Official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [.NET Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)

Web documentation changes; M0 must record the backend versions and relevant
contracts used. Public source availability is not evidence that a proposed
integration or platform capability works.
