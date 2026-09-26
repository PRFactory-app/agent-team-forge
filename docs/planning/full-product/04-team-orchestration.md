# P04 — Local team orchestration

## 1. Objective, scope, and evidence

Deliver complete local team control on the durable P02 daemon and P03 managed
Claude/Codex/Pi adapters: multiple teams, bounded nested delegation, authorization,
queues, communication, approvals, resource policies, host wake, workspaces, and
inspectable results. Implementation follows P01 → P02 → P03 → P04 → P05;
P06 adds Pi lead-host/attach/integration proof, supported attachment/join, and
the opt-in connector; managed Pi execution is mandatory in P03.

This is a draft plan, not implementation approval or runtime evidence. The
[full-product brief](README.md) defines the expanded baseline and ordered phases;
[cross-phase contracts](contracts.md) own synthesis decisions, and
[product scope](../../product-scope.md#2-product-feature-inventory) owns canonical
F01–F27. P01 R groups are local: use [the crosswalk](contracts.md#1-scope-ids-and-phase-ownership).
P04 primarily delivers F10–F15 and extends F03/F08; phase gates refine, not replace,
canonical requirements. Earlier PoC restrictions on nesting
are expanded here deliberately, not retroactively declared implemented. Preserve
[architecture](../../architecture.md), [delivery contracts](../../plan.md),
[PoC cases](../../poc.md), and [terminal rules](../../terminal-modes.md).

Planning requested GPT-6 Astra at medium effort with separate phase authors.
This document does not certify model selection or independent review. No live
agents, model calls, installation, Git operations, or runtime tests are authorized
by this planning task. Existing spike findings remain unresolved until independently
checked; .NET 10 observations do not prove the selected .NET 11 target or AOT.

**Full P04 exit:** all local orchestration contracts below pass, including real
managed Claude/Codex/Pi regression and P01-selected supported-host wake evidence on the
selected P01/P03 combinations. Missing required platforms or host evidence block
full exit. A Linux/Codex or fake-only preview may demonstrate a subset, labelled
with missing gates; it neither completes P03 nor unlocks the waterfall baseline.
P03 managed Pi evidence, including native Windows lifecycle, is a prerequisite;
additional Pi lead-host wake proof belongs to P06. Linux tests or cross-builds
cannot establish Windows support. Separately reviewed Linux experiments follow
[contracts section 6](contracts.md#6-waterfall-path-versus-early-experimental-preview)
and do not complete P01/P02/P03 or waive full waterfall gates. P05 installer and
service packaging are not prerequisites: use P03's explicit persisted mode and
approved temporary production-style launch contexts.

Excluded: distributed scheduling, automatic lead replacement, model planning
loops, rich analytics dashboard, arbitrary existing-session adoption, OS sandbox claims,
automatic merges, mandatory connector, and unrestricted nesting. Existing agent
TUIs remain real interactive sessions; no log tails or silent headless fallback.

## 2. Entry criteria and predecessor inputs

- P01 decisions name OS/architecture, backend versions, terminal providers,
  supported host candidates, security bootstrap, and .NET 11/AOT test matrix.
  Unselected providers, unsafe approval control, or unknown ownership mechanisms
  block affected implementation paths, not merely documentation.
- P02 exit supplies private authenticated IPC/MCP, daemon-only SQLite ownership,
  protocol/schema negotiation, idempotency, atomic acceptance plus unattempted
  intent, pre-effect attempt generation, final-result/event transaction, and
  deterministic fake-process crash harness. C01–C32 coverage is an input to
  inspect, not an assumed pass.
- P03 exit supplies managed Claude, Codex and Pi in both selected modes, including
  real native Windows Pi launch, same-session follow-up/results, interruption,
  separate stop and reconnect in visible selected terminal tabs or explicitly
  chosen headless mode; verified
  conversation/terminal/process bindings, interruption distinct from stop,
  approval and question capability declarations, authoritative result evidence,
  foreign-activity observations, and outage reconciliation. Pin exact adapter
  versions and fixtures. Any safety finding that affects these contracts blocks
  entry until fixed and independently checked.
- Reconcile this proposed schema/API delta with actual P02/P03 versions before
  implementation. Do not edit predecessor plans implicitly or invent version
  numbers that conflict with their migration history.
- Independent review of this major persistence/security plan is required before
  implementation. Model and platform availability are external dependencies.

## 3. Binding local contracts

### 3.1 Identity, immutable authority, and bounded delegation

Stable opaque team, principal, agent, conversation, job, and run IDs are distinct
from display names, PIDs, terminal IDs, and provider session IDs. Each agent has
one owning team and one active conversation binding; each conversation admits
at most one unresolved active turn. A new conversation requires verified idle
and an explicit transition; old history and ownership are never rewritten.

A capability grant has immutable issuer, subject, team, permitted operations,
resource scope, parent grant, expiry, and maximum delegation depth. Revocation
is a separate durable record. No API changes a grant into broader authority:
issue a new attenuated grant through an authorized issuer. Transport identity
binds the principal; request-body role names never authenticate anyone.

| Role | Allowed scope | Never implicit |
| --- | --- | --- |
| Local operator | Explicit team creation, delegation, revoke, policy and recovery actions under local authentication | Automatic permission to export secrets or terminate interactive survivors |
| Team lead | Submit, inspect, message, approve, reconcile and delegate within granted team/subtree and accepted policy | Other teams, daemon administration, broader backend permissions |
| Delegated lead | Same named operations for its bounded subtree | Parent/sibling administration or budget expansion |
| Worker | Its jobs/results, addressed messages, explicitly allowed child delegation | Lead reconnect, grant issuance beyond delegated scope, arbitrary workspace reads |
| Observer/helper | Explicit read-only view or session-scoped lifecycle reporting | Job submission, approval or team administration |

A parent-child edge is immutable. Team trees and agent trees are acyclic, each
node has at most one parent; no live reparenting or cross-team agent migration.
A parent team relationship provides provenance and budget ancestry, not implicit
access. Child-team creation atomically records narrowed grants and reservations;
siblings have no access. Cross-team messages require explicit recipient and sender
grants; jobs require explicit target-team control. IDs and error details must not
leak whether inaccessible resources exist.

**Proposed bounded defaults, frozen before implementation:** root depth 0,
maximum three delegation edges along the combined team/agent ancestry; at most
32 descendant agents/teams combined per root; at most 100 accepted jobs and
20 wake attempts per root delegation epoch. Duplicate idempotent retries do not
consume new slots. Closing a child or reconnecting does not refund creation/job
counts or reset wake counts. An epoch is a durable operator-authorized work
scope, not a bridge lifetime. Exhaustion blocks new actions; only an operator
may explicitly renew with a fresh finite allocation. Policy may tighten these
bounds, never remove them. This bounds sequential delegation loops as well as
nesting. Message size/rate and queue quotas apply independently. Wake limits
may leave unread results for explicitly reported degraded manual catch-up; they
never discard them or turn recovery into a required native-wake gate pass.

Grant checks occur at acceptance and immediately before new external effects.
Revocation blocks queued dispatch and new delegation; it does not erase accepted
jobs or pretend to stop ongoing physical effects. Active revoked work enters an
observable policy stop/interrupt or reconciliation path. Revocation does not grant
new authority to the revoked client. Same-user capabilities are not an OS sandbox.

### 3.2 Scheduling, accepted policy, and lead loss

Use a single-machine deterministic scheduler: round-robin among eligible teams,
FIFO within each conversation, with global, root-subtree, team, backend and
conversation limits. Default global active-turn limit is two; configure explicit
finite limits rather than inheriting PoC scale as a product maximum. Count
approval-blocked turns and uncertain survivors against safety reservations.
A blocked conversation need not block other eligible conversations in its team.
No priority/preemption engine, remote worker placement, or autonomous planning.

Acceptance snapshots authority provenance, launch mode, workspace, backend/model
allowlist, deadlines, offline behavior, quotas, escalation permission, and policy
version. Later tightening/revocation may pause work; broadening never silently
changes an accepted job. Atomically reserve ancestor budgets, enqueue and record
idempotency before acknowledgment; recheck eligibility/reservations in the
attempt-start transaction before external effects. Commit no external call inside
SQLite. Reconcile uncertain effects instead of releasing their safety ownership.

Lead/bridge death leaves accepted eligible jobs running and queued jobs dispatching
within their accepted policy. Approvals/questions wait or expire. A previously
explicitly delegated child may submit within its remaining grant/budget; the
daemon never invents follow-ups, starts a replacement lead, or turns results into
instructions. Lead liveness is not inferred from a socket timeout to cancel work.
New lead access requires operator delegation and consumer-generation fencing.

### 3.3 Messages, durable read/ack, and wake

Messages are informational records, never executable jobs. A notification of an
accepted job references its ID and must not enqueue that instruction again.
Promoting message content to work is an explicit authorized job submission with
its own idempotency key. Duplicate messages use scoped sender keys. Read/ack
means client receipt, not model execution or backend completion.

Extend P02 with a recipient-delivery projection per durable team-role consumer
principal in the same schema/migration stream. P02's global monotonic event
sequence remains lifecycle/audit identity; no parallel authoritative event store.
Each recipient delivery references an existing event/message ID. An authorized
event may create multiple recipient deliveries in the same transaction; each
recipient stream has its own contiguous monotonic sequence. Global event sequence
and recipient sequence are distinct fields, neither a run nor consumer generation.
Backend delivery attempts are also distinct from recipient deliveries. The stream contains only deliveries authorized for that
principal. Reconnect atomically increments its consumer generation; old reads,
acks and wake subscriptions are fenced. Run, consumer, grant and workspace
generations are distinct and never interchangeable.

Canonical reads have no destructive filters. A bounded read returns the oldest
unacknowledged contiguous batch, preceding cursor, generation, stable IDs and an
opaque durable batch receipt bound to principal, stream, consumer generation,
preceding cursor and exact issued batch. One outstanding canonical batch per generation;
retries return that batch. Ack supplies receipt plus generation, not an arbitrary
future sequence. Compare-and-swap advances only from the receipt's preceding
cursor to its exact end. Retried committed ack returns the same outcome; stale
or forged receipts cannot skip anything. Connection loss before ack rereads IDs;
reconnect may reissue a receipt for the same unacknowledged records. Clients
must deduplicate by stable ID.

Filtered search/inspection is read-only and cannot ack the canonical stream.
A subscription's recipient scope is immutable; newly granted access creates an
explicit subscription/backfill operation, not an invisible cursor jump. Revoked
principals cannot read retained payloads. If operator reauthorization narrows an
existing stream, removed entries become explicit redacted revocation markers in
the contiguous batch; never silently skip them or mark them received beforehand.
Unread retention gaps likewise require explicit markers and operator recovery,
not synthetic acknowledgment. Receipt validation and revocation races are atomic.

**Native session wake is the default standard**, following
[contracts section 4](contracts.md#4-host-wake-and-backend-support-are-different),
the public [PR #70 design](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70)
and [research](../../research/native-wake-pr70.md). Reference snapshot is public,
merged to main at `471a17514d041e09b69cb24b910e418da28d2027`; inspected head
`6da4e041f95e62e4c5f3a9ddf8f043c0fe7d769e`. Design reference
is not source-copy/license approval and upstream tests are not our evidence.
Unlike upstream's opt-in rollout, every promised Claude/Codex host/platform cell
must pass native wake here. One host suffices only for an early checkpoint.
Manual read/catch-up is explicit degraded recovery, never a silent waiver.
Watchers, terminal keystrokes and tight model polling are not the normal path.

Commit messages/events before attempting wake. Report committed send and wake
status separately: failed wake cannot fail or roll back committed send. Wake is
a notice-only host capability, not event delivery or permission to submit jobs;
no message bodies or credentials enter notices. Successful transport write is
neither read/ack nor evidence of a model turn. Recipients fetch through
authenticated canonical read/ack.

Bind registration to authorized principal, native host/session and consumer
generation. Validate nearest-host ownership; scrub inherited wake handles and
environment on spawn/resume to prevent child routing into a parent's session.
Use one per-reader OS lock plus generation fencing for a single active notifier.
Coalesce pending references with a proposed minimum five-second interval and one
outstanding attempt; freeze measured timing in P01. Persist pending state, attempt
outcome and high-water reference. Apply bounded backoff/retry within finite wake
allocation, revalidating target and generation before each attempt. Unknown notice
delivery may duplicate only through this safe bounded path, never execute a job
or advance a cursor. Reconnect uses fresh registration and unread catch-up;
stale targets are rejected. Exhaustion or disconnection exposes degraded state
and retained unread data, not a false wake guarantee.

| Host | P04 obligation | Evidence boundary |
| --- | --- | --- |
| Claude lead | Native owning-session inbox transport via own child; nearest-host check, scrub on spawn/resume, per-reader OS lock and authenticated notice-only routing | Linux reference is not platform parity. Windows Claude is unsupported upstream pending cancellable named pipes; macOS reader is unproven. Each promised cell needs our native idle-wake/security evidence; MCP notification alone is insufficient |
| Codex lead | Authenticated thread/home registration bound to generation; bounded `codex queue`, scrubbed environment, trusted working directory and read-only thread lookup | Upstream Windows W1–W3 passed, including native Codex member wake. Codex-lead self-wake is not established by that member result; independently test each promised role and our Windows integration. Cross-builds cannot pass live gates |
| Pi lead | Reuse versioned native-wake contract and normalized fake fixtures; consume P01 Windows/Pi feasibility | Product lead-host/attachment integration and live proof belong to P06. Managed Pi execution already requires P03 evidence, including native Windows lifecycle; neither execution nor fakes prove wake |

### 3.4 Approvals, deadlines, and bounded storage

Persist approvals and user questions separately, correlated to job/run generation,
backend request ID, decision schema, authorized decider and expiry. Questions do
not convey permission; approval answers apply only to their named request.
One idempotent decision wins concurrent responses; stale, expired, foreign-team
or old-run decisions cannot release a current turn. Persist decision-delivery
intent before backend I/O. Unknown decision delivery requires reconciliation,
not blind approval replay. Unsupported observation/response is explicit; native
human decisions may be accepted only through correlated authoritative evidence.
No connected decider means blocked until an authorized response or timeout.

Store absolute UTC deadlines at acceptance; derive running timers from monotonic
time and re-evaluate persisted deadlines at restart. Clock anomalies cause
conservative pause/reconciliation, never extra unbounded runtime. Distinguish
queue expiry, turn deadline, approval expiry, and grant expiry. Queued expiry
has no external effect. Interactive deadlines request turn interruption, keeping
the TUI; termination requires separately accepted escalation and verified owned
process identity. An unconfirmed interrupt is not a guaranteed hard cap.

Ancestor reservations cover job/agent counts, active-turn slots, queue bytes,
output/artifact/log bytes, and bounded runtime policy. Repeated restarts cannot
reset budgets. Token/cost reports carry source and freshness; caps are advisory
unless P03 supplies sufficiently precise enforceable backend limits. Unknown
usage is not zero. Reject unavailable hard spending guarantees at acceptance.

Proposed retention defaults: completed payloads and acked events 30 days,
rotated logs 7 days with a 256 MiB per-team cap, artifacts 1 GiB per-team cap,
and unacknowledged events pinned subject to an explicit finite storage quota.
Require finite daemon-wide quotas before dispatch is enabled. Unread quota
pressure pauses intake; reserve space for lifecycle records and expose disk
pressure. If emergency operator purge is required, record a durable gap marker
and require recovery; never claim lossless catch-up. Active/uncertain runs,
unresolved approvals and ownership records are pinned. Retain compact
idempotency tombstones for retired keys; issued key namespaces can be explicitly
retired and thereafter rejected, never silently reused. Physical disk failure
still follows C19: no false acceptance or invented terminal outcome.

### 3.5 Workspace ownership, foreign activity, and references

Default to a distinct daemon-managed worktree per writing agent. An explicitly
shared writable workspace permits only one verified writer; read-only sharing
requires a declared backend-enforced capability, otherwise it is not claimed.
Worktree creation is a journaled external effect with identity and intent; a
crash reconciles the directory rather than making duplicate branches/worktrees.
Repository hooks/config discovery require accepted trust policy. Do not
implicitly merge, reset, delete dirty work, or clean a user-owned directory.

Persist workspace ID, canonical allowed root, ownership class, agent/team,
generation, creation identity and cleanup eligibility. Prevent path traversal,
symlink/reparse escapes and deletion outside owned roots; resolve and validate
at use, not only when storing a path. Worktree isolation does not sandbox tools
running with the same user's permissions.

Foreign/human activity sets the conversation and associated writer reservation
unsafe for automatic follow-up. Record activity separately from daemon jobs.
Do not attribute it to the next job or infer completion from text/silence.
Reconciliation requires authorized action bound to the latest activity revision,
verified backend idle, session/process identity, and workspace writer evidence.
New foreign activity invalidates that decision before dispatch. If the adapter
cannot observe or exclude the race, retain the pause: DB fencing cannot prevent
physical typing. Never auto-kill a live interactive TUI. Abandoning a job does
not release a workspace while its survivor can still write.

Logs, artifacts and large results use opaque references with owning team/run,
size, media type, digest where available, retention state and access policy.
Reads use authenticated bounded/ranged retrieval; never expose arbitrary paths,
full transcripts or capability secrets by default. Final result metadata and
completion event commit atomically. File publication uses staging plus validated
finalization; reconcile orphan files or missing content after crashes without
claiming a complete artifact. Streaming fragments may be lost before checkpoint;
a durable result reference must report unavailable/corrupt data explicitly.

## 4. Ordered vertical work packages

Every package includes Host entry points, Business rules and DAL atomic
operations as needed, not separate layer-first delivery. Names below describe
proposed operations, not existing commands. Critical behavior follows red →
green → refactor with real temporary SQLite and few deterministic fake-process
barriers. Do not test prose, private class shape or coverage quotas.

| Package | Observable result and deliverables | Host / Business / DAL touchpoints | Dependency; meaningful tests | Size / confidence |
| --- | --- | --- | --- | --- |
| P04-W01 | Create/inspect two isolated teams; immutable scoped grants, revoke and reconnect; versioned authorization matrix | Host binds identity and exposes team/grant operations; Business attenuates authority; DAL team/principal/grant/revocation constraints and migration | P02/P03 exits and plan review; cross-team read/mutation/role escalation, revoke-versus-dispatch race, lost response | M / medium |
| P04-W02 | Delegate child teams/agents and reserve finite ancestor budgets; inspect provenance | Host delegation request; Business cycle/depth/subtree rules; DAL atomic edges, counters and reservations | W01; depth/cycle/sibling denial, concurrent last-slot allocation, restart/retry cannot reset counters | M / medium |
| P04-W03 | Durable fair local queues and one active conversation; accepted offline policy survives lead loss | Host submit/policy/queue view; Business eligibility and scheduling; DAL policy snapshots, intent/reservation CAS | W02; FIFO, eligible-team fairness, two targets sharing conversation, lead death, C07/C08 barriers and stale dispatch | L / medium |
| P04-W04 | Send information without execution; durable canonical read/ack and filtered inspect | Host message/read/ack/reconnect endpoints; Business recipient grants and receipt rules; DAL streams, receipts, generation CAS | W03; lost read/ack, stale bridge, forged future ack, filter/no-unseen-skip, concurrent revocation, atomic final event fan-out | L / medium |
| P04-W05 | Block and resolve approvals/questions, apply deadlines and resource limits honestly | Host decision and budget views; Business authorization/expiry/escalation; DAL decisions, delivery intents, usage and retention metadata | W04; competing/stale answers, no lead, uncertain answer delivery, advisory spending, deadline restart, quota/disk pressure | L / medium |
| P04-W06 | Run isolated workspaces; safely pause and reconcile human activity; retrieve bounded artifacts | Host workspace/reconcile/reference reads; Business trust/path/writer rules; DAL ownership intents, activity revision, artifact finalization | W05; foreign activity race, live orphan holds writer, symlink escape, dirty cleanup refusal, file/DB publication crash | L / medium-low |
| P04-W07 | Standard native Claude/Codex integration for every promised host/platform cell, authenticated notice-only wake and durable catch-up; Pi lead/attach seam awaits P06, not its P01 feasibility or P03 execution | Host own-child inbox or registered thread/queue bridge; Business ownership validation, scrub, coalescing/backoff and bounded retries; DAL registration/pending wake/attempt generations | W06; duplicate reader locks, wrong host/thread/home, stale registration, inherited environment, untrusted cwd, burst, failed/unknown wake after committed send, reconnect and budget exhaustion; separately authorized native live host/platform gates | L / low until host evidence |
| P04-W08 | Inspect and operate complete local team through CLI/MCP without dashboard; acceptance and handoff evidence | Host compact tree/status/queue/inbox/approval/diagnostic views and paging; Business authorized projections; DAL indexed bounded queries | W07; end-to-end failure demos, no cross-team leakage in summaries/references, stale pagination, published binary/AOT regression | M / medium |

Tree/status output includes parent IDs, policy/budget remaining, queue reason,
actual launch mode/provider, current conversation, blocked approvals, foreign
activity, reconciliation reason, unread count and result/log references. CLI and
MCP invoke the same Business operations; neither opens SQLite. Detailed content
requires explicit retrieval. Mutations keep scoped idempotency and structured
errors. Snapshot pagination is versioned; a changed snapshot yields explicit
restart, not an assertion of complete inventory from mixed revisions.

## 5. Schema/API deltas and downstream contracts

P02 owns base migrations and wire negotiation; P04 extends them rather than
forking a second scheduler, database or event broker. Logical additions may be
columns or feature-local tables after schema review:

| Delta from P02/P03 | Atomic invariant / API impact |
| --- | --- |
| Team ancestry, principal/grant/revocation and delegation epoch | Creation plus attenuated grants and reservations commits together; no mutable owner IDs; authenticated team/delegation/revoke operations |
| Conversation binding, accepted policy and ancestor budgets | At most one active turn; reserve and mark attempt together; expose reasoned queue/policy/budget views |
| Recipient delivery projection, batch receipts, consumer generations | References P02 event/message IDs; recipient sequence differs from global event sequence. Complete public canonical read/ack with receipt CAS; inspection cannot ack; reconnect fences old consumer; no second event store |
| Approval/question decisions and decision delivery intent | First valid decision wins; generation/expiry enforced; distinguish accepted decision from backend receipt |
| Host binding, wake capabilities and pending attempts | Wake is separate from event ack; hooks scoped to host/principal generation; unsupported is machine-readable |
| Workspace ownership, activity revision and file references | Physical safety survives logical cancellation; authorized reconciliation and opaque content reads |
| Retention, usage and idempotency tombstones | Cleanup cannot silently skip unread work or recreate expired operations |

Assign the next P02-compatible schema migration and negotiated protocol feature
version before coding. P04 owns complete public read/ack and migration of any
experimental P02 internal cursor. A change to exposed experimental semantics
requires a wire version boundary, not interpreting numeric cursor acks as receipts.
Older clients fail capability negotiation for unsupported mutations. Existing
active attempts must be drained or reconciled before migration; never fabricate
ownership/grants from display names. Migrate existing single-team records only
from verified P02 principals; ambiguous rows block migration for operator mapping.
Unknown newer schema remains refused without mutation (C30).

**P05 handoff:** compact operator CLI/MCP projections, policy configuration
schema, explicit errors and remediation actions, permission/redaction matrix,
retention controls, and doctor capability query. Provide startup/desktop-required
states without requiring installer implementation here. P05 verifies real
installed service contexts (T13/T14), setup/config/update/uninstall and migration
UX. It must preserve existing agents' actual launch bindings.

**P06 handoff:** versioned backend and host-hook manifests separately declare
execution, authoritative completion, approvals/questions, interruption, stop,
foreign-activity observation, resume, replay and wake capabilities. Include
P03 managed Pi conformance evidence and normalized lead-host fixtures; fake
lead-host tests cannot establish live wake or attachment support. Managed team
reconnect is not attached-session adoption. Attachment/join must supply verified
external session ownership, narrowed tickets and closure semantics; absent proof
cannot acquire writable ownership. The connector maps external commands to the
same scoped API/idempotency, with explicit owner generation/offline policy and
export grants. P06 defines remote leases and opt-in transport; P04 implements no
remote scheduler or network dependency. Local team provenance never becomes a
remote authority merely by adding an external identifier.

**P07/P08 handoff:** migration fixtures, adversarial authorization/ack scenarios,
resource measurements and real platform/host matrix for cumulative qualification
and public support claims. Verification artifacts must include sanitized schema
before/after snapshots, protocol fixtures, operation/job/run IDs, versions,
commands, outcomes and blocked/unrun cases. These artifacts are planned, not
claimed present.

## 6. Gate table

All gates below are **planned / not run by this author**. Proposed harnesses and
commands must be implemented with reproducible invocation records; no existing
`atf` command or test script is asserted. Reuse cases rather than duplicate tests.
Mappings identify relevant semantics, not blanket PoC completion.

| Gate | Reproducible verification and exact success criterion | Mapping | Evidence and blockers |
| --- | --- | --- | --- |
| P04-G01 | Migrate realistic P02/P03 state, retry concurrent mutations and inject failed commits: one scoped operation, no foreign read/write, no broadened grant, incompatible schemas untouched | C01–C03, C19, C24–C25, C29–C30 | Real SQLite/IPC plus fake backend; blocked on agreed predecessor schema and review |
| P04-G02 | Barrier-driven delegation/scheduling tests: depth/count exhaustion rejects before effect; no cycle; one active turn/conversation; finite eligible teams get round-robin service within one scheduling round when slots become free | C03, C05, C07–C08, C20 | Fake process and persisted counters; deterministic fairness under fixed eligibility, not model timing promise |
| P04-G03 | Drop read/ack responses, reconnect old/new bridges, forge receipt/end and filter reads: exact contiguous unacked IDs reappear, valid ack advances once, stale generation never advances and no unseen item is consumed | C11–C12, C32 | Real SQLite and concurrent IPC clients; includes narrowed grants and retention gaps |
| P04-G04 | Competing approval/question responses, lead loss, expiry/restart and unknown backend receipt: one authorized decision, no stale release, no bypass; budgets never reset and unenforceable spending cap is rejected | C20–C23, L05, T18 | Fake proof plus real Claude/Codex/Pi capability-specific approval tests; unavailable safe response remains explicit unsupported, no fabricated parity |
| P04-G05 | Inject foreign activity between idle check and dispatch; crash with live writer and try cleanup/new run: no automatic follow-up, no second writer, no foreign deletion; authorized revision-bound reconciliation only | C13–C16, C21, C31; T05, T08–T10, T15–T17 | Fake race/path tests plus real interactive tests per selected provider; no PID-only or model-report proof |
| P04-G06 | Native idle wake, burst/backoff/exhaustion, duplicate OS-lock owner, wrong host/thread/home, stale generation, inherited environment and untrusted cwd tests; disconnect/reconnect and failed/unknown wake after commit. Bounded notice-only payloads, no bodies/secrets; send stays committed, wake never acks, unread data remains retrievable; actual idle model turn observed separately | L07 for Claude only; independent Codex host/platform scenarios; C11–C12, C32 catch-up subset | Every promised Claude/Codex native host/platform cell has our live version/path evidence, including native Windows; missing transports/access block. One-host checkpoint, upstream tests, cross-builds or manual recovery cannot pass full gate. Pi lead/attach integration gate is P06; P01 feasibility and P03 managed execution remain prerequisites |
| P04-G07 | Kill lead/bridge during queued and active multi-team work, then kill daemon at attempt boundary: eligible work survives client death; uncertainty blocks replay; new authorized lead retrieves results and cannot read another team | C04–C10, C13, C15, C24, C31–C32; L01–L03, L06; T06–T07, T10, T19 | Fake barriers then real Claude/Codex/Pi both modes, including native Windows Pi lifecycle; L03 retains three repetitions per Linux mode; missing machines block platform evidence |
| P04-G08 | Slow consumer/output burst and quota pressure, expire/purge data, corrupt or remove artifact: bounded memory/files, explicit backpressure/gap/unavailable result, no false completion or key reuse | C18–C20, C29; T16 | Real files/SQLite and fake bounded load; record chosen finite quotas, actual peaks and cleanup outcomes |
| P04-G09 | Run published .NET 11/AOT CLI/MCP end-to-end on selected Linux/Windows/macOS combinations, inspect neighboring tabs and preserved modes; no regression in actual TUIs/control paths | L01–L06, L08–L12 relevant subsets; T01–T12, T15–T19 | P03 evidence plus changed-path reruns and justified unchanged-case references; T04/T07/T10 in temporary production-style context; installed T13/T14 owned by P05 |
| P04-G10 | Independent plan review, opposite-family implementation review/re-review; full applicable format/lint, Release build, tests and published smoke recorded; handoff fixtures replayable | Architecture and contributor review policy | No blocking findings; unavailable reviewer blocks gate, not same-family substitution; no live tests in this planning session |

L04 interrupt/stop and L09 headless platform support remain distinct from wake.
Codex-as-worker success does not prove Codex-as-lead wake. P03 managed Pi evidence
does not prove Pi lead-host wake; fake contracts establish neither live capability. Any supported-host matrix restriction requires an
explicit parent scope decision; do not silently replace a required host with
manual polling to declare full P04 complete.

## 7. Operator acceptance demonstrations

Use disposable repositories, approved tiny prompts/budgets and public sanitized
evidence. The following are future demonstrations, not actions performed now.

1. Start using P03's explicit interactive profile and separate daemon. Create
   two teams, delegate a child lead and nested worker within the finite epoch.
   Show real Claude/Codex/Pi TUIs and compact CLI/MCP tree, policy and queue state.
   Submit two follow-ups to one conversation; show serialization and unrelated
   eligible team progress. Repeat essential control in explicit headless mode.
2. Attempt sibling administration, foreign-team result read and one excessive
   nesting/job allocation. Each fails without leaking content or launching work.
   Send an informational message; show that no executable job appears.
3. Read an inbox batch, disconnect without ack, reconnect with a new generation.
   Show the same stable IDs; stale bridge ack and arbitrary future cursor fail.
   Filtered inspection cannot consume omitted records. Ack succeeds once.
4. Kill lead and bridge while accepted work runs and another turn awaits approval.
   Completed results persist; approval waits/expires. No replacement lead starts.
   Authorized reconnect sees both results and blocked question, then answers only
   its current request. Show stale answer rejected after run generation change.
5. Give human input in a worker TUI while a follow-up is queued. Observe
   `foreign_busy`, retained writer reservation and blocked automation. Reconcile
   only after verified idle and current activity revision. Interrupt keeps the
   tab; separately authorized stop leaves neighboring tabs untouched.
6. Crash daemon at a started attempt; recovery exposes uncertainty and never
   blindly resends. An uncertain live writer blocks a new writer and cleanup.
   Retrieve log/result references; unauthorized/path-escape requests fail.
7. For every promised Claude/Codex native host/platform cell, publish a completion
   burst while idle, record notice-only transport writes versus actual model wakes,
   then reconnect after a lost notifier. Reject wrong/stale registrations and
   duplicate notifier ownership. Fail wake after a committed send: send remains
   successful and unread data remains retrievable. Demonstrate bounded backoff and
   explicit degraded manual recovery, never a watcher, keystroke or polling normal
   path. Missing native evidence blocks full gate; one host is checkpoint only.
8. Exhaust queue/log/artifact/wake allowances. Show bounded refusal or pause,
   manual catch-up still possible, explicit expiry outcomes and retained
   idempotency identity. Record each platform/mode/host tested separately.

## 8. Decisions, dependencies, stop/go, and rollback

| Decision / risk | Disposition and stop rule |
| --- | --- |
| Nesting and loop budgets | Adopt section 3.1 finite defaults pending independent review; change via explicit policy version, never reconnect reset |
| Schema/read-ack compatibility | Agree migration and wire boundary with P02 owner before W01/W04; incompatible cursor semantics block release |
| Host wake feasibility | Frozen P01 required matrix plus W07 native evidence for every promised Claude/Codex cell. Windows Claude cancellable named pipes and macOS reader remain transport gaps. Reference Windows Codex W1–W3 passed; our integration still requires tests. Missing transport/access blocks gate or requires explicit owner scope decision; manual recovery and one-host checkpoint cannot pass full release |
| Physical ownership race | P03 must provide trustworthy activity/idle evidence; uncertain adapter remains paused, not papered over with a DB lock |
| Approvals/usage limitations | Explicit unsupported/advisory states; unsafe permission bypass or false hard cap stops affected feature |
| Platform/SDK availability | .NET 11 target and actual AOT runs required; absent SDK/packages/GUI access block evidence, no installation authorized here |
| Storage and unread retention | Backpressure before hard quota; operator purge is explicit data loss with gap markers, never silent cursor advancement |
| Review and model availability | Independent major-plan review before implementation; required opposite-family code review before exit; unavailable reviewer is a blocker |

Before migrations, quiesce dispatch and take a verified SQLite-consistent backup
including required file references and ownership manifests; P02 must provide a
safe mechanism or implementation blocks. Never copy only a live WAL database
file. Rollback stops intake/wake/delegation, preserves journals and leaves live
interactive TUIs untouched pending reconciliation. Disable new features through
capability negotiation; do not route unsupported calls to old semantics.

A binary downgrade may open state only if explicitly schema-compatible. Otherwise
restore a verified snapshot into a separate state directory after proving old
processes cannot write and reconciling post-snapshot external effects. Restoration
is not permission to replay accepted jobs. Keep the newer state/journals for
recovery; never delete dirty worktrees or lose unseen events to make rollback
appear successful. Unresolved ownership means stop dispatch, not auto-cleanup.

## 9. Size, sequencing, and exit handoff

Overall **L / medium-low confidence**: durability, authority and backend wake
interact; host evidence and physical workspace ownership dominate uncertainty.
W01–W08 are ordered vertical increments. Parallel fixture preparation is possible,
but implementation cannot bypass predecessor exits. External waits include
independent review, supported backend/host APIs, authorized model budgets,
.NET 11/package availability and real Windows/macOS GUI environments. No calendar
or within-hours completion promise. The separately scoped early Linux demo does
not reduce full-phase work.

Definition of done and handoff checklist:

- [ ] P02/P03 entry evidence inspected; independent P04 plan review resolved.
- [ ] W01–W08 complete through exactly Host → Business → DAL; daemon alone owns DB.
- [ ] G01–G10 results recorded with real/fake, platform, mode and host distinctions;
      blocked/unrun cases prevent full exit rather than disappearing from scope.
- [ ] Team authority, nesting, accepted policy, physical ownership and canonical
      read/ack contracts documented in public API and migration fixtures.
- [ ] CLI/MCP acceptance demos cover cross-team denial, stale ack/no-unseen-skip,
      lead crash, foreign activity, budgets, approvals and artifact references.
- [ ] Every promised Claude/Codex native host/platform gate passed independently,
      including native Windows; no manual-only waiver or upstream-test substitution.
      Notice-only authentication, scrub/ownership/lock, bounded retry and committed
      send versus wake failure tested. Managed Pi/P03 evidence includes native
      Windows lifecycle; only additional Pi lead-host/attach integration remains
      P06, with P01 feasibility already required. No P05 installer dependency.
- [ ] P05 receives operator/config/doctor contracts; P06 receives backend/wake,
      attach/join and connector seams; P07 receives failure/load/recovery evidence.
- [ ] Applicable format/lint/build/test/AOT/platform gates and opposite-family
      review/re-review complete; accepted non-blocking follow-ups explicitly owned.
- [ ] Parent receives scope deltas and gate status for roadmap synthesis; this
      phase author changes only this document.

### Planning-session validation boundary

Only bounded checks of this owned Markdown file are appropriate here: UTF-8,
final newline, trailing whitespace, balanced fences, local file links, unique
ordered W/G identifiers and C/L/T references against their source definitions.
These checks cannot establish runtime correctness, host support, independent
review or Markdown rendering across tools. Repository-wide code format/lint,
build, AOT and live tests are unrun in this documentation-only task. Reproducible
project-wide document tooling remains a setup follow-up if absent.

## F27 scope addition — operator text console

P04 supplies the same authorized bounded agent-status/output projection and durable human follow-up/targeted-stop operations for the F27 text web console. It does not create browser-specific job authority or bypass busy/foreign-activity policy. P05 owns the HTTP surface; existing CLI/MCP parity remains required.

See [the console plan and HTML mockup](../../ui/operator-console-plan.md). This explicit
user addition supersedes earlier blanket dashboard exclusions; richer analytics,
remote administration and terminal emulation remain outside scope.
