# P06 — Cooperative sessions, lead-host integration, and optional connector

## 1. Objective, scope, and status

**Draft plan; no implementation, independent review, or runtime gate is passed.**
P06 adds cooperative attachment/join, additional lead-host integration and the
optional connector to the accepted P02–P05 local product. Managed/spawned Pi is
already mandatory in P03 alongside Claude Code and Codex; P06 reuses it, not
implements it for the first time. Execution order remains
P01 → P02 → P03 → P04 → P05 → P06 → P07 → P08.

Canonical feature scope is [F01–F26](../../product-scope.md). Binding inputs are
[the brief](README.md), [cross-phase contracts](contracts.md),
[roadmap](../../roadmap.md),
[architecture](../../architecture.md), [execution contracts](../../plan.md),
[terminal requirements](../../terminal-modes.md), [PoC cases](../../poc.md), and
[review policy](../../../AGENTS.md). Available predecessor plans are
[P01](01-foundation.md), [P02](02-durable-core.md),
[P03](03-managed-agents.md), [P04](04-team-orchestration.md), and
[P05](05-platform-experience.md). Plans are input requirements, not evidence of
completed work. The clarified scope/brief assigns managed Pi to P03; any remaining
P06 adapter deferral in cross-phase text is stale. Pi lead-host integration stays
in P06.

Deliver these full-baseline capabilities:

- **F19 regression, not new implementation:** reuse P03's first-class spawned Pi
  backend and P05's installed qualification in both modes, including native
  Windows. Pi retains its own model loop, tools, authentication and UI.
- **F20:** at least one P01-selected independently launched cooperative join
  combination, plus verified managed-session rejoin. Consent, scoped authority,
  revocation and external lifetime limits are observable product behavior.
- **F21:** an available but default-disabled generic outbound connector, with a
  small public versioned contract and reproducible conformance peer. Pairing,
  remote identity/ownership, offline policy and export consent are required.
- **F12:** standard native Pi lead-host and attachment wake, separately qualified
  for every promised host/platform cell, with durable unread catch-up. Execution
  as a Pi worker is not evidence that an idle Pi lead can be woken. P01 owns early
  transport feasibility, including Windows/Pi gaps; P06 does not defer that proof.

Extend, do not reimplement, F01–F04 durable state/API, F08–F11 ownership/recovery/
team authority/read-ack, F13–F14 limits/workspaces/artifacts, and F15–F18 operator
and installation experience. F22–F24 migration/security/AOT apply throughout;
P07 owns cumulative qualification, and P08 owns F25–F26 release/operations.
These mappings are P06 contributions, not claims of complete cross-cutting coverage.

Excluded: arbitrary uncooperative Desktop attachment, PID adoption, keystroke
injection, terminal scraping, custom model loops, generic plugin loading,
distributed scheduling, dashboard, remote administrative listener, replacement
of another worker, mandatory proprietary integration, or external domain types
in the core. No private server or unavailable third-party source blocks standalone
readiness. A named third-party adapter can be proposed later as an optional,
separately qualified contract client.

Existing spike evidence is not Pi, join, connector or .NET 11/AOT approval.
Unresolved spike safety findings must be closed and independently re-reviewed
before affected code is reused. This document adds no runtime evidence.

## 2. Entry criteria and inherited boundaries

1. **P01:** approved exact Pi version/public surface and mode/platform capability
   manifest, cooperative join handshake, host-wake decisions, and generic
   connector version/authority/export design with bounded feasibility fixtures.
   P01 proves feasibility and freezes interfaces; it need not deliver a finished
   production connector. Missing or contradicted feasibility returns to explicit
   change control, not an invented private-server dependency.
2. **P02:** accepted local singleton/IPC authentication, durable operations,
   idempotency retention, pre-effect attempts, physical claims, evidence and
   migration/backup primitives. Only daemon mode opens runtime SQLite.
3. **P03:** implemented, reviewed Claude Code/Codex/Pi execution in both modes,
   including actual Windows Pi spawn, same-session follow-up, final native
   correlated results, targeted interrupt versus stop, reconnect and owned
   process identity. Require visible selected Windows terminal and explicit
   headless evidence, wrapper/quoting/Unicode/environment/credential/private-IPC
   and descendant ownership tests. Linux or cross-build evidence is insufficient.
   Missing mandatory Pi evidence blocks P03 exit and P06 entry; it is not P06 work.
   **P04:** accepted nested grant attenuation, scheduler/budget reservations,
   teams/messages, canonical read/ack, wake and reconnect generations, bounded
   workspace/artifact retrieval. Pi and remote jobs use these same Business
   operations. P02's C11/C12/C32 foundations alone do not satisfy P04 contracts.
4. **P05:** accepted installed Linux x64, Windows x64 and selected macOS
   architecture/provider profiles; explicit mode selection, actual service/GUI
   context doctor, credential handling and upgrade/rollback procedures. P01's
   current macOS candidate is arm64; consume the final accepted manifest rather
   than silently choose another architecture or provider.
5. Independent review of this major security/persistence plan, disposition of
   blockers, implementation authorization, actual platform access, approved live
   test budget, and required separate implementation reviewers.

Planning can proceed without these completed artifacts; implementation and full
exit cannot. Record contract revision mismatches before coding. No SDK install,
Git operation, system change, live model spend or agent launch is authorized by
this plan-writing task.

## 3. Product contracts

### 3.1 Reused Pi backend versus additional lead-host capabilities

Consume P03's implemented, qualified Pi backend and P05's installed profiles.
P06 does not add a second Pi execution adapter, scheduler or authoritative job
store. Changes needed for join/lead integration extend existing feature contracts
and rerun affected P03/P05 gates; missing mandatory backend behavior returns to
its owning phase rather than being silently deferred here.

A native headless control path does not prove control of an existing interactive
TUI. Do not assert current Pi RPC supports that topology. P06 attachment and
lead-host additions need their own proven public cooperative/native surfaces,
exact versions, protocol shapes, dependencies and fixtures before W01 closes.
Read pinned public Pi documentation and relevant cross-references fully before
API design; unknown APIs remain proof gates, not invented commands. If a host
component requires a non-C# runtime artifact, obtain explicit language/dependency
approval first; do not add a side runtime or private protocol clone.

For each backend × version × mode × provider × OS × launch-context cell record:
start/resume, same-conversation send, native delivery-to-turn correlation,
authoritative completion, approval/question observation and response, usage
fidelity, targeted interrupt, separately authorized stop, human/foreign input,
outage replay and reconnect. Use verified/blocked/unverified/unsupported states
per capability. Unknown versions fail admission rather than inherit support.
Required managed Pi cells are P03-owned: both modes across the three baseline
platforms, with native Windows evidence mandatory. P06 preserves and regression-
tests these cells; unsupported required semantics block predecessor acceptance
or require visible scope change, never attach-only substitution.

Use P02 acceptance/attempt/result transactions and P04 scheduler and policy.
Persist attempt generation and correlation before each launch, prompt or control
effect. A missing reply cannot authorize replay. Completion requires trusted
backend lifecycle or verified durable records bound to session, turn and attempt;
model self-report, text tags, tab presence and silence are not authoritative.
Approval waits remain explicit, never bypassed. Interrupt preserves interactive
agent/tab by default; stop requires separate authority and verified ownership.
No mode fallback, automatic TUI termination on recovery or promise of a hard cost
cap without enforceable telemetry/control.

Maintain a **separate lead-host/attachment manifest**: host version/platform,
launch context, bridge placement, authorized native session, consumer principal/
generation, registration, native idle-wake path, coalescing/backoff and catch-up.
Native wake is the default standard under [contracts section 4](contracts.md#4-host-wake-and-backend-support-are-different),
not an optional feature or watcher-driven normal path. Every promised required
Pi lead-host and attachment cell needs native evidence, including Windows where
promised. One host is an early checkpoint only; missing transport or access blocks
full exit or requires explicit owner scope decision. Manual read/catch-up is
explicit degraded recovery, never a silent substitute. No terminal keystrokes
or tight model polling on the normal path.

Use the public [PR #70 design](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70)
and [research](../../research/native-wake-pr70.md), not copied-source/license
approval or our runtime test evidence. PR #70 is merged to main at
`471a17514d041e09b69cb24b910e418da28d2027`; source inspection pins head
`6da4e041f95e62e4c5f3a9ddf8f043c0fe7d769e`. Its Linux Claude
own-child inbox socket and Codex authenticated thread/home generation registration
with bounded `codex queue` guide the contract, not an invented Pi API. Windows
W1–W3 passed: native Codex wake is reference-proven on both operating systems,
not yet in our .NET integration. Claude Windows is unsupported there pending cancellable
named pipes; macOS reader and Pi are not covered. P01 must retire required
feasibility gaps early; P04 owns Claude/Codex product integration. P06 proves
Pi/cooperative native surfaces separately, preserving P03 Windows managed Pi.

Reuse P04 authenticated read/ack, notifier budgets and generation fencing. Commit
messages before wake; failed wake is reported separately and cannot fail a
committed send. Notices contain no message bodies or credentials. Successful
transport write is not acknowledgment or proof of model action. Validate nearest
host/session ownership, scrub inherited wake handles/environment on spawn/resume,
and fence one notifier per reader with an OS lock plus current generation.
Authenticate fresh registration on join/rejoin; reject stale or wrong-session
bindings. Apply finite coalescing/backoff/retry, revalidate authority before each
attempt and retain unread catch-up after disconnect/exhaustion. For any reused
Codex path preserve trusted cwd, scrubbed environment and read-only thread lookup;
do not generalize those commands into an unsupported Pi control protocol.

### 3.2 Cooperative join and managed rejoin

Three distinct operations remain visible:

| Operation | Required proof and authority | Lifetime and control limit |
| --- | --- | --- |
| Managed start | Daemon launch intent, verified backend/process/provider binding and accepted policy | Client-independent job ownership; backend/machine survival remains separately qualified |
| Managed rejoin | Existing durable agent/session provenance plus fresh authenticated native handshake and current physical ownership evidence | Rebind the same conversation; do not respawn, reset budget or infer authority from a PID/session filename |
| Independent cooperative join | Local operator consent and supported external host handshake proving the exact session and granted control capabilities | External host retains process/terminal lifetime; joining does not make it daemon-owned or safely killable |

P01 selects at least one independent join combination, initially a Pi candidate.
P03 already owns managed reconnect/reconciliation. P06 qualifies the selected
independent join and additional consent/credential rejoin flow on every promised
platform, reusing P03 identity and ownership checks;
it does not declare arbitrary Claude/Codex/Desktop attachment supported.
Joining as a participant uses P04 membership/delegation rules, never arbitrary
team-role names from a payload. Observation, submit, interrupt, approval and stop
are separate grants. External stop is denied unless specifically consented,
provably targeted and supported by that cell; stopping the daemon never implies
permission to terminate externally owned sessions.

**Ticket exchange:** an authenticated operator issues a cryptographically random,
short-lived, single-use ticket bound to issuer/subject, team/agent, intended
session identity or handshake challenge, allowed operations/resources, expiry,
daemon epoch and invitation generation. Freeze lifetime and attempt/rate limits
in the reviewed public schema before implementation. Store a verifier, not the
raw bearer secret. Deliver through a private local channel, not argv, logs,
transcripts or ordinary diagnostic output. The cooperating host proves possession
and native session binding; Business validates consent and attenuates authority.
DAL atomically consumes the ticket and commits grant/binding/reconnect generation.
Concurrent redemption has exactly one winner. Lost exchange response requires
fresh operator-authorized reconnect; replaying the ticket never reissues a secret.

Epoch change invalidates unused tickets. Session replacement, expired consent,
wrong principal/team, altered scope or mismatched challenge fails before effects.
Reconnect rotates scoped credentials and advances binding/consumer generations;
stale bridges cannot send, report current evidence, read or ack. Do not conflate
consumer, backend run, binding and remote lease generations. Revocation durably
fences queued/new effects and descendants of the grant; ongoing physical work
follows accepted policy and may remain uncertain. Revoke is not retroactive
cancellation or proof that an external writer stopped.

Human or uncorrelated input records foreign activity and pauses automation,
including after the human turn finishes. Clearing requires authorized fresh
verified-idle reconciliation against the latest activity revision. External
host closure/control loss blocks unsafe work and preserves claims; it never
causes automatic reopen/resend. Existing foreign work is not attributed to the
next daemon job. An unobservable human/control race makes that automation
capability unsupported, not exclusive merely because SQLite holds a lock.

### 3.3 Small public connector v1 contract

The production connector is an explicitly registered feature adapter, using an
outbound authenticated HTTPS request/response transport to an operator-approved
origin. No inbound daemon port, remote administration or automatic discovery.
Pairing is an explicit local action against the approved peer; enabling is a
separate recorded choice. Pairing traffic requires that consent. Default/off or
revoked connector performs **no DNS, connect, retry, heartbeat, telemetry, update
check or export traffic attributable to this feature**. Backend model traffic
is a separate feature and must not be misreported as connector contact.

Publish a small provider-neutral JSON contract with examples, schemas, bounded
errors and a C# conformance peer runnable without model credentials. This is a
test executable, not a fourth production project or external service dependency.
Use P01's frozen contract; incompatible corrections require version/change review.
The following is the minimum v1 vocabulary, not an unspecified future server API:

| Operation | Required fields and semantics |
| --- | --- |
| Negotiate/pair | Protocol major/minor, supported capabilities, peer identity and approved origin, local installation identity, one-use pairing challenge; provision scoped expiring credentials through an authenticated consent flow |
| Receive assignment/control | Peer namespace, stable external command ID, external work ID, semantic payload, requested operation/target, ownership generation, lease ID and validity, immutable policy proposal; bounded batch with receipt cursor |
| Acceptance receipt | Command ID, local operation/job ID, fingerprint version, accepted/rejected/uncertain disposition; return accepted only after durable local mapping and acceptance commit |
| Renew/release authority | Work ID, owner installation, expected generation/lease, expiry and explicit release acknowledgment; compare-and-set, never implicit transfer on socket reconnect |
| Publish result/status | Stable export envelope ID, command/job/run IDs, authority generation, committed status, approved bounded summary/artifact descriptors and payload digest; no raw DAL records |
| Acknowledge result | Envelope ID, digest, peer identity and durable receipt ID; peer commits receipt before ack, duplicates return the same receipt |

Reject unsupported major versions and unknown effectful operations before
mutation; negotiate minor features explicitly. Freeze numeric frame/batch/queue,
retry, lease/skew, credential/ticket lifetime and retention limits in the public
v1 fixture. No unlimited queues or ambiguous optional security fields. Errors
must distinguish unauthorized, conflict, stale authority, expired, unsupported,
quota, reconciliation-required and transient-unavailable without leaking secrets.

Pair credentials to one peer origin/identity and installation with team/resource,
operation and export scopes. Credential storage extends P05's private credential
facility; remote credentials never become local operator credentials. Require
validated TLS; no insecure fallback or automatic cross-origin redirects. Reject
unapproved endpoint changes, metadata/link-local targets and credential-forwarding
redirects; explicit test-only loopback conformance configuration is not a production
exception. Rotation fences old credentials. Local revoke/disable closes transport,
cancels pending attempts and prevents reconnect; it does not contact the peer to
announce revocation. Peer-side revocation, if needed, is a separately authorized
operator action. Bytes already transmitted cannot be recalled.

**Identity and durable delivery:** key external IDs by peer identity/namespace,
installation and operation, not free text work ID alone. Atomically map stable
command ID and semantic fingerprint to P02's local operation/job/intent. Same ID
and meaning returns the same operation after reconnect/crash; changed meaning
conflicts, including changed target, policy or generation. New remote ownership
does not relabel or restart an old accepted command. Use retained tombstones/epoch
refusal so ancient redelivery cannot create new work. Acceptance replies and
completion export are separate journals, not one delivery acknowledgment.

Results derive from committed local facts. Persist bounded export intent and
immutable sanitized envelope before transmission. Lost response permits resend
of the **same envelope**, never reexecution of the job. Peer deduplicates and
commits result receipt before ack; local durable ack records envelope/digest and
scope before retention releases it. An ack means remote receipt, not model action
or successful arbitrary side effects. Quota exhaustion pauses remote intake;
never drop unacked results silently or consume P04's local read/ack cursor.

### 3.4 Authority, lease loss, and no dual execution

Local jobs never require remote leases. Remote jobs require a valid scoped grant,
accepted policy snapshot and current generation/lease before admission and before
every new effect. Local operator safety pause/revoke/interrupt overrides remote
requests, but cannot silently transfer ownership to a local lead or restart a
remote job. A remote command cannot administer unrelated local teams, change
backend permissions, broaden nested budgets or bypass approval/human fences.

V1 default offline policy is **no new dispatch on lease loss/disconnect**; an
already active turn receives the accepted interrupt policy and remains claimed
until authoritative idle/stop evidence. An opt-in finish-current-turn policy may
allow that turn within its finite accepted deadline, but never queued successors.
Accept it only when peer agrees to retain exclusive ownership until verified
release. Unenforceable hard termination claims are rejected; unknown interrupt
outcomes keep ownership quarantined. Clock rollback/skew and restart without a
fresh trusted lease conservatively block effects, never extend authority.

Lease expiry alone cannot stop a physical writer and **never permits reassignment**.
Conformance peer must retain a possibly-live owner quarantine until durable release
with verified idle/termination evidence, or an explicit operator reconciliation
that establishes physical safety. Generation fences reject stale commands/results
as current control, but are not evidence of filesystem fencing. If a peer requires
timeout-only failover to another executor, that peer is incompatible with v1;
no dual-execution guarantee may be claimed for it. Test two simulated executors
and an actual surviving local process, not merely a generation comparison.

Disable/revoke durably prevents connector network and new remote effects, leaving
local execution/results usable. Already-active remote work follows its accepted
local safety policy; unresolved work keeps claims and an operator-visible reason.
Re-enable requires explicit consent, fresh credential/authority validation and
reconciliation; it does not flush stale control commands or auto-export backlog.

### 3.5 Explicit export and artifact policy

Default export is deny. Pairing or assignment acceptance is not permission to
send prompts, source, transcripts, logs, credentials or arbitrary files. Operator
approves a versioned per-peer/team policy covering status fields, summaries,
artifact classes, byte/count limits, redaction and retention. Snapshot this policy
at acceptance; later tightening/revocation applies before every transmission,
while later broadening needs new explicit consent. Preview pending exports locally.

Use P04 opaque artifact IDs and bounded authorized retrieval, never remote paths.
Resolve root ownership and validate symlink/reparse traversal and race-safe file
access at use. Stage immutable approved content with size/digest; reject changed,
missing or corrupt files. Test parent traversal, absolute paths, encoded traversal,
symlink swaps, Windows reparse/alternate-stream paths and cross-team references.
No arbitrary remote download URLs or local file reads in v1. Redact secrets and
sensitive metadata before queueing export; scan sentinel credentials through
errors and retries. Content redaction cannot guarantee removal of every secret;
operators must approve public-safe inputs/artifacts. Revocation prevents future
sends but cannot erase copies already received by a peer.

## 4. Ordered vertical work packages

H/B/D means Host/Business/DAL. Exactly three production projects remain:
**Host → Business → DAL**. Business calls feature-local DAL operations directly;
Host accesses DAL only for composition. Reuse P03's scoped Pi backend;
cooperative-session, additional lead-host wake and connector adapters live beside
their features. Register
explicitly; no assembly reflection/plugin loader, global repository framework,
extra Contracts project, duplicate scheduler or custom UI.

Each package includes red → green → refactor tests for critical behavior,
meaningful real SQLite/process tests, docs and review evidence. Sizes include
engineering, not external wait time.

| Package / dependency | Observable result and deliverables | H/B/D touchpoints and meaningful tests | Size / confidence |
| --- | --- | --- | --- |
| P06-W01 / accepted P01–P05, plan review | Freeze attachment/join and additional lead-host capability boundaries against inherited Pi backend/platform evidence; public fixtures and dependency manifest | H reports separate backend/join/lead capabilities; B reuses P03 admission and verifies proposed cooperative/host surfaces; D reuses versioned bindings/policy. Reject unproven TUI control or stale versions before effects; audit native Windows predecessor evidence | M / medium-low |
| P06-W02 / W01 | Regression-qualify reused P03 Pi backend through P04 policy and P05 installed contexts before cooperative integration; no new execution engine | H existing controls/reconcile; B existing Pi lifecycle and P04 policy; D existing attempts/activity/replay. Repeat first/follow-up/final result, human pause, approval, interrupt/stop, client reconnect and daemon recovery; actual Windows wrapper/security/ownership regression from P03 section 3.5 | M / medium |
| P06-W03 / W02 | Consent-based independent join and managed rejoin without spawn; scoped ticket/revoke/reconnect UX | H invite/join/rejoin/revoke; B identity/consent/binding and P04 attenuation; D atomic ticket consume/grants/generations. Replay/expiry/concurrent redemption, wrong session/role, crash after consumption, stale bridge and external-host closure | L / low |
| P06-W04 / W03 | Standard native Pi lead-host and attachment wake for every promised host/platform cell; reuse P04 infrastructure and P01 feasibility, no cursor skip or watcher/keystroke/model-polling normal path | H authenticated native registration/helper; B ownership, scrub, lock and bounded coalescing/backoff adapter; D existing notifier/consumer generations. Real idle wake, notice-only payloads, wrong/stale target, duplicate reader lock, inherited environment, committed send with failed wake, lost read/ack, revoke and reconnect; worker evidence stays separate | M / low |
| P06-W05 / W04 | Public connector v1 and local conformance peer; explicit pair/enable accepts one remote assignment into normal queue | H setup/credential identity/protocol composition; B scoped connector mapping and local API admission; D atomic external-ID mapping/acceptance/policy. Same/different-payload retries, commit loss, wrong peer and confused-deputy tests | L / medium |
| P06-W06 / W05 | Fenced remote ownership, offline/disable/revoke behavior without disturbing local teams | H authority/status/disable controls; B lease policy and P04 eligibility; D generations/lease snapshots/control attempts. Partition, expiry/clock anomaly, restart, stale owner and two-executor survivor tests; deny all connector network after disable | L / medium-low |
| P06-W07 / W06 | Explicit redacted result/artifact export with durable peer acknowledgment | H export preview/policy; B safe artifact selection/redaction/transmission; D export envelopes/acks and bounded retention. Traversal/reparse/race, denied data, duplicate/lost ack, full queue, revoke-versus-send and peer crash tests | L / medium |
| P06-W08 / W07 | Extend P05 installed profiles/doctor/update UX; run integrated UAT and deliver P07 bundle | H existing setup/CLI; B compatibility/drain/recovery; D additive migrations. Clean profile, upgrade/rollback/disabled-network and local regression on every baseline platform; review fixes and published AOT rerun | L / medium-low |

## 5. Acceptance gates and traceability

All gates are **planned/unrun**. Implementation supplies reproducible C# scenario
commands, fixtures and sanitized evidence manifests: source/binary identity,
SDK/ref-pack/packages, OS/RID/provider/backend/host/protocol versions, launch
context, exact invocation, operation IDs, expected/actual outcomes and blockers.
No commands or conformance peer are claimed to exist today. Fakes prove faults,
not real backend or GUI support. Existing C/L/T cases retain their original scope.

| Gate | Reproducible method and exact success criterion | Feature/case mapping and evidence limit | Blockers |
| --- | --- | --- | --- |
| P06-G01 | Review separate backend/join/lead manifests and accepted P03/P05 native Windows Pi evidence; negative admission tests cause zero effects; explicit mode required | F04/F20–F21, inherited F19; C25, T01–T03 partial | Unreviewed plan, missing frozen interface, unsupported mandatory surface or unresolved dependency approval |
| P06-G02 | Regress published P03 Pi first/follow-up/final native correlated result, approval, targeted interrupt/separate stop and client-death/reconnect through inherited P04/P05 contracts; real human input and neighboring tabs; crash daemon and replay or quarantine. Repeat P03 section 3.5 native Windows `.cmd`/Node quoting, Unicode, environment, credentials, private IPC and process/descendant ownership checks in visible selected terminal and explicit headless | Inherited F06–F09/F19, not first delivery; C01–C10/C13–C17/C21–C23/C27/C31; T04–T11/T15–T19 adapted Pi cases. L01–L06 are Claude/Codex cases: analogous Pi tests do not pass them | Missing real Pi mode/OS/context, false completion, unsafe survivor or unsupported required control |
| P06-G03 | Barrier-driven ticket consume/reconnect/revoke through authenticated IPC with real SQLite; then real independent cooperative join and managed rejoin on declared platforms | F03/F08/F10–F11/F20; C24–C25/C29/C32 partial. One redemption, no authority expansion, no spawn on rejoin, stale credentials/read/ack rejected; foreign input pauses | No proven session binding, ticket leak/replay, arbitrary role escalation, external lifetime advertised as managed |
| P06-G04 | Every promised Pi lead-host/attachment platform cell receives real bounded native idle wake; count notice-only writes versus model turns. Test burst/backoff/exhaustion, duplicate OS-lock owner, nearest-host/wrong-session, inherited environment scrub, revoked/stale registration, lost helper, reconnect/read/ack replay and failed wake after committed send | F11–F12/F20; C11–C12/C32 reused; L07 analogy only, not Claude wake pass. No bodies/secrets in notices, committed send stays successful, all unacked IDs remain readable, wake never acks; actual native Windows evidence where promised | Missing required transport/platform/budget/access blocks; worker/backend success, upstream tests, Linux/cross-builds, one-host checkpoint or manual recovery cannot substitute |
| P06-G05 | Run documented v1 fixtures against C# peer and real daemon API; peer retransmits assignment after lost acceptance response and restart | F02–F04/F21; C01–C03/C07/C28. Exactly one mapping/job/intent, changed meaning conflicts, unsupported version has zero effects. C28 itself is only fake-source seam coverage | Private server prerequisite, remote domain dependency, duplicate effect or missing public conformance fixtures |
| P06-G06 | Peer partitions/renews/revokes/restarts; delayed old-owner commands; two executors with one actual survivor; kill daemon around attempt and authority release | F08–F09/F13/F21; C08–C09/C13/C15–C16/C21–C22/C31 partial. Zero replacement execution until physical safety/release; stale owner cannot mutate current run; accepted offline policy remains fixed | Timeout-only reassignment, dual writer, unverifiable stop or local operator override treated as transfer |
| P06-G07 | Redacted export fixtures through peer durable ack barriers; artifact traversal/symlink/reparse races, cross-team IDs, secret sentinels, queue/disk failure | F14/F21/F23; C10/C19–C20/C24/C29 partial. Denied bytes never transmitted; same envelope/digest after lost ack, no job replay, quotas bounded | Unsafe file read/export, broadened policy without consent, unbounded queue or ack-before-peer-commit |
| P06-G08 | On each OS instrument connector DNS/socket activity and peer requests for fresh-disabled, configured-disabled, unavailable and revoked states; execute local jobs/messages/nesting/results during each | F01/F10–F11/F21/F26. Disabled/revoked: zero new network attempts after shutdown barrier, including retries after restart; unavailable: bounded retry and local operations meet inherited resource budgets | Hidden heartbeat/export, remote lease/account needed by local work, connector failure stalls local scheduler |
| P06-G09 | Real installed profile doctor, GUI absent, update/rollback and migration fixtures; publish and execute .NET 11 AOT MCP/IPC/SQLite plus Pi/join/connector paths on Linux x64, Windows x64 and selected macOS RID | F16–F18/F22/F24; C30, T12–T14 and changed T04/T07/T10 paths; L08/L12 runtime subset only, not L09–L11 Claude/Codex qualification | Missing real OS/desktop, native assets, unanalyzed AOT warnings, destructive rollback or silent headless fallback |
| P06-G10 | Complete UAT, cross-feature regression and independent review of actual implementation/evidence; fix/re-review and full applicable format/check/build/test/published gates | F19–F21 plus cross-cutting ledger. No blocking finding; every required cell has passed evidence, limitations and P07 owner | Missing opposite-family reviewer, unsupported required cell counted passed, absent handoff or unrun required gate |

P06 extends rather than replaces prior C01–C32/L01–L12/T01–T19 evidence. Record
partial mappings and explicit not-applicable reasons per cell. Missing Windows or
macOS access blocks full exit; Linux or fake-only preview is labelled accordingly.
Windows cross-builds do not prove native Windows Pi or GUI/ownership behavior.

## 6. User/operator acceptance

Use disposable public-safe workspaces and separately approved tiny live tasks.
Run from P05's actual installed user/service/desktop contexts, not only a shell.

1. Regress P03/P05 managed Pi with connector off: choose interactive explicitly,
   show real TUI, final native correlated
   result and same-session follow-up; repeat explicit headless flow. Remove GUI
   availability: show wait/error, never fallback. Local Claude/Codex regression
   remains available independently of connector state.
2. Kill lead/bridge while work runs; a new authorized lead reads the committed
   result. Separately kill daemon and demonstrate verified replay/rejoin or
   visible reconciliation without second launch or automatic TUI termination.
3. Independently start the supported cooperative host, consent to narrowly scoped
   join, then attempt ticket replay and another team's control. Both fail. Type
   human input; automation pauses. Revoke and reconnect: old credential/read/ack
   fails; closing external host leaves honest blocked/uncertain state.
4. For every promised Pi lead-host/attachment platform cell, complete work while
   host is idle; witness native wake separately from transport success. Revoke or
   replace registration, reject stale/wrong-session notices, fail wake after commit
   and show send remains committed. Disconnect/rejoin and catch up without storms
   or cursor skips. Show manual read only as explicit degraded recovery; record
   exact host versions and native Windows evidence, not managed-worker proof.
5. Explicitly pair/enable local conformance peer and approve minimal export.
   Redeliver one command: same local job. Drop result ack: same envelope resent,
   no execution repeated. Show changed-payload conflict and denied traversal.
6. Partition peer during work and request transfer from another executor. Show
   no new dispatch/dual execution, visible owner quarantine and local safety
   control. Finish-current policy, if offered, gets its own witnessed scenario.
7. Disable/revoke with queued export and retries pending; observe zero subsequent
   connector contact, no backlog flush on restart, and normal unrelated local
   jobs/messages. Explain that previously transmitted data cannot be recalled.
8. Upgrade and restore a test backup with unresolved work; demonstrate preserved
   IDs, revoked credentials, quarantined survivors and compatible rollback.

Record operator acceptance, failed/unrun steps and exact platform cells; a video
of a terminal or a model's success statement cannot replace authoritative traces.

## 7. Rollout, migration, disable, and rollback

Extend the one P02 migration stream only where required: cooperative/lead-host
capability and binding additions, ticket verifiers and consent/grant references, external-ID mappings,
peer/credential references, authority/policy snapshots and export/ack records.
Reuse P04 grants, nested budgets, activity, consumer receipts, workspaces and job
records. Keep schema, local wire, native adapter and connector versions separate.
No credential values or proprietary work-item schema enter public records.

Qualify through offline fixtures → consented disposable Pi/join trials →
conformance peer → installed platform UAT. Supported host integrations use native
wake by default, not a separate opt-in wake rollout. Join/control consent and
scoped registration remain mandatory; default wake never grants new authority.
Connector defaults disabled even on upgrade; adding join/lead integration never
changes existing Pi or other agents' mode/provider or grants. Doctor shows
unsupported/degraded wake, pending exports, ownership blocks and actual host/
backend distinctions without spending model tokens automatically. Join and
connector enablement require consent, not presence of credentials.

Before migration quiesce intake/dispatch and connector networking, use P02's
SQLite-consistent backup with necessary evidence/artifact manifest, and validate
schema compatibility. Test interrupted migration, active/uncertain jobs and
revoked grants. Unknown newer schemas refuse without mutation. Do not copy only
a live main DB file or infer a safe retry from a restored queued row.

Rollback first disables affected new effects/network, preserves reads/evidence
and keeps live interactive sessions untouched. Downgrade binary only when schema
and wire compatibility permit; otherwise restore into isolated state and retain
the newer journal for reconciliation. Restoring an older backup must not resurrect
tickets, paired credentials, lease authority or revoked grants: invalidate these
on restored-state startup, require fresh operator pairing/rejoin, and quarantine
possible post-backup physical effects. Restoration cannot undo exported data or
external commands. No direct schema-number edits or automatic cleanup of foreign
sessions/workspaces. P05 UX is extended, not replaced by a new installer/UI.

## 8. Decisions, risks, estimates, and stop/go

| Decision / risk | Owner and deadline | Stop rule / safe response |
| --- | --- | --- |
| Inherited Pi backend versus new cooperative/lead surfaces | P03/P05 backend evidence accepted before entry; P06 confirms public join/lead sources and dependency decisions before W01 | Mandatory backend gaps return to P03/P05 and block entry; new join/lead gaps block P06 cells. No assumed RPC-to-existing-TUI control, custom loop, private clone or unapproved non-C# helper |
| Supported join cells and external lifetime | Owner confirms P01 selection before W03 | No identity/consent proof means no join; managed rejoin alone does not satisfy independent-join baseline |
| Pi lead/attachment native wake API and host placement | P01 owns early Windows/Pi/required-matrix feasibility; P06 integration owner confirms before W01/W04 | PR does not establish Pi/macOS or Windows parity. Each promised native cell requires our tests; missing transport/access blocks gate or explicit scope decision. Manual catch-up is degraded recovery, never waiver |
| Connector v1 limits, lease/release and offline policy | P01 contract owner plus P06 independent reviewer before W05 | Unbounded values, timeout-only reassignment or ambiguous local/remote authority block acceptance |
| Export fields and credential/endpoint trust | Local operator/product security owner before W05/W07 | Default deny; missing export approval prevents transmission, not local result retrieval |
| Platform/SDK/package/reviewer access | Phase owner before live qualification | External wait; no fake/cross-build substitution, no inherited .NET 10/AOT approval |
| Third-party peer request | Separate optional scope decision after generic conformance | No named peer is needed for standalone full-baseline readiness; incompatible peer remains unsupported |

Overall **L effort, medium-low confidence**. W01–W08 are ordered vertical slices;
W03/W04/W06 and real platform qualification dominate risk. Fixtures can be prepared
early, but no finished connector is required from P01–P05. External waits include
public protocol clarification, approved model budget, desktop test access,
dependency authorization and independent reviewers; no calendar promise follows
from agent count or the near-term Linux demonstration.

Any duplicate uncertain dispatch, confused-deputy escalation, exported forbidden
content, stale-owner effect, unsafe physical takeover or unsupported required
capability is a no-go. Disable affected feature and preserve evidence/claims;
local use must remain available where safe. Scope changes record affected F IDs,
old/new contract, dependent phase impact, migration, security tests, owner decision
and re-review. Unsupported is an honest runtime response, **not a passed required
feature**. A narrower preview cannot unlock full P07 entry.

## 9. P07 handoff and definition of done

P07 receives a versioned public bundle, not an assertion that broad system
qualification already happened:

- Inherited P03 Pi backend/mode/platform manifest and P05 installed evidence,
  P06 regression deltas and separate additional lead-host/Pi wake manifest;
  native fixtures, public sources/dependencies and actual Windows/AOT evidence.
- Cooperative join/rejoin state machine, consent/ticket/grant/revocation schemas,
  session/ownership matrix, external lifetime and human-input limitations.
- Connector v1 schemas/examples/error/limit/compatibility rules, runnable C#
  conformance peer and adversarial scenarios. No inaccessible server prerequisite.
- External-ID/idempotency, generation/lease/release/offline policy and no-dual-
  execution fixtures, including stale owners and physical survivors.
- Export/redaction/artifact policy, durable result receipt/ack tests, disabled/
  revoked network evidence, bounded queue and local-isolation measurements.
- Migration/backup/restore/rollback fixtures and operator runbooks; evidence of
  credential invalidation and post-restore quarantine.
- F/C/L/T ledger distinguishing passed, partial, failed, blocked and unrun cases;
  reproducible commands, sanitized artifacts and accepted nonblocking follow-ups.

Exit checklist:

- [ ] Predecessor entry artifacts and independent major-plan review accepted,
      including P03 first-class spawned Pi and native Windows gates. P06 join or
      lead-host evidence does not substitute for mandatory managed execution.
- [ ] W01–W08 delivered feature-first within exactly three production projects.
- [ ] G01–G10 passed for every required declared cell; no fake/preview substitution.
- [ ] Every promised Pi lead-host/attachment native-wake cell passed, including
      native Windows where promised; default notice-only wake, authenticated
      registration, ownership/scrub/OS-lock fencing, bounded retry and unread
      catch-up proved. Failed wake never fails committed send. No manual-only
      waiver, one-host full-release shortcut or upstream-test substitution.
- [ ] Operator UAT covers both positive and attack/failure cases on actual platforms.
- [ ] Local-only and connector-unavailable operation preserve inherited guarantees.
- [ ] Opposite-family code review of actual diff/context/test evidence completed;
  blocking findings fixed and re-reviewed. GPT implementation needs Claude review;
  Claude implementation needs GPT/Codex review. Missing reviewer blocks exit.
- [ ] Full applicable format/lint/check, Release build/test, published .NET 11/AOT
  and changed platform/backend gates run; no self-approval or old spike waiver.
- [ ] Parent accepts P07 contract/evidence bundle and records scope deltas; P08
  receives support/compatibility and public documentation requirements.

**Done means implemented, reviewed, reproducibly qualified baseline capabilities
with explicit limitations, not completion of this document.** Independent review
is pending; this document does not claim separate review approval.

Planning-only validation is bounded to this file: UTF-8/newline/whitespace,
Markdown fences, local file links, ordered unique package/gate definitions and
referenced C/L/T IDs. No code, SDK, system, Git, live model or runtime changes.
Repository-wide format/build/runtime checks are not evidence for this prose-only
change. If standard document tooling is absent, add reproducible document checks
during authorized implementation setup; this task does not create tooling files.
