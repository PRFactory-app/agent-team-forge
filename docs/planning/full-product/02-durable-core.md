# P02 — Durable local core

## 1. Objective, scope, and status

**Draft plan; independent plan review is required before implementation.** No
runtime, platform, review, or Native AOT gate passes by publication of this plan.
P02 turns the persistence/lifetime contracts into a product-quality local core,
not a permanent single-team demo. It follows P01 and hands verified contracts to
P03, then P04. The parent owns roadmap integration;
[cross-phase contracts](contracts.md) own synthesis decisions.

The [shared brief](README.md) controls scope and waterfall order. This plan uses
[architecture](../../architecture.md), [implementation contracts](../../plan.md),
[PoC cases](../../poc.md), [terminal rules](../../terminal-modes.md),
[contributor policy](../../../AGENTS.md), and
[.NET 11 research](../../research/net11-process-api.md). The expanded baseline
in the shared brief supersedes older roadmap deferrals, not their safety rules.

Canonical requirements are [F01–F26](../../product-scope.md#2-product-feature-inventory),
not P01's local R groups; use [the crosswalk](contracts.md#1-scope-ids-and-phase-ownership).
P02 primarily delivers F01–F04/F09 and foundations for F11/F22/F24; downstream
phases retain full qualification obligations.

### Included

- One daemon per OS user and machine; separate daemon, CLI/client, and thin
  stdio MCP Host roles. Only the daemon opens the runtime job database.
- Authenticated, bounded local IPC; explicit role authority, delegation and
  restricted helper bootstrap foundations, without relying on caller role names.
- Real SQLite schema, migration compatibility, atomic acceptance/outbox,
  idempotency retention, attempt-start, conversation/resource claims, durable
  lifecycle/results/events, and fail-closed storage behavior.
- Daemon-owned dispatch and accepted policy, process provenance, distinct turn
  interrupt and agent stop, restart quarantine, and evidence-bound reconciliation.
- Stable principal/event/consumer foundations for P04. No second event store or
  disposable bridge identity masquerading as a durable consumer.
- Deterministic C# child-process fakes, actual crash barriers, meaningful TDD,
  published .NET 11 Native AOT tests, and bounded operational evidence.

### Excluded and evidence limits

P03 owns real managed Claude/Codex/Pi adapters, terminal binding and native
control internals, including native Windows Pi launch, same-session follow-up/
results, interrupt, separate stop and reconnect. Linux or cross-build evidence
cannot establish Windows support.
P04 owns complete team/nested delegation, messages, public read/ack, approvals,
budget allocation, wake and workspace/artifact features. P05 owns installers,
service/desktop integration, setup UX, update and uninstall. P06 owns additional
Pi lead-host/attach/integration proof, supported attachment/join and the opt-in
external connector; it does not defer mandatory managed Pi execution. P07 qualifies the whole system;
P08 owns release operations. No cloud dependency, custom model loop, dashboard,
generic plugin framework, or seamless process adoption enters P02.

The [M0 durable spike](../../spikes/m0-durable-core-plan.md) is a useful bounded
experiment, not P02 completion. P02 adds multi-scope authorization, retention,
migrations, cancellation/reconciliation and reusable durable foundations beyond
that spike. Existing .NET 10 and interactive-spike observations stay
version-specific; unresolved safety review findings must be fixed and re-reviewed
before any code promotion. This planning task performs no experiments.

The [few-hours Linux demo](../../spikes/e2e-demo-plan.md) remains an interim
checkpoint: a fake core pass is engineering evidence only; the real Codex/Herdr
demo belongs to P03. Neither checkpoint guarantees full core delivery within hours.

## 2. Entry criteria and predecessor inputs

Formal P02 entry requires full P01 exit and an approved contract/decision snapshot.
A separately authorized, reviewed Linux-specific experiment may proceed before
full gates under [contracts section 6](contracts.md#6-waterfall-path-versus-early-experimental-preview).
It completes no phase; promotion still requires full reviews and gates.
P01 must deliver:

1. Accepted/attempted/acknowledged/completed/uncertain semantics, authoritative
   evidence rules, explicit launch-mode policy, and physical ownership boundaries.
2. Supported initial OS/RIDs, actual Windows/macOS test access, local transport
   and identity bootstrap decisions, per-user singleton namespace, and private
   state/runtime locations. Missing platform access is a blocked gate, not a waiver.
3. Selected .NET 11 SDK/ref-pack and package feasibility evidence, including MCP
   registration, JSON generation, native SQLite and process APIs. Public candidate
   SDK is `11.0.100-rc.1.26425.128`; source presence is not installed/AOT evidence.
   Provisioning needs separate authorization; no implicit SDK or system changes.
4. Core limits and supported retry/retention windows, migration/backup policy,
   authorization matrix, and fake protocol evidence format. Resolve numerical
   limits before measurement, not after a failing test.
5. Disposition of spike safety findings and an explicit reuse-versus-rewrite list.
   No invocation of a spike CLI as a second scheduler or state authority.
6. Independent review of this major persistence/security/lifetime plan, resolved
   blockers, and implementation authorization. Required model availability and
   review provenance must be recorded honestly, never inferred from a request.

If P01 leaves an irreversible contract unresolved, stop the affected package and
return it to the parent for change control. Harmless scaffolding is not permission
to implement an unreviewed security or durability contract.

## 3. Core contracts to freeze during P02

### 3.1 Composition, transport, and authority

Exactly three production projects: **Host → Business → DAL**. Business directly
calls concrete feature-specific DAL atomic operations; DAL owns records, SQL and
migrations, not orchestration. Host references DAL only for daemon composition.
No Business-owned repository ports, mediator chain, extra Contracts assembly or
parallel runtime solution. Tests/fake executables are not production layers.

Host daemon mode acquires a per-user OS lock before opening/migrating the DB or
changing the endpoint. A second state-path argument must not bypass this lock.
Test isolation uses explicitly test-only namespaces. Crash-released locks are
not persistent PID files. Endpoint cleanup requires verified ownership; a losing
starter must not unlink the winner's socket. CLI/MCP modes only create IPC
clients; disconnect cancels response waiting, never committed work.

Use Unix domain sockets on Linux/macOS and user-ACL-restricted named pipes on
Windows, with private owner-checked directories and symlink/reparse-point checks.
No TCP listener. Versioned, length-bounded JSON framing uses source-generated
serialization, bounded connections/in-flight requests, read/write/handshake
timeouts and backpressure. Pin numeric limits in one versioned configuration and
wire fixture. Reject incompatible versions, malformed/oversize frames and unknown
operations before effects. MCP stdout is protocol only; diagnostics use stderr.

Transport peer checks establish local access, not team authority. Bind each
connection to an authenticated principal and capability scope: operator, delegated
team-role, or restricted agent/session helper. Credential rotation/revocation
invalidates affected sessions. Bootstrap uses permission-checked private storage;
child exchange is short-lived, single-use, session-scoped and atomically consumed.
Invalidate outstanding bootstrap grants on daemon epoch change rather than allow
restart replay. Provider-specific delivery of bootstrap material belongs to P03.
No secrets in argv, ordinary responses, logs or diagnostic exports. Payload IDs
never change sender identity. Same-user capabilities are not an OS sandbox against
an agent with unrestricted access to that user's files/processes.

### 3.2 One schema and migration stream

These are logical records, not a mandate for one class/table per row. Final SQL
and wire shapes are reviewed/versioned artifacts; later phases extend this same
schema through migrations, never maintain duplicate jobs, runs or inbox journals.

| Logical record | P02 invariant and later ownership |
| --- | --- |
| `schema_migrations` | Ordered version/checksum history; explicit supported upgrade range. Unknown newer, altered or unsupported old schema refuses startup without mutation. |
| `principals`, `teams`, `agents`, `conversations` | Stable opaque IDs and authorization scope; actual mode and session/binding generation distinct from display name and PID. P02 has minimal authenticated registration/delegation for core use; P04 extends lifecycle, membership and hierarchy. |
| `operations` | Principal/team/operation/key uniqueness, fingerprint version/hash, stable operation and resource IDs, result/error reference, retry expiry/tombstone state. Shared by submit, interrupt, stop and reconcile. |
| `jobs`, `dispatch_intents` | Accepted semantic payload and immutable policy snapshot; one unique initially unattempted intent per job, separate from delivery evidence. |
| `runs`, backend delivery attempts | Monotonic generation, unique correlation, pre-effect attempt-start, phase/evidence records, backend turn/session IDs when verified. No backend acknowledgment inferred from a local write. |
| `conversation_claims`, `resource_claims` | At most one active machine turn per conversation; conservative exclusive workspace/resource identity where needed. Quarantine retains claims. No expiry alone authorizes replacement. |
| `results`, `events` | Atomic terminal result/state/event; globally monotonic committed event sequence, stable IDs, scope and versioned bounded payload. Results can be inline bounded data or verified file references. |
| `observed_activity`, `reconciliations` | Foreign activity stays separate from jobs; evidence provenance, resolution identity and audit trail. Human activity pauses automation. |
| `consumer_cursors` | Durable team-role principal and stream identity, active consumer generation and retention boundary. Any experimental internal cursor has an explicit sequence domain and migration path; P04 owns recipient cursors, canonical batch receipts and complete public read/ack. |

SQLite lives on local disk, never NFS/SMB. Require WAL, foreign keys,
`synchronous=FULL`, bounded busy handling and short transactions. Check existing
schema compatibility before journal changes or migration writes. Migrations run
under the singleton lock with dispatch disabled, using transactional steps and a
consistent SQLite backup before upgrade. Never copy only a live main DB file.
Test one realistic previous-version fixture to current version, interrupted
migration, backup integrity and restore into isolated state. Unknown schemas or
corruption never trigger creation of empty replacement state.

P02 implements the migration/backup primitives and maintenance interlock; P05
supplies operator upgrade UX and P07 qualifies release upgrade histories and
broader recovery. Restored state starts quarantined: a backup may predate real
external effects, so queued status in a restored snapshot is not proof of safe
redelivery. Reconcile against surviving processes/evidence before dispatch.

### 3.3 Atomic operations, idempotency, and evidence

1. **Accept-or-get:** Business validates authority, explicit mode, target,
   capability, payload, quotas and policy. One DAL transaction checks duplicate
   operation first, then atomically enforces admission, stores key/fingerprint,
   job, unattempted intent and acceptance event. Return acceptance only after
   commit. Duplicate retrieval still requires current authorization.
2. **Begin attempt:** one conditional transaction claims eligible intent,
   conversation and relevant resource, verifies no quarantine/foreign activity,
   enforces concurrency, and persists generation/correlation/attempt-start.
   Commit before process spawn, terminal launch, prompt write or other effect.
   Claims persist through uncertainty; an in-memory lock is insufficient.
3. **External work:** outside SQLite transactions, under daemon-owned lifetime
   and accepted policy. Journal effect substeps when launch and prompt delivery
   are separate; a restart cannot treat an unfinished substep as safe to replay.
4. **Record evidence/complete:** validate scope, binding, run/generation,
   correlation and legal transition. Atomically persist terminal state, result
   and completion event, with uniqueness making repeated evidence harmless.
   Release claims only when physical idle/termination is verified; logical job
   completion alone cannot prove no surviving writer. Stale evidence is rejected
   or retained diagnostically, never applied to a newer run.
5. **Recover/reconcile:** safe ordinary restart dispatches only genuinely
   unattempted work. Started nonterminal attempts enter `needs_reconciliation`;
   no timeout, missing PID, EOF or lost ack authorizes blind retry. An authorized
   idempotent resolution accepts authoritative evidence, abandons safely, or
   permits a separately accepted new-key operation after verified idle/stop.
   New execution links to the prior job/run for audit; it never erases history.

State distinguishes `accepted`, `dispatching`, `backend_acknowledged`, `running`,
explicit blocked reasons, terminal completed/failed/cancelled, and
`needs_reconciliation`. `foreign_busy` is conversation/session state. A stop or
interrupt request has its own operation outcome: accepted is not confirmed
interruption. Failed/cancelled requires evidence sufficient for that outcome;
uncertain effects stay uncertain. Backend exit is not successful turn completion.

Fingerprints use a versioned canonical encoding of validated semantic fields:
operation, target/conversation, exact instruction text, backend/mode choices,
workspace, permissions and policy/options after defaults resolve. Transport IDs,
JSON property order and presentation names do not define meaning. No silent
Unicode normalization or newline alteration. Same key/same meaning returns the
same operation, even after completion; different meaning conflicts without effect.

Retention must be bounded without making ancient retries new work. Proposed
contract for P01/plan-review approval: authenticated principals receive bounded
idempotency epochs; keys are unique within principal/team/operation/epoch. Full
operation responses survive the advertised retry window, then compact tombstones
return stable identity or `operation_expired`, never create a replacement. An
epoch is retired only after all its keys are outside their retry windows; a durable
minimum accepted epoch rejects old/unknown epochs after tombstone compaction.
Outstanding jobs/results retain their own references independently. Never prune
keys into an unrestricted UUID namespace. If this epoch contract is not approved,
retain tombstones and reject admission at quota rather than silently prune; that
bounded fallback and its operational limit must be explicit.

### 3.4 Events and P04 consumer boundary

Acceptance, terminal transitions and reconciliation append compact events in the
same transaction as state. Events are notifications about committed state, not a
second job-command queue; streaming token deltas need not be durable events.
Public snapshots/results remain authoritative. Sequence order is commit order;
gaps are allowed, sequence reuse is not. Scope filtering must not leak other
teams' payloads or grant access merely because a sequence is known.

P02 implements authorized consumer binding and atomic generation advancement on
reattach, independent of sockets; restart does not reset identity. Store cursor
and retention metadata, but do not expose an unrestricted `ack(sequence)` API.
P04 adds a recipient-delivery projection in the same schema, referencing existing
event/message IDs, not a parallel authoritative event store. Global event sequence,
recipient sequence, run generation and consumer generation are separate fields;
none substitutes for another. Backend delivery attempts are not recipient deliveries.
P04 owns migration of experimental internal cursors and complete public read/ack.
Canonical batch receipts bind principal, stream, consumer generation, preceding
cursor and exact issued batch; only a matching receipt may advance ack. Canonical
delivery has no destructive filter. Filtered inspection/search is read-only and
cannot advance its cursor. P04 supplies retry-safe read/ack and explicit revocation
markers or retention-gap/recovery behavior using these existing foundations. P02 tests cursor generation fencing internally, not a
pretend complete event-delivery service. Never garbage-collect unacknowledged
history silently: use documented retention floors and explicit expired-consumer
resync, while preserving required result/audit references. P04 owns wake cursors
and coalescing policy, not another source of lifecycle truth.

### 3.5 Process, cancellation, and resource ownership

Persist launch provenance: daemon epoch, run generation, process start identity,
OS supervision identity, backend binding and terminal/provider IDs where verified.
Hold direct-child handles while available; PID lookup or `SafeProcessHandle.Open`
is not ownership proof. .NET 11 API choice follows tested platform behavior;
`StartAndForget` is not a managed-run primitive, capture helpers need byte bounds,
and `StartSuspended` is not a Linux facility.

Separate request-wait cancellation, queued-job cancellation, active-turn interrupt,
agent stop and daemon shutdown. Pre-start cancellation atomically removes dispatch
eligibility. Active control requires its own durable idempotent operation and
pre-effect marker. A lost control response is reconciled, not blindly replayed
unless the adapter proves the particular control is safely idempotent. Interactive
interrupt keeps agent/tab alive by default. Stop requires separate authority and
explicit intent. Timeout escalation requires the accepted policy and proven
ownership; otherwise report blocked/unconfirmed, not a hard-cap guarantee.

Daemon shutdown stops admission/dispatch, checkpoints durable state, and follows
accepted headless stop policy with bounded drain and descendant verification.
Never auto-kill live interactive TUIs on shutdown/recovery. If ownership is
uncertain, retain quarantine over conversation/workspace and report actionable
reason. Fencing DB generations does not fence file writes by old processes.

Continuously drain headless stdout/stderr independently of client speed; cap
frames, buffers, result files, logs, queue, active runs, event backlog and diagnostic
exports. Quota checks are atomic. A completion storage failure pauses new effects,
retains uncertainty and allows replay only from authoritative evidence. Keep
bounded result-file writes private and verified before DB references commit;
partial files are not results, and cleanup must preserve unresolved evidence.
P02 enforces local queue/runtime/output policies and approval-blocked seams; P04
adds team allocation/accounting and decisions. No invented hard token-cost cap.

## 4. Ordered vertical work packages

Each package uses red → green → refactor for critical behavior, real SQLite and
few deterministic process scenarios. H/B/D below mean Host/Business/DAL. Package
size is relative, not elapsed hours. Each depends on the preceding package unless
stated otherwise; fixtures may be prepared early, but implementation gates remain
ordered.

| Package | Observable result and deliverables | H/B/D touchpoints | Dependencies and meaningful tests | Size / confidence |
| --- | --- | --- | --- | --- |
| **P02-W01** Role-isolated durable acceptance | Minimal production solution, versioned wire/schema fixtures, private singleton daemon, MCP initialize/list/call and accepted job readable from a fresh bridge. Early published AOT chain. | H role composition/auth/framing; B authorize/accept/get; D migration v1 and atomic acceptance. | P01 entry; red failed-commit, two-daemon and unauthorized-call tests; bridge cannot open DB; MCP-to-SQLite AOT invocation, not tool listing alone. | M / medium |
| **P02-W02** Retry-safe admission across scopes | Minimal stable team/agent/principal registration and delegation, full semantic fingerprint/epoch contract, atomic bounded admission, retained operation outcomes. Not team orchestration. | H bounded commands and identity binding; B scope/policy/idempotency; D uniqueness, quotas, retention and credentials metadata. | W01; concurrent equal/conflicting submissions, lost response, changed defaults/version, old-key/epoch replay, cross-team reads/control, nonce replay/expiry and credential revocation. | L / medium |
| **P02-W03** Autonomous claimed dispatch | Real fake child and supervisor, commit-before-effect barriers, one active turn per conversation, resource quarantine claims, durable correlated completion. | H daemon workers/test-only channel; B dispatcher/fake adapter/evidence; D conditional claims, runs/deliveries/results/events. | W02; queued and active work survive bridge kill, two conversations progress within limits, same-conversation races yield one owner, late evidence/EOF/ack-only never complete. | L / medium |
| **P02-W04** Crash quarantine and reconciliation | Restart classification, inspectable blockers and authorized idempotent reconciliation, old writer exclusion. No fake automatic adoption. | H recovery/status/reconcile endpoints; B evidence and physical ownership decisions; D recovery transitions/audit and retained claims. | W03; abrupt kills at every commit/effect barrier, lost ack/final write, changed start identity, survivor/grandchild, foreign activity, lost reconcile response. | L / medium-low |
| **P02-W05** Distinct cancellation and bounded execution | Durable interrupt/stop operations, policy deadlines, approval-blocked/unsupported seam, slow-client-safe output and crash-loop breaker. | H control endpoints/shutdown; B control policy and bounded process drains; D operation journal, accepted limits, breaker/block state. | W04; cancel queued/active/terminal jobs, race completion with interrupt, stop scope, missing ack, approved/unapproved escalation, output burst, restart cannot reset deadline/breaker. | L / medium |
| **P02-W06** Durable event and consumer foundations | Stable scoped events and generation-fenced consumer binding, compact state/result queries, contract fixtures for P04 batch receipts and retention gaps; no public read/ack or wake claim. | H authenticated attach/status; B consumer authority/retention rules; D event sequence, cursor/generation and retention metadata. | W05; reconnect invalidates old consumer generation, duplicate completion emits one logical terminal event, restart preserves IDs and cursor, bounded backlog reports gap rather than silent consumption. | M / medium |
| **P02-W07** Storage maintenance and fail-closed operation | Reviewed upgrade/backup/restore primitives, compatibility refusal, safe maintenance mode, bounded sanitized diagnostics and capacity behavior. | H maintenance interlock/diagnostics; B drain/quarantine/retention; D migration, backup/integrity and compaction transactions. | W06; realistic prior-schema fixture, interrupted migration, full disk/busy/corruption and ambiguous commit tests, isolated restore quarantines possible prior effects; no empty fallback. | L / medium |
| **P02-W08** Published qualification and handoff | Reproducible C# scenario runner, per-platform AOT artifacts/evidence, operating/recovery instructions and reviewed P03/P04 contracts. | H real published entry points; B full fake lifecycle; D real persisted state across process runs. | W07; full applicable suite, native-platform process/IPC/AOT paths, opposite-family review, fixes/re-review and rerun. | L / medium-low |

Fake protocol emits separate correlated receipt/ack/final records and supports
blocked approval, foreign activity, bad frames, output bursts and descendant
writers. Test supervisor controls bounded barriers before/after acceptance commit,
attempt commit, spawn/prompt receipt, final evidence and final commit. Actual abrupt
termination proves crash paths; thrown exceptions and sleep-only tests do not.
Fake receipt traces count effects for assertions, not production recovery evidence.
Only the supervisor cleans up proven test-owned resources. Test controls remain
outside public MCP and production authority. No model calls or model spending.

## 5. Dependency contracts and handoffs

| Consumer | P02 contract supplied | Extension/ownership rule and verification artifact |
| --- | --- | --- |
| P03 managed adapters | Business-local backend/terminal boundary with explicit capabilities, launch/control/result evidence and bindings; core owns generation, claims, policy, operations and state. | P03 implements real protocols, not SQL or a second scheduler. Unsupported capability is explicit. Run same contract fixtures plus real L/T cases. Artifact: versioned interface/wire fixtures, fake conformance suite and crash traces. |
| P03 launch/recovery | Attempt commits before every launch/delivery; helper authority scoped to run/session; interactive provenance differs from direct child. | P03 supplies verified binding, foreign activity and replay evidence, not status guessed from text or PID. Any helper spool needs bounded authenticated replay/dedup under same run IDs. Artifact: ownership/evidence matrix with platform gaps. |
| P04 teams/policies | Stable principal/team/agent/conversation IDs, authorization checks, immutable accepted policy and resource claims. | Extend membership, nested delegation, approval records, budgets and workspace/artifact metadata via same migrations and Business operations. Parent-child roles cannot bypass core scope. Artifact: authorization and state-transition fixtures. |
| P04 events/read-ack/wake | Global lifecycle event sequence and durable team-role consumer identity/generation; retained result IDs; no public ack API. | Add recipient-delivery projection referencing existing event/message IDs, distinct recipient cursors, canonical receipts and complete fenced public read/ack; filtered inspection is read-only. Migrate experimental cursors; add explicit gap recovery and supported-host wake. Job commands never double as executable inbox messages. Artifact: reviewed schema/API compatibility fixture and explicit C11/C12/C32 remaining cases. |
| P05 platform experience | Daemon singleton/maintenance lifecycle, role-specific executable, transport/credential rules and capability diagnostics. | Installer chooses/persists explicit launch mode, packages native assets and launches in correct user/GUI context; no duplicate state DB or service-owned scheduler. Artifact: tested RIDs, dependency manifest, startup/stop/upgrade preconditions. |
| P06/P07/P08 | Stable local operation/idempotency surface and version negotiation; sanitized failure/measurement evidence and migration policy. | Fake external command-source redelivery uses same API, not external domain tables. Later connector, system qualification and release checks extend evidence; they do not retroactively complete missing core gates. |

Public tool names follow the shared API: accept-first-job/follow-up, job lookup,
interrupt, stop and reconcile. Spike-only `job_submit` must not become an
accidental incompatible public API. P01/parent freezes exact names and versions;
P02 maps CLI/MCP to the same Business operations. P04 adds team orchestration and
`events_read`/`events_ack`, rather than redefine acceptance or consumer identity.

Schema, IPC and event payload versions are separate. Breaking changes require
parent-approved change control and migrated fixtures. Additive fields require
specified compatibility behavior, not permissive deserialization that bypasses
policy. Do not export entire DAL rows as public wire contracts.

## 6. Acceptance gates and verification

All methods below are **planned**, not existing commands or passed tests.
Implementation must provide reproducible invocation, fixed limits, environment
manifest, binary/content identity, normalized IDs/transitions and sanitized logs
for each gate. Platform-qualified gates run on actual OS/RID, not cross-builds.

| Gate | Reproducible method and exact success criterion | C/L/T mapping and evidence boundary | Blocking condition |
| --- | --- | --- | --- |
| **P02-G01** Architecture and early AOT | Inspect project references/role composition; publish .NET 11 AOT and invoke real MCP initialize/list/call → IPC → SQLite → fresh bridge lookup. Only daemon opens DB; no unanalyzed trim/AOT warnings; correct native SQLite loads. | Core published path only; no real backend or TUI evidence. | Missing reviewed plan, SDK/ref-pack/package/native prerequisites, reflection-only registration failure or bridge DB access. |
| **P02-G02** Acceptance/idempotency | Real SQLite plus concurrent clients and commit barriers; exactly one operation/job/intent for equal keys, conflict with zero effects for changed semantics, safe old-key rejection after retention; quotas remain atomic. | C01–C03, C06–C07; C27 exact fake instruction transfer; C28 fake external source through same API. | Duplicate identity/effect, ack before commit, old retry creates new work, altered prompt or over-admission. |
| **P02-G03** Autonomous ownership | Abruptly kill bridge, release child barriers, reconnect; accepted queued work starts without client and active work completes. Race dispatchers and claims; one machine turn per conversation, no conflicting resource writer. | C04–C05; fake core only, not L03/T07 terminal survival. | Client token/tree owns work, concurrent claim succeeds twice or process leak escapes accepted policy. |
| **P02-G04** Crash/evidence/reconciliation | Kill daemon at attempt/effect/ack/final boundaries; restart same state, inspect receipts and journal. Unattempted work dispatches once; uncertain attempts remain blocked, stale evidence cannot mutate current state, repeated resolution returns same operation. | C08–C10, C13, C17, C31 with fake protocol; foreign/self-report fixtures cover core rules behind T15–T17, not terminal tests. | Blind retry, fabricated completion, partial terminal/result/event commit, unowned evidence accepted or claim released before physical safety. |
| **P02-G05** Process and cancellation safety | Native process runner exercises start-identity mismatch, survivor/grandchild and control races; assert no foreign process signaled and no new writer before verified idle/stop. Interrupt/stop retries retain identity, unknown outcome remains unconfirmed. | C14–C16, C21–C22 fake/native-process scope; interactive no-auto-kill policy fixture only, not real T08/T10. | PID-only signal/adoption, descendant survives claimed stop, hidden escalation or false hard cap. |
| **P02-G06** Authority and IPC | Malformed/oversize/slow frames, incompatible versions, wrong principal/team, expired/replayed bootstrap and revoked capabilities; inspect logs/argv/exports with sentinel secrets. Second daemon cannot mutate DB or endpoint. | C24–C26, C29; restricted helper exchange tested locally, provider bootstrap delivery deferred to P03. | Unauthorized leak/mutation, credential exposure, endpoint takeover, unbounded transport work; unavailable separate-user ACL test is blocked, not passed. |
| **P02-G07** Bounded failures | Fixed-cap output burst/slow-reader/backlog tests; actual bounded-volume disk-full where available plus separately labelled fault injection; busy/corruption/commit-error fixtures. No false acceptance or further dispatch on storage uncertainty; memory/log/queue stay within predeclared caps. | C18–C20; C23 fake approval-blocked/unsupported contract only. Event reread portion of C18 waits for P04. | Unbounded buffers/storage, empty-state fallback, false acceptance, bypassed approval; injected errors alone cannot pass actual disk-full path. |
| **P02-G08** Events and consumer foundations | Restart after atomic event write and race consumer reattach; stable event IDs/order, unchanged acknowledged position, exactly one current consumer generation, stale generations rejected by internal operations. | Partial C32 only. C11/C12 and full public C32 remain P04 gates; no L07 wake claim. | Socket-derived durable identity, cursor reset/skip, duplicate logical completion events, undocumented retention gaps. |
| **P02-G09** Migration and recovery | Known-version upgrade, crash mid-migration, newer/unsupported schema refusal, consistent backup/isolated restore with realistic active/terminal state. Refused schema unchanged; upgrade preserves IDs/policy/operations; restored effects quarantined. | C30 and selected C19; beyond M0 migration scope. Process kills do not prove hardware power-loss durability. | Destructive downgrade, missing WAL data, automatic replay after restore or unverified backup. |
| **P02-G10** Published platform and resource evidence | Run applicable G02–G09 scenarios using published daemon/bridge/fake, on Linux x64, Windows x64 and P01-selected macOS architecture. Record JIT/AOT startup, memory, idle, acceptance/event latency and raw measurements against frozen PoC budgets. | L08/L12 core fake/IPC/process paths only when actually executed; all real-backend L cases and terminal T cases unpassed by P02. | Missing OS access/toolchain blocks that platform and full P02 exit; Linux-only evidence may support labelled interim checkpoint, not waterfall completion. |
| **P02-G11** Review and handoff | Run format/check, Release build/analyzers, tests and published suite; independent opposite-family code review of actual snapshot, fix/re-review and rerun. Parent accepts P03/P04 contract bundle and remaining-case ledger. | C01–C32 disposition per platform, including partial/unrun rows; no blanket PoC pass. | Open correctness/security blockers, missing reviewer, unrun required gate or incompatible cross-phase contract. |

Implementation supplies reproducible repository commands for `dotnet format
--verify-no-changes`, Release build/test, warning-as-error analysis and RID-specific
`dotnet publish -p:PublishAot=true`, plus a C# scenario driver for published
artifacts. Use minimal shell glue only. Store exact paths/commands once the
production solution exists; examples here are not evidence that tooling exists.
Narrow analyzer suppressions require rationale and a published test of the path.
No JIT fallback may be called AOT success.

Measure fake local IPC with at least 1,000 operations per configuration and save
raw percentile data; follow PoC startup/idle sampling where applicable. Freeze
resource limits before runs. Report hardware/filesystem and platform-specific
memory metric; no model-token or total product memory savings inferred from fakes.

## 7. Operator acceptance demonstration

Use disposable private state and fixed harmless fake tasks, no backend credentials
or model calls. Planned user-visible sequence:

1. Start daemon independently of both client and its kill scope. Start second
   daemon and observe refusal without endpoint takeover. Connect authenticated
   MCP and CLI clients to the first daemon.
2. Register two authorized test scopes/conversations through minimal core
   registration. Submit bounded work; display stable job/operation IDs. Same-key
   retry returns same identity; changed payload conflicts; wrong scope is denied.
3. Queue work, kill submitting bridge abruptly, allow queued and active fake work
   to finish. Fresh authorized bridge retrieves committed results with no prior
   process memory and no duplicate fake receipts.
4. Crash daemon after attempt-start but before fake receipt. Restart: job is
   visibly uncertain; no replacement starts. Demonstrate safe evidence-bound
   reconciliation and separate new-key execution only after old writer is absent.
5. Interrupt one turn without stopping its fake session; separately authorize
   agent stop. Lost acknowledgment stays unconfirmed. Simulated foreign activity
   and approval wait block automation without bypass or invented completion.
6. Show consumer reattach fences previous generation without claiming public
   read/ack or wake. Show disk/queue/output errors and unknown-schema refusal
   leave accepted state recoverable, then inspect redacted diagnostics.
7. Demonstrate upgrade/backup/restore in isolated state, including restored-run
   quarantine. Clean up only verified test-owned processes/files.

Record published binary, OS/RID, versions, IDs, expected/actual transitions and
remaining limitations. A terminal-like fake or log view is not a real TUI demo.

## 8. Risks, decisions, stop/go, and rollback

| Decision or risk | Owner / required disposition |
| --- | --- |
| .NET RC/ref-pack mismatch, MCP trimming or SQLite native packaging | P01 inputs plus P02-G01/G10 evidence; stop on incompatibility. SDK/package changes require recorded decision and retest, not silent retargeting. |
| Exact retry epoch, payload canonicalization, limits and retention windows | Parent/P01 approve before W02; P02 delivers fixtures. Unresolved policy blocks pruning/public contract freeze. |
| Core event foundations versus P04 read/ack | Parent confirms single schema ownership and P04 batch/retention semantics before W06; no broad C11/C12/C32 pass from foundations. |
| Multi-step launch uncertainty and live orphan writers | Quarantine conservatively; P03 must supply binding/replay capabilities. Refuse adapters that need text tags, busy override, PID-only control or blind resend. |
| Cross-platform locks/ACLs/process descendants differ | Require actual OS tests. No fake Linux result implies Windows Job Object, macOS GUI or terminal behavior. Missing access is external wait and blocked gate. |
| Storage exhaustion or evidence file loss | Fail closed, bound admission/retention, preserve unresolved evidence. Recovery cannot guarantee arbitrary hardware power-loss or exactly-once effects. |
| Full same-user adversary | Document threat boundary; capabilities complement OS/backend permissions, not sandbox them. P07 broadens security qualification without relaxing P02 checks. |
| Prior spike code has unresolved findings | Reuse only after independent fix review; retain versioned provenance. No inherited approval for promoted code. |

**Go:** P02 gates pass for frozen required platform scope and parent accepts
handoff. A Linux fake checkpoint may be reported separately while full exit is
blocked. Waterfall progression cannot silently treat that as complete P02.

**Stop/replan:** any duplicate uncertain effect, false completion, unsafe signal,
authority bypass, unbounded resource path, incompatible schema/API or unavailable
required review. Budget/time pressure cannot remove these gates. Parent resolves
scope/platform changes explicitly and updates dependent plans.

**Rollback/safe recovery:** stop admission/dispatch first; do not kill live
interactive sessions automatically. Retain state, evidence and physical claims;
expose blockers with authorized reconcile actions. Revert executable only when
schema/wire compatibility permits. Never run an older binary against a newer DB
or edit migration numbers. Restore a verified consistent backup into isolated
state only after stopping the active daemon and inventorying possible survivors;
quarantine potential effects before resuming. Backup restoration may lose locally
recorded work since backup and cannot undo external effects. P05 supplies safe
operator update flow; P07 tests broader recovery history. Reverting a test run
cleans only verified test-owned resources, not user sessions/workspaces.

## 9. Relative effort and sequencing assumptions

Overall **L, medium-low confidence** until P01 toolchain/platform evidence and
review are available. Acceptance/SQL mechanics are relatively predictable;
physical ownership, retention semantics, migration failure paths and platform
qualification dominate risk. W01 is a small observable AOT checkpoint, not proof
that W02–W08 fit a few-hour budget.

Implementation stays vertical and sequential as listed; each package has tests
and local review evidence before the next relies on it. Independent review may
inspect stable slices, but final changed safety paths require re-review. External
waits include authorized SDK/native prerequisites, Windows/macOS access, reviewer
availability and parent contract decisions. No model spending is needed for P02.
Real backend costs and service/desktop testing belong to later explicit gates.

## 10. Exit checklist and definition of done

- [ ] P01 entry decisions and independent P02 plan review recorded; implementation
  authorization distinct from planning.
- [ ] W01–W08 implemented through exactly three production projects, without
  duplicate spike schedulers or client-side DB access.
- [ ] G01–G11 evidence includes exact platform/binary identity, commands, limits,
  results and explicit failed/blocked/unrun cases; C/L/T mappings remain honest.
- [ ] Accepted work, retry retention, attempt-start, conversation/resource claims,
  atomic results/events, scoped authority and physical recovery contracts hold.
- [ ] Operator demonstration includes negative cases and safe cleanup; published
  .NET 11 AOT artifacts, not only JIT tests, exercise critical paths.
- [ ] Schema/migration, IPC/event versions, authorization matrix, accepted-policy
  limits and P03/P04 fixtures are public and self-contained.
- [ ] P03 real-adapter obligations and P04 full read/ack/wake obligations are
  explicitly outstanding, not renamed P02 successes.
- [ ] Opposite-family code review completed, fixes re-reviewed, all applicable
  gates rerun; no open blocking correctness/security findings.
- [ ] Parent accepts contract bundle and integrates cross-phase choices/roadmap.
  Missing required platform evidence blocks full exit, not the written plan.

### Validation of this planning artifact

Only bounded UTF-8/newline/whitespace, Markdown structure, phase-ID consistency and
local-file link checks apply to this documentation task. They do not establish
plan approval, schema correctness, package compatibility, runtime behavior or
platform support. No agents, installations, Git operations, code changes or live
experiments are part of this task. Independent plan review remains outstanding.
