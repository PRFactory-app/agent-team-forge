# P03 — Safe managed Claude Code, Codex and Pi execution

## 1. Objective, scope, and evidence boundary

**Status: draft plan, not implementation approval or runtime evidence.** This is
one phase of the [full-product waterfall baseline](README.md):
P01 → P02 → **P03** → P04 → P05 → P06 → P07 → P08. A preview checkpoint inside
P03 does not satisfy P03 exit or authorize later phase implementation.

Deliver first-class managed/spawned Claude Code, Codex and Pi agents through the
durable daemon (F05–F09 and F19): start,
initial send, follow-up, status, result, approval/user-question observation and
safe response where supported, turn interrupt, separate agent stop, and recovery.
Both real interactive TUIs and explicitly selected headless execution are in
scope. Interactive means Herdr on Linux and visible tabs in the selected
Windows/macOS providers, with actual human input and verified machine control.
No log-tail substitute or silent mode/provider fallback is permitted.

The first demonstration is deliberately **Codex-only, Linux, Herdr interactive**.
It combines the reviewed adapter with P02's independent daemon, SQLite, private
IPC and thin MCP client; it is not another standalone control harness. The full
phase retains all three backends, both modes, and real platform evidence.
Managed Pi belongs to P03, not P06; attachment or Pi acting only as a lead host
cannot satisfy it. Native Windows Pi execution is mandatory. Linux execution or
a Windows cross-build cannot establish Windows support.

### Inputs and current limits

Normative inputs are [architecture](../../architecture.md),
[execution and recovery contracts](../../plan.md), [PoC cases](../../poc.md),
[terminal requirements](../../terminal-modes.md),
[product scope](../../product-scope.md), [cross-phase contracts](contracts.md), and the
[bounded Linux demo](../../spikes/e2e-demo-plan.md). The
[interactive spike report](../../../spikes/m0-interactive/REPORT.md) and
[independent review](../../../spikes/m0-interactive/CODE-REVIEW.md) provide
version-specific feasibility and unresolved safety findings, not an approved
adapter. Repair work is concurrent; no later revision inherits approval from
this plan or from an earlier review snapshot.

- The reported Linux spike used .NET SDK 10.0.401, Herdr 0.8.2, Claude Code
  2.1.283 and Codex 0.157.1. It showed real TUIs and useful native Codex control.
  Its controller-death experiment did not use the product daemon/SQLite/MCP
  stack. It does not establish complete C, T, or L cases.
- Concurrent dispatch, spoofable text tags, unproven terminal ownership, stale
  Codex binding, missing persistent human-wins pause, history errors treated as
  empty, unrestricted RPC and unbounded/non-durable replay were review blockers.
  Launch intent also did not precede terminal effects.
- Claude's hook lifecycle does not prove machine origin. Targeted native Claude
  turn cancellation lacks a verified solution in the investigated path. Esc
  observations are not approved cancellation, delivery, or result contracts.
- No managed Pi native control, interactive binding or Windows execution is
  established by the reported Claude/Codex spike. Pi capabilities require their
  own pinned public sources and actual mode/platform evidence.
- Human T05/T15, several terminal failures, headless flows, actual service
  contexts, .NET 11, published AOT and Windows/macOS support remain unverified
  by that spike. Codex approval and running-tool cleanup were not established.
- Upcoming product work targets .NET 11; candidate SDK is
  `11.0.100-rc.1.26425.128`. [RC1 research](../../research/net11-process-api.md)
  establishes source/API candidates, not installed-pack, package or platform
  compatibility. Existing .NET 10 evidence must retain its version label.

No installation, Git operation, live model test, agent launch or spike repair is
authorized by this document. An isolated old spike need not adopt three projects.
Promotion into production follows **Host → Business → DAL**, by feature slices;
never execute the spike CLI as a second scheduler or state authority.

### Explicit exclusions and phase boundaries

| Phase | Responsibility at this boundary |
| --- | --- |
| P01 | Freeze baseline, capability decisions and selected versions/providers; perform early platform risk probes for GUI context, identity, native control, ownership, SDK/AOT feasibility. A fundamental Windows/macOS impossibility must not first be discovered in P05. Unresolved mandatory risks are explicit blockers or reviewed baseline decisions. |
| P02 | Supply durable acceptance, claims/generations, fake-process crash tests, private IPC, daemon lifetime, authorization and result/event storage. P03 extends these contracts rather than duplicating them. |
| P03 | Implement and verify Claude Code, Codex and Pi real adapter semantics, mode-specific launch/binding, approvals, foreign input, interrupt/stop, recovery and bounded transports. Exercise real providers on all selected platforms, including temporary intended production launch contexts. |
| P04 | Own multi-team/nested delegation, membership and workspace allocation/conflict policy, cross-agent approvals/budget policy, durable messaging and host wake/catch-up. P03 supplies backend primitives and enforces accepted execution limits; it does not invent team policy. |
| P05 | Own install/setup UX, production service/autostart and desktop-session launcher integration, doctor, packaging/update/uninstall and production provider qualification. Re-run P03 semantics in installed contexts, including T13/T14; P03 platform evidence does not qualify an installer. |
| P06 | Reuse P03's Pi backend for cooperative attachment/join, additional lead-host wake/integration and optional connector. Do not defer managed Pi or duplicate scheduling/storage. Backend execution never proves lead-host wake. |
| P07–P08 | Cumulative system qualification and release operations. No arbitrary existing Desktop attachment, generic plugin framework, dashboard or external orchestrator is required in P03. |

The clarified product scope and brief assign managed Pi to P03. Any remaining
P06 adapter deferral in cross-phase text is stale, not permission to delay F19.

## 2. Entry criteria and stop conditions before implementation

P01 and P02 are named predecessors; their signed-off artifacts, not their plan
filenames, are formal phase-entry evidence. A separately reviewed isolated
Linux preview may use the relevant durable-core safety subset before full
multi-platform phase exit, under [the experiment boundary](contracts.md#6-waterfall-path-versus-early-experimental-preview).
That exception never approves production promotion or declares P01/P02/P03
complete. Before formal real integration:

1. P01 records supported OS/architecture, terminal choice, CLI/control protocol
   versions, authentication/configuration context, and a disposition for each
   required capability. Windows Terminal is a candidate; the macOS provider
   must be selected with evidence, not assumed equivalent to another provider.
2. P02 passes independently reviewed durable-core gates using real temporary
   SQLite and a deterministic fake child. Acceptance and unattempted intent
   commit together; attempts precede effects; only the daemon opens runtime DB.
   Client cancellation cannot cancel accepted work. Single-daemon, private IPC,
   principal-scoped idempotency and schema/version refusals are in place.
3. The reviewed promotion snapshot closes relevant spike safety findings, with
   regression tests and re-review. Codex-only readiness may admit the preview;
   Claude and Pi blockers continue to prevent their promotion and full P03 exit.
   P01 must supply Pi mode-specific public control and native Windows feasibility
   decisions; P03 implements them, P05 qualifies installed contexts.
4. Independent review approves this major implementation plan. Implementation
   code requires separate opposite-model-family review under
   [AGENTS.md](../../../AGENTS.md), including fixes and re-review. No review is
   performed or claimed by authoring this document.
5. Explicit authorization covers disposable test workspaces, model budget,
   platform/desktop access and later SDK provisioning. Production context probes
   must use test-owned launch configuration, never a user's live sessions.
6. Adopt P02's accepted policy snapshot and workspace exclusion mechanism for
   a small test team. If physical ownership cannot be excluded, do not dispatch.

Unavailable platform access blocks that platform's gates. An unverified mandatory
native contract blocks that adapter, not an invitation to add a backdoor. P01's
unresolved fundamental platform decisions require change control before claiming
full-phase readiness. Safe offline contract work may proceed on approved slices;
it cannot convert blocked platform/provider requirements into passes.

## 3. Execution contract to implement

### 3.1 Evidence, capability and API vocabulary

Keep backend, launch mode, terminal provider and control path separate. Each
supported tuple records exact versions, protocol source, tested launch context,
capability and evidence revision. Expose `verified`, `unsupported`, `blocked` or
`unverified` per capability, not a single misleading “supported agent” flag.
A version change invalidates the affected qualification until re-tested.

| Operation | Required semantics |
| --- | --- |
| Start | Authorize target/configuration and durably accept agent, first job and unattempted launch intent. Return the same operation on same-key retry. A terminal can be pending or uncertain; acceptance is not launch success. |
| Send/follow-up | Accept into P02 queue, then atomically claim one job and one bound session. Deliver only under current verified binding, history and idle/foreign-input fence. Preserve the existing conversation; never implement follow-up by silently starting another agent. |
| Status/result | Separate accepted, dispatching, acknowledged, running, approval-blocked, terminal and needs-reconciliation evidence. Return compact results with IDs and artifact references. Turn completion is not task/goal success. |
| Approval/question | Persist native request ID, session/run generation, requested action, deadline and current state. Authorized decisions use idempotency and checked native response semantics; a lost response is uncertain, not permission to resend blindly. If response automation is unsupported, expose that and retain a verified human path. |
| Interrupt | Target the bound job/turn, not “whatever is active.” Repeated calls return one operation. Before-start cancellation prevents dispatch; after completion returns the existing terminal fact. Interactive interrupt preserves agent/tab by default. |
| Stop | Separately authorize and journal termination of a proven-owned agent/resource set. Confirm process/descendant outcome; a successful signal or closed tab is not confirmed stop or completed work. |
| Reconcile | Authorized, idempotent and evidence-bound: accept a correlated result, abandon without releasing unsafe ownership, or permit a new-key attempt only after verified idle/termination. It cannot repair uncertainty by changing a DB status alone. |

Provisional tool names follow the existing plan: `agent_start`, `agent_send`,
`agent_list`, `job_get`, `job_interrupt`, `agent_stop`, `run_reconcile`, plus a
small approval decision operation only after its native contract is qualified.
All controls pass through authenticated Host → Business → DAL use cases. No
arbitrary mutating RPC passthrough, diagnostic bypass, unsafe busy override or
client-side DB access. Diagnostics use bounded read-only allowlisted methods.

### 3.2 Commit-before-effect and ownership

Acceptance transaction includes scoped idempotency/fingerprint and unattempted
intent. Before **each** spawn, terminal launch, prompt delivery, approval reply,
interrupt or stop effect, persist that operation's attempt identity and expected
binding/generation. For multi-step launch, journal each effectful step so a crash
between app-server and TUI creation cannot authorize blind relaunch of either.
Never keep a SQLite transaction open while waiting on backend I/O.

DAL atomically compares and claims both job and per-session dispatch authority;
Business verifies the evidence revision/binding and rechecks before delivery.
Two same-job callers and two different jobs for one session must not both win.
A stale evidence revision, new foreign observation or unavailable history blocks
dispatch. This is a machine-dispatch fence, not exclusion of physical human input.
Race-time uncorrelated activity is quarantined, not assigned to the claimed job.

Binding includes agent ID, launch/attempt generation, selected mode/provider,
provider instance and tab/pane identity, backend session/thread, app-server or
runner process creation identity and control endpoint provenance. A PID, tab
name, one loaded thread, socket path or restored provider metadata alone is not
proof. Check live TUI association and server/socket identity before controls.
Detect replacement or ambiguous binding and fail closed. Teardown affects only
proven-owned resources; never close a shared terminal window or delete a
same-named pre-existing session. Uncertain live resources remain quarantined.

Headless direct runners and terminal-owned interactive TUIs have different
ownership mechanisms. Process groups/cgroups, Windows Job Objects, macOS user
sessions and provider parentage must be tested, including shell descendants.
Do not regain authority from a PID lookup after daemon restart.

### 3.3 Native correlation, history and foreign activity

Codex's app-server plus remote TUI is a candidate, not blanket approval. Require
native `clientUserMessageId`/`clientId` and authoritative thread/turn association
tied to the durable attempt; reject missing/conflicting identities. Preserve the
observed `completedAt` finality caveat in pinned-version fixtures and verify its
meaning for the selected version. Notifications can wake evidence collection;
they do not bypass finality checks. Headless app-server gets separate tests.

Claude stream-json is a headless candidate; inbox plus hooks is an interactive
candidate. A hook `prompt_id` can identify a lifecycle but cannot authenticate
origin of a prompt containing `[atf-job:...]`. Tags are informational for all
three backends. Claude needs a demonstrably trusted delivery-to-native-turn binding
that survives races and outage replay. Until found, ambiguous turns are foreign
or need reconciliation; they cannot automatically acknowledge/complete a job.
Undocumented stream control frames or a private SDK protocol are not solutions.
An official SDK alternative needs explicit dependency, public-contract and
AOT assessment before adoption.

Pi needs a separately verified public native control path for each launch mode.
Do not assume current RPC controls an existing interactive TUI, or invent request
names, correlation fields or finality guarantees. Before implementation, read the
pinned public Pi documentation and relevant cross-references fully; record exact
protocol shapes, dependencies, mode limitations and versioned fixtures. Any
cooperative interactive component needs proven binding to the actual launched TUI;
a non-C# component requires explicit language/dependency approval. Pi retains its
own model loop, tools, authentication and UI.

Prove initial delivery and same-session follow-up bind the durable attempt to a
native session/turn and an authoritative **final** result. A command response,
stream fragment, idle notification or model-written completion tag is insufficient
without proven native finality and correlation. Test missing/conflicting IDs,
human races, delayed/duplicate finals, lost replies and outage evidence. Unknown
approval/control behavior remains visible and blocks dependent mandatory cells.
Pi backend execution and Pi lead-host wake have separate capability manifests;
P06 adds lead-host integration, not missing managed backend functionality.

History unavailable is a blocked/uncertain state, never an empty history or idle
proof. A narrowly tested pre-first-turn exception may exist only for a proven
new, exclusively owned, empty session with authoritative first-send safety
proof. Allowlisted error codes alone do not establish those facts. Failure on a
materialized session, missing pages, truncation or stale status cannot authorize
send. Prefer blocking over an unverified first-turn special case.

Persist foreign/human observations even after their turns finish. They set
`foreign_busy`, pause all automatic follow-ups and invalidate prior idle evidence.
Human wins; only authorized reconciliation against fresh verified idle clears
the fence. Revalidate at dispatch, because reconciliation is not a permanent
lease on the keyboard. Human approval responses also need native observation so
machine response cannot race and answer an unrelated request.

### 3.4 Replay, deadlines and permissions

Prefer verified durable backend records for completion during daemon outages.
If a helper is essential, scope its authority to one session; specify a private,
bounded append-only spool with record IDs, native correlation, checksums/framing,
replay cursor/deduplication, retention and explicit write-failure/gap detection.
A full disk, lost/torn record or unavailable history cannot silently become a
successful hook or zero-result-loss promise. Terminal result, metadata and event
commit atomically; replay cannot complete a newer generation or change a final
run. Missing evidence leaves visible reconciliation with machine work blocked.

Bound connect/request waits, pending RPCs, inbound frames, prompt/output sizes,
stream drains, queues, spool/log retention and cancellation waits. Slow clients
must not block backend output or the TUI. Persist absolute accepted deadlines
and their policy basis; daemon downtime does not reset them. Restart first
reconciles ownership and turn state, then applies overdue policy to that same
turn. A timed-out request proves neither nondelivery nor termination.

Approval waits are durable and observable with no connected lead. Unknown
approval observation is explicitly unsupported, not fabricated `running`.
Deny/expire or wait according to the accepted policy; never auto-approve or use
blanket permission bypass. Interactive deadlines request verified turn interrupt;
without explicitly accepted termination escalation, unresolved interruption is
blocked/unconfirmed, not a guaranteed hard cap. Recovery never automatically
kills a live interactive TUI; stopping it requires explicit human intent.
Headless termination on daemon loss may be enabled only by an accepted,
platform-tested policy. Token/cost limits are advisory unless measurement and
control make enforcement demonstrable.

Use argument lists, allowlisted launch environment and private bootstrap files
or channels. No inherited host messaging credentials, tokens in argv/logs or
unrestricted child helper authority. Deliberately constrain backend config,
hooks/plugins and MCP discovery. Same-user capability isolation is not an OS
sandbox against a malicious process with that user's filesystem/process access.

### 3.5 Native Windows Pi qualification

Test the real Windows Pi distribution in a visible tab of the selected terminal
provider and in explicitly selected headless mode. Pin launcher, `.cmd` wrapper,
Node executable/version and control transport where used. Verify executable
resolution and argument handling across every wrapper boundary; argument lists
alone do not prove shell-wrapper quoting. Test paths/prompts with spaces, quotes,
metacharacters, newlines and Unicode, including non-ASCII profile/workspace paths.
Reject unsupported combinations rather than silently changing mode or shell.

Use disposable user/desktop contexts to verify `PATH`, home/profile, working
directory, allowlisted environment and backend credential access. Sentinel tests
must show no inherited lead/connector credentials or bootstrap secrets in argv,
logs or diagnostics. Test private IPC authentication/access controls and endpoint
replacement; pipe/path names alone never authorize control.

Record process creation/handle identity, native session/turn identity, terminal
provider/tab binding and wrapper → Node → tool descendant ownership. Test wrapper
exit, PID reuse, replacement endpoints, shared neighboring tabs, child survivors
and any Job Object assignment/teardown assumptions on Windows itself. Exercise
spawn, follow-up, final native correlated result, targeted turn interrupt versus
separate owned stop, client reconnect and daemon reconciliation. Interrupt must
preserve the interactive agent/tab; stop must account for owned descendants without
affecting unrelated processes. Reconnect proves current identity or quarantines;
it never adopts by PID or blindly respawns. Missing Windows GUI/machine access or
an unproven mandatory control path blocks full P03 exit. Cross-builds and Linux
runs are not Windows proof; P05 repeats these contracts in installed contexts.

### 3.6 .NET 11 candidates, not guarantees

Test the pinned SDK/ref pack and packages before using candidate APIs.
`SafeProcessHandle.Start` may help retain direct-child observation; `Open`,
`TryOpen` and PID lookups do not establish ownership. `StartDetached` does not
prove survival of service/provider teardown; `StartAndForget` loses required
observation and is not a managed runner. `KillOnParentExit` is a separately
accepted headless policy, unsupported on macOS, not interactive recovery.
`StartSuspended`/`Resume` are Windows/macOS candidates, **not Linux**. They cannot
make spawn atomic with SQLite or bind a terminal-owned child automatically.
`Process.Signal` is not portable native turn interruption; process exit is not
turn completion. Whole-output capture lacks the byte bounds needed for long
workers. Test bounded drains, descendants and kill scopes on each actual OS.
No source research or .NET 10 test establishes .NET 11 or broad AOT support.

## 4. Ordered vertical work packages

Each package includes focused red → green → refactor tests for safety behavior,
then integration and independent code review. Tests use C#/.NET; fake transports
and deterministic barriers precede approved live tests. H/B/D below mean Host,
Business and DAL; they are touchpoints, not extra layers or prescribed classes.

| Package and dependency | Observable result and H/B/D touchpoints | Deliverables and meaningful tests |
| --- | --- | --- |
| **P03-W01 — Fail-closed capability admission.** Depends on P01/P02 entry. | An authenticated start/control request for an unqualified tuple is rejected before effects. H exposes mode/capability diagnostics; B validates version, permissions, provider and public control contract; D stores versioned configuration and immutable per-agent policy/mode snapshot. | Capability matrix, review-finding disposition and version-pinned fixtures. Test unknown versions, missing explicit choice/provider/desktop, unsupported approval/control and attempted arbitrary RPC. Same-principal retry semantics remain P02's, not a second implementation. |
| **P03-W02 — Owned Codex interactive start.** Depends on W01. | Start returns durable identity then opens a proven-owned real TUI. H maps start/status; B performs staged app-server/provider/TUI launch and verification; D commits launch steps, bindings, session claims and uncertainty. | Unique resource provenance, allowlisted environment/bootstrap and targeted cleanup procedure. Crash at each launch step and lost response; retry creates no second resource. Test stale pane/socket, PID reuse, same-name foreign session and neighboring tabs using fakes before live launch. |
| **P03-W03 — Codex send, follow-up and result.** Depends on W02. | MCP client receives acceptance/status/result for the same conversation; daemon dispatches without a client. H stays thin; B checks binding/history/origin and bounded native transport; D atomically claims job/session and commits result/event. | Native-ID evidence mapping and foreign-busy reconciliation. Two-process same/different-job races, spoofed tags, missing/conflicting client IDs, history errors, stale idle, human turn completion and finality fixtures. Real first/follow-up and exact Unicode/newline/metacharacter delivery; slow/disconnected client cannot stall output. |
| **P03-W04 — Labelled Linux/Codex E2E preview.** Depends on W03 plus relevant safety re-review and reviewed Linux P02 safety gates; full-phase promotion still requires full P01/P02 exit. | Independent daemon + SQLite + thin MCP client controls one real Herdr Codex TUI. Kill the client/bridge, reconnect and read the committed result; follow-up stays in the same verified conversation. H provides bounded operator steps; B runs the reviewed adapter; D provides durable acceptance/result and no duplicate retry. | Preview evidence manifest and safe cleanup guide. Abrupt client kill during active work; accepted-unattempted versus attempted-uncertain crash barriers; daemon-restart negative scenario quarantines uncertain work without respawn. Harmless no-tool job; approval and optional interrupt/stop cannot be called supported without their later gates. JIT-only preview is labelled JIT-only; no full PoC, Claude, wake or AOT claim. |
| **P03-W05 — Codex control and explicit headless mode.** Depends on W04. | Both selected Codex modes expose durable approvals, honest interrupt/stop and deadline outcomes. H maps authorized controls; B implements mode-specific app-server/process semantics; D records native request/turn IDs, control attempts, decisions and policy deadlines. | Live approval with no lead, human decision observation, targeted interrupt with running tool/descendants and separate stop. Test stale/cross-session approval, lost reply, before-start/during/after-final interrupt, no escalation versus accepted headless escalation, output floods and stalled peer. Mode changes affect new agents only. |
| **P03-W06 — Claude start/send/result and controls.** Depends on W05 and resolution of Claude native-origin/cancel decisions. | Same public use cases work through verified Claude contracts in both modes, without pretending parity where unsupported. H reports precise capability/blocked reasons; B implements qualified stream-json or reviewed official alternative and real TUI control; D uses the shared launch/claim/evidence/control records. | First/follow-up, approvals/questions, interrupt/stop, exact prompt and mode tests. Mandatory forged-tag, race, lost-delivery, hook failure and origin-binding tests. Native targeted cancel must have actual authoritative outcome evidence; whole-agent kill is not a turn-cancel substitute. If no solution exists, stop this package for explicit product scope decision, not keystroke fallback. |
| **P03-W07 — First-class managed Pi.** Depends on W06 and P01 Pi public-control/Windows feasibility decisions. | Same start/send/status/result/approval/interrupt/stop contracts work for spawned Pi in both modes. H exposes existing authenticated controls and capability diagnostics; B implements proven mode-specific native binding/control; D extends shared attempts/bindings/evidence, never another store. | Public source/version fixtures; real initial/follow-up/final correlated result; approval where exposed; human pause; interrupt preserves interactive session; separate owned stop and reconnect. Run section 3.5's native Windows wrapper/quoting/Unicode/environment/credential/private-IPC/descendant tests. Lost reply, foreign turn, stale final and endpoint replacement fail closed. Unproven RPC-to-TUI control blocks admission, not deferred P06 work. |
| **P03-W08 — Outage replay and physical recovery.** Depends on W07; Codex subset tested earlier for preview safety. | Restart reconstructs evidence or quarantines work; client loss, daemon loss and backend/machine failure remain distinct. H exposes actionable reconciliation; B verifies survivor/rebind/idle policy and overdue controls; D persists observations, replay checkpoints, deadlines and idempotent resolutions. | Faults before/after effects and final commit; completion during outage; duplicate/torn/missing spool records; full disk; process survivors and shell children; backend EOF/malformed stream. Never auto-kill live interactive TUI, blind-replay a prompt or release a workspace fence while an old writer can remain. |
| **P03-W09 — Selected-platform semantics and exit.** Depends on W08. | Real Claude Code/Codex/Pi flows work in both modes on Linux x64, Windows x64 and P01-selected macOS architecture/provider. H uses temporary intended launch context; B verifies actual provider/process contracts; D preserves identical durability and migration guarantees. | Cross-platform evidence matrix, published-binary checks and sanitized operator guide. Repeat actual human/neighbor-tab, control, client death and daemon restart tests; T04/T07/T10 also in temporary intended production contexts. Pin packages/RIDs, assess trim warnings and run published AOT per platform. Missing machines or unsupported mandatory contracts keep full gate blocked. |

W04 is an early checkpoint, not a reason to skip W05–W09. Contract fixtures and
platform laboratories may be prepared from P01 evidence, but waterfall phase
exit order is unchanged. No promised completion “within hours” applies to full
P03 or the whole product.

## 5. Contracts handed to subsequent phases

| Consumer | Contract and invariants | Verification artifact to hand over |
| --- | --- | --- |
| P04 teams, budgets and workspace policy | Stable agent/job/run IDs; immutable mode and accepted policy; one machine turn per session; capabilities for approvals, usage, interrupt and stop. Supply workspace/resource exclusion and unresolved-survivor facts. P04 allocates teams/workspaces and policy, but cannot bypass a P03 physical-ownership fence. | Versioned request/result/event schema, state-transition fixtures and concurrent-claim/foreign-input tests. |
| P04 approvals and wake | Durable native approval identity, decision/expiry status, backend usage fidelity and completion events. Readable result is not host wake; P04 must qualify wake/catch-up independently. Unsupported approval automation remains visible, not replaced by permission bypass. | Approval/lost-response fixtures, result-event traces and capability limits. |
| P05 service/provider qualification | Required user/profile/credential/desktop context, environment allowlist, provider/process binding, cleanup and GUI-unavailable behavior. A restricted desktop launcher may create resources but never own durable scheduling. | Actual OS × mode × backend × provider × launch-context matrix; T04/T07/T10 evidence, command recipes and resource identities. P05 re-runs these plus installed T13/T14. |
| P06 extensions/attachment and lead integration | Reuse the implemented P03 Pi backend and P05 installed profiles. Cooperative join/attachment adds consent and scoped grants; lead-host wake is separately qualified using P04 delivery. Managed reconnect is already P03 behavior. No arbitrary adoption, shared RPC access, duplicate scheduler or authoritative store. | Three-backend managed lifecycle/capability manifest, native Windows Pi evidence, explicit unsupported combinations and separate lead-host evidence gaps. |
| P07/P08 qualification and operations | Version pins, safe disable/drain, no-blind-replay recovery, evidence gaps, resource ceilings, diagnostics redaction and migration compatibility. No exactly-once external side effects or seamless process adoption promise. | Gate ledger with binary/source identities, sanitized traces, replay fixtures, review disposition and tested rollback recipe. |

Schema changes extend P02 migrations for staged launch attempts, verified
bindings, session/evidence revisions, observed foreign activity, control attempts,
approval IDs and replay cursors only where absent. Keep native IDs with backend
and protocol version; do not expose raw database rows as public responses.
Expected-generation conditional writes protect terminal results and events.
Version IPC and capability payloads, reject unknown newer schema without mutation,
and specify retention/tombstones so an expired key cannot silently become a new
operation. Do not downgrade a DB to make an older binary start; reconcile and use
compatible recovery/backup procedures. No migration can revoke a live writer's
physical authority by itself.

## 6. Acceptance gates and traceability

All gates below are **planned/unrun for P03**. Proposed harnesses, manifests and
command recipes are deliverables, not claims that commands already exist.
Each execution records source/binary identity, SDK/ref pack/packages, CLI/provider
versions, OS/architecture, mode, launch context, job/run/native IDs, sanitized
observations, exact reproduction steps and pass/fail/blocked/unrun status.

“Partial” below describes coverage of an existing case, not permission to mark
that entire case passed. A mapping becomes complete only after every backend,
mode, platform, repetition and failure condition required by that case is
recorded. Existing P02 fake evidence remains necessary but is not live evidence.

| Gate | Reproducible method and exact success criterion | C/T/L mapping; evidence and blockers |
| --- | --- | --- |
| **P03-G01 — Admission and review** | Compare pinned capability manifest and reviewed promotion snapshot with P01/P02 artifacts. Exercise rejected start/control requests with an effect counter. Zero effects for unsupported, unauthorized or missing-mode requests; no unresolved blocking finding on promoted path. | C24/C25/C29, T01–T03/T12: partial until platform/context matrix exercised. Fake negative tests plus real doctor/provider observations. Missing plan/code re-review or contract source blocks promotion. |
| **P03-G02 — Launch/binding/claim integrity** | Deterministic two-process races and abrupt crash barriers around each launch/dispatch step, using real SQLite. One accepted operation/claim, no second launch/send after uncertain attempt; wrong PID/start/socket/pane identity never controls or tears down a resource. | C01–C03/C06–C09/C13–C16/C27/C31, T08/T10/T11/T19: adapter portions; full T08 needs G06. Fake process/provider injection plus actual launch/retry/neighbor resource checks. No unproven same-name resource reuse. |
| **P03-G03 — Origin, idle and finality** | Replay native fixtures and adversarial human-tag/foreign-turn/history-error cases through public use cases. Only matching native attempt/turn evidence acknowledges/completes; foreign fence persists after idle and only authorized fresh reconciliation clears it; unavailable history blocks send. | C09/C10/C13/C17/C31; T05/T06/T15/T16; L01/L02 portions. Fixtures cannot replace actual human T05/T15. Claude trusted-origin gap blocks its completion claim. |
| **P03-G04 — Linux/Codex preview** | From a disposable profile run W04 using the published .NET 11 build: explicit Herdr choice, MCP acceptance, abrupt MCP client/bridge kill mid-turn, new client result read, same-session follow-up and duplicate retry. Confirm durable result from SQLite via daemon API and independent kill scope; daemon restart must not duplicate uncertain work. | Codex-interactive subset of L01, C04/C05/C08–C10 and T04/T06/T07/T10/T19 only. **Not L03**: one backend and a test client do not prove both backends/real-lead/repetitions. Record JIT/AOT separately. Safety review, SDK or native binding failures block preview. |
| **P03-G05 — Three-backend execution in both modes** | Live bounded first job/final native correlated result/same-session follow-up for Claude Code, Codex and Pi, Herdr interactive and explicit headless; real lead uses MCP before and after lead+bridge death. Three successful L03 repetitions per Linux mode for its original Claude/Codex scope; separately repeat analogous Pi flows three times per mode, retaining results without old client memory. | Complete Linux L01/L02/L03 only when their original flows pass; Pi analogues are additional F19 evidence, not redefinitions of L cases. T04/T06/T07/T11/T19 Linux portions; C04/C05 plus P02 durability regressions. No approval bypass; Claude or Pi gaps cannot be excused by preview pass. |
| **P03-G06 — Approval, interrupt, stop and deadlines** | Exercise pending approval without decision maker, authorized/stale/duplicate decision, native targeted interrupt during active tool, separate owned stop and descendant inspection. Run deadline expiry with and without allowed escalation and across daemon downtime. No false running/completion/stop; no unrelated tab affected; unsupported capabilities explicitly block dependent work. | C21–C23; T08/T18; L04/L05 Linux complete only for both modes/backends. L05 permits explicit unsupported observation, which does not prove usable approval automation. Equivalent Pi control/approval cases are additional F19 gates. Full required native interrupt remains blocked for any backend lacking proof or approved baseline change. |
| **P03-G07 — Replay and recovery** | Kill daemon at barriers; finish interactive work during outage; restart, replay result twice, inject final-commit/spool failure and expired deadlines. Exactly one durable final event/result per run; no replay into newer generation, no blind prompt resend, no new writer until verified idle/termination. Insufficient evidence produces actionable quarantine. | C08–C10/C13–C17/C19/C22/C31; T09/T10/T17; L06 complete for both Linux modes/backends. Equivalent Pi recovery cases are mandatory F19 evidence. Real daemon/backend/provider loss plus fake disk/history corruption. Honest reconciliation is allowed by cases; cannot be advertised as zero-loss replay. |
| **P03-G08 — Bounded secure transports** | Flood/stall fake peers, slow/disconnect clients, send malformed/oversized/Unicode data, exhaust spool/DB and retry unauthorized operations. Assert declared time/byte/queue ceilings, continuing bounded drains, no false acceptance or secret exposure, no arbitrary mutating RPC. | C17–C20/C24/C25/C27/C29 with real SQLite/process/IPC; retain P02 C26/C30 regressions and only P02 foundations for C11/C12/C32 (stable events, consumer/generation and retention primitives). Complete teams/messages/public read-ack/wake semantics for C11/C12/C32 belong to P04, not a full P02 regression claim. Native live approval/result channel sampling complements fakes. Missing enforceable limits or hidden hook write failure blocks gate. |
| **P03-G09 — Actual platform and published binary matrix** | On Linux, Windows and selected macOS target, run all three backends' real interactive and headless first/follow-up/final native correlated result/approval/interrupt/stop/reconnect/recovery. Native Windows Pi must pass every section 3.5 wrapper, quoting, Unicode, environment, credential, private IPC and process/descendant ownership test, including visible selected terminal and explicit headless. Human exercises T05/T15; neighboring tabs survive. Run T04/T07/T10 in temporary intended production context. Build, publish and execute .NET 11 AOT MCP/IPC/SQLite/adapter paths; assess every trim warning. | T01–T12/T15–T19 per platform; L08–L12 with L09 both backends/platforms. Fake AOT smoke is only part of L08/L12. Pi cases are additional F19 evidence, not a relabeling of Claude/Codex L cases. Linux tests and Windows cross-builds cannot pass native Windows; Linux evidence cannot pass macOS. T13/T14 installed qualification belongs to P05; P03 context tests are partial evidence for those cases. Missing platform, credentials, SDK/native assets or mandatory Claude/Codex/Pi capability blocks full exit. |
| **P03-G10 — Full phase handoff** | Review complete manifest, operator acceptance, security findings and schema/API artifacts. All mandatory G01–G03/G05–G09 criteria met, no blocking review findings, accepted nonblocking limits explicitly recorded. G04 is labelled historical preview, not full exit evidence. | C coverage is cumulative with P02; C28 connector seam is not established by real adapters. L07 selected-host wake belongs to P04; additional Pi lead-host wake belongs to P06 and stays unverified here. C11/C12/C32 P02 foundations do not establish full P04 teams/messages/wake. P03 does not claim whole PoC/product qualification or P05 installed T13/T14. Independent review/re-review and artifact redaction required. |

Full gate retains Claude Code/Codex/Pi, interactive/headless and actual platform
evidence, including mandatory native Windows Pi.
Reporting “unsupported” is safe behavior, but does not satisfy a mandatory
capability. Removing one requires an explicit owner-approved scope decision and
updated waterfall baseline; it must not be recorded as a pass of the original
requirement. The phase author does not make that decision by writing this plan.

## 7. User/operator acceptance demonstration

Use public, disposable workspace content and approved tiny tasks. Provide tested
relative-path command recipes when implementation exists; none are asserted here.

1. Show saved explicit mode/provider and pinned capabilities. Missing provider,
   unavailable GUI or unsupported tuple produces wait/error without fallback.
2. For the preview, launch one real Codex TUI through the daemon/MCP path. Show
   acceptance before dispatch, native correlated result and same-session
   follow-up. Kill only the client/bridge; reconnect to read the persisted result.
   State prominently: Codex/Linux preview, not complete P03 or hands-free wake.
3. For full acceptance, repeat with Claude Code, Codex and Pi in both modes,
   then on the selected Windows/macOS platforms. Native Windows Pi additionally
   exercises every section 3.5 launcher/security/ownership scenario. A human types in each real TUI; automation
   pauses even after the human turn finishes. Reconcile verified idle explicitly.
4. Attempt forged tags, a concurrent send, stale binding/history and duplicate
   start. Show no false completion, duplicate prompt/tab or unsafe busy override.
5. Leave an approval pending with no lead. Show durable blocked/unsupported
   status and supported human/authorized decision path; stale decision is refused.
6. Interrupt a long turn while neighboring tabs remain; show the intended agent
   survives. Separately stop an owned agent and inspect tool children. Show both
   no-escalation and explicitly accepted headless deadline policies honestly.
7. Kill daemon separately. Let interactive work finish during outage, restart
   and show replay or actionable uncertainty. Do not auto-kill surviving TUIs.
   Try a new job before ownership reconciliation: it must remain blocked.
8. Close a test tab, lose provider/control transport and simulate storage failure.
   None becomes successful completion or an automatic rerun. Recover via the
   supported evidence-bound operation; cleanup touches only test-owned resources.

Operator acceptance records what was seen and any manual steps. Technical passes
cannot substitute for unrun human-input tests. Raw prompts, paths, transcripts,
credentials and process metadata need artifact review before publication.

## 8. Risks, decisions, stop/go and rollback

| Risk / pending decision | Owner and stop/go rule | Safe response or rollback |
| --- | --- | --- |
| Claude origin cannot be authenticated from inbox/hook data | P03 adapter investigation using P01 contract decisions. No safe completion claim until a native delivery-to-turn solution passes adversarial tests. | Disable automated dispatch for that tuple; keep observations informational. Request explicit baseline decision if no supported solution exists. |
| Claude lacks targeted native cancel with authoritative outcome | P03 must prove supported native alternative or obtain explicit product scope decision. Esc, signal success or agent kill cannot be relabelled turn cancel. | Keep capability blocked and full P03 gate open; no keystroke fallback. |
| Codex remote-TUI/native APIs or provider versions drift | Pin and qualify each tuple; verify `clientId`, finality/history and live TUI association. | Stop new dispatch on mismatch; retain old evidence and quarantine uncertain sessions. Never downgrade a live conversation by guessing IDs. |
| Pi native control, final correlation or Windows wrapper/TUI binding unproven | P01 proves feasibility; P03 W07 implements/tests both modes, including actual Windows. Current RPC is not assumed to control an existing TUI. | Block affected mandatory cell and full P03 exit; obtain public protocol proof or explicit scope decision. No attach-only, lead-host-only or P06 deferral substitute. |
| Physical ownership differs across OS/provider/service contexts | P01 probes fundamental feasibility; P03 verifies semantics; P05 qualifies production deployment. | Missing identity/desktop/platform access blocks gate. Preserve live interactive sessions and deny new writers; no PID-only cleanup. |
| Human input races machine claims or approvals | P03 detects and persists foreign activity; P04 later governs workspace/team policy. | Human-wins pause, new evidence revision and authorized verified-idle reconciliation; no claim that SQLite locks a keyboard. |
| Outage evidence/spool is incomplete or storage fails | P03 bounds and observes failures; P02 durable-write refusal remains authoritative. | Stop new effects, retain uncertainty and existing fences, recover authoritative evidence where possible. Never clear state or call missing DB empty. |
| Deadlines exceed enforceable backend control | Accepted policy must distinguish interrupt request from guaranteed termination and advisory spend. | Report blocked/unconfirmed; use only authorized, proven-owned headless escalation. Live interactive recovery stop needs human intent. |
| SDK/AOT package or native asset failure | P01 risk result plus P03 published runtime test; source signatures are insufficient. | Label preview JIT-only if allowed; full AOT gate stays blocked. Any production JIT fallback needs explicit decision, not silent substitution. |
| Unsafe repair promoted from changing spike snapshot | Bind review and evidence to exact files/binary. Current feasibility is not adapter approval. | Re-review affected safety paths; retain fake mode for development and stop real dispatch until promotion gate passes. |

Rollback disables new starts/sends for the affected tuple while retaining result
reads, diagnostics and evidence-bound recovery. Drain or explicitly stop only
proven-owned work under accepted policy; do not forcibly close live interactive
TUIs on upgrade/restart. Preserve DB, version pins and evidence; use a compatible
binary/state recovery procedure rather than opening newer schema with an older
binary. Never delete uncertain rows/resources to make tests pass. Existing user
workflows stay available; switching back is explicit, not silent headless fallback.

## 9. Relative effort, sequencing and definition of done

| Work | Relative effort | Confidence and external wait |
| --- | --- | --- |
| W01 admission and contract freeze | M | Medium; depends on P01/P02 acceptance and reviewed repair snapshot. |
| W02–W03 safe Codex interactive vertical slices | L | Medium-low; native feasibility exists, ownership/races need real integration. |
| W04 preview | S–M incremental | Medium after prior gates; SDK availability, approved model budget and review can dominate elapsed time. |
| W05 Codex headless/control | M–L | Medium-low; approval routing and running-tool cleanup require live proof. |
| W06 Claude both-mode semantics | L | Low; protocol/vendor capability or product decision may block indefinitely. |
| W07 Pi both-mode/native Windows semantics | L | Low; public native control, GUI access, wrapper security and descendant ownership need independent proof. |
| W08 crash/replay/deadline recovery | L | Medium-low; backend evidence fidelity and physical survivors are main uncertainty. |
| W09 cross-platform/published qualification | L | Low until platform/GUI access, selected providers and .NET 11 native assets are available. |

Estimates are relative engineering size, not a calendar promise. One isolated
runner/app-server per agent is acceptable; multiplexing optimization is excluded.
Preview keeps one agent and harmless work. Full Linux L03 retains its original
Claude/Codex scope and three successful repetitions per mode; P03 adds three
analogous Pi repetitions per mode without redefining historical cases. External waits include independent
reviewers, OS/desktop access, credentials, approved spend, SDK/package provisioning
and possibly upstream contract clarification. The next waterfall phase starts
only after P03 exit or an explicit reviewed baseline revision, not after W04.

### Exit handoff checklist

- [ ] P01/P02 entry artifacts and independent P03 plan review accepted.
- [ ] W01–W09 delivered as vertical slices; promoted code follows three-project
      architecture without demanding refactoring of the isolated old spike.
- [ ] G01–G10 ledger distinguishes fake, live, manual, JIT, AOT and actual platform
      evidence; partial C/T/L mappings are not reported as complete cases.
- [ ] Claude native origin and targeted cancel resolved with real proof, or an
      explicit owner-approved baseline change records the unsatisfied original
      requirements. No implied waiver from a Codex-only demonstration.
- [ ] Managed Pi delivered in P03, with native Windows spawn/follow-up/final
      correlated result/interrupt/stop/reconnect and section 3.5 evidence in both
      modes. Neither attachment nor lead-host wake nor cross-build passes F19.
- [ ] All required modes/providers have tested binding, approval, foreign-input,
      interrupt/stop, boundedness and outage/reconciliation semantics.
- [ ] Public API/schema/capability versions, compatibility/retention rules,
      operator recipes, diagnostics and rollback artifacts handed to P04/P05.
- [ ] Format, lint/check, Release build/analyzers, regression/crash tests and
      relevant published-binary/AOT tests run on the implementation revision.
      Missing tooling is added during implementation setup, not presumed present.
- [ ] Separate opposite-family code review and fix re-review complete; blocking
      findings closed, accepted nonblocking limitations and follow-ups recorded.
- [ ] Sanitized evidence reviewed for publication; no secrets, private repository
      dependencies or machine-specific personal paths in public documentation.

For this planning-only change, validation is limited to bounded document checks
(UTF-8/newline/whitespace, fences, local links, package/gate IDs and referenced
C/T/L definitions). It establishes no build, runtime, adapter, model or platform
pass. Independent plan review remains a separate gate before implementation.
