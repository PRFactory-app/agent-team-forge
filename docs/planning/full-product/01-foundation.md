# P01 — Full-product foundation and scope freeze

## 1. Objective, authority, and evidence

**Status: draft plan; not approved, implemented, or runtime-qualified.** P01
freezes a testable full-product baseline and resolves architectural feasibility
before formal P02 implementation begins. Writing this document does not pass P01. Future probes below
require separate execution authorization, platform access, and model budget.
No installation, live test, agent delegation, or Git operation is authorized here.

The [shared brief](README.md) owns scope and ordered phases. It supersedes the
older roadmap's treatment of Pi, supported attachment, nested teams, and the
optional connector as merely future candidates: these are required parts of the
full-product planning baseline. Their use remains optional where stated below.
[Cross-phase contracts](contracts.md) own synthesis decisions; the parent owns
roadmap synthesis and all other phase plans. P01 does not replace
them or the separately scoped [Linux demo](../../spikes/e2e-demo-plan.md).

Planning assignment requests GPT-6 Astra at medium effort and a separate author
per phase. This document makes no assertion about unverified runtime model
identity and delegates no agents. Independent review remains pending.

Binding inputs: [contributor policy](../../../AGENTS.md),
[architecture](../../architecture.md), [delivery and recovery plan](../../plan.md),
[PoC cases](../../poc.md), [terminal contract](../../terminal-modes.md),
[SDK research](../../research/net11-process-api.md), and
[handoff](../../../HANDOFF.md).

### Existing evidence, with limits

The [interactive report](../../../spikes/m0-interactive/REPORT.md) describes
Linux x64, .NET SDK 10.0.401/runtime 10.0.12, Herdr 0.8.2, Claude Code 2.1.283,
and Codex 0.157.1. Real TUIs and selected follow-ups/controller-death recovery
were observed. These are candidate versions, not a product compatibility list.
The [independent review](../../../spikes/m0-interactive/CODE-REVIEW.md) rejects
promotion as a safe adapter: eight blocking findings remain. Its 49 passing
tests do not resolve those findings. Claude turn cancellation and trusted
message origin remain unresolved; human input, actual MCP lead crash, durable
SQLite, .NET 11, AOT, service contexts, and Windows/macOS are not established.
Raw spike artifacts require separate publication review; summaries are not
substitutes for reproducible, sanitized evidence.

## 2. Entry criteria and freeze boundary

P01 has no predecessor phase. Entry requires the shared brief, source contracts,
and explicit recognition of existing failures, rather than permission to reuse
unsafe spike behavior. Documentary planning can proceed now. Probe execution is
blocked until the owner authorizes disposable environments, exact test budget,
SDK/package provisioning, platform access, and independent reviewers.

Two distinct outputs prevent paper approval:

- **Requirements baseline:** the MUST requirements below and named candidate
  support cells. Owner acceptance and independent plan review are still needed.
- **Feasibility freeze:** exact version/provider decisions backed by P01-G02–G06.
  Unavailable platforms or unsafe mandatory capabilities block this freeze and
  formal P02 entry. A separately authorized, reviewed Linux-specific contract
  may permit isolated durable-core/adapter experiments before full gates pass;
  no phase is completed by that evidence. Promotion requires full review and
  gates; see [contracts section 6](contracts.md#6-waterfall-path-versus-early-experimental-preview).

### Full-product requirements and traceability seed

[Product scope](../../product-scope.md#2-product-feature-inventory) owns canonical
F01–F27. R01–R16 below are phase-local groupings, not competing scope IDs; use
[the synthesis crosswalk](contracts.md#1-scope-ids-and-phase-ownership).
Every requirement is mandatory unless explicitly an option. Preserve canonical
identity and disposition in downstream evidence.

| ID | Frozen requirement and observable acceptance | Delivery owner / evidence obligation |
| --- | --- | --- |
| R01 | Durable multi-team jobs survive client loss; acceptance and unattempted intent commit together; no blind replay. | P02, P04; C01–C10, C13, C19, C26, C30–C32. |
| R02 | Managed Claude, Codex and Pi support interactive and explicit headless execution on all three baseline platforms; real control, result, interrupt, separate stop, approval behavior. | P03, P05; L01–L06, L08–L12 and applicable T01–T19 per cell. |
| R03 | Authenticated MCP/CLI control, scoped capabilities, private IPC, version refusal, bounded input; bridge never opens job DB. | P02, P04; C24–C27, C29 and role-isolation tests. |
| R04 | Multiple teams, parent/child/nested delegation, cycle rejection, bounded depth/concurrency, attenuated permissions and budgets. | P04; cross-team denial, cycle, depth, aggregate-budget and parent-loss tests. |
| R05 | Durable messages/events and explicit read/ack, fenced durable consumer principals, deduplication; informational messages never execute jobs. | P04; C11–C12, C32 plus canonical receipt/cursor and read-only filtered-inspection tests. |
| R06 | Approval waits persist; no absent-decision-maker bypass; interrupt and stop remain distinct; runtime/cost limits declare enforceability. | P03, P04; C21–C23, L04–L05, T08, T18. |
| R07 | Native session wake is standard by default for every promised host/platform cell, with authenticated notice-only delivery, bounded retries and unread catch-up. No watcher, keystroke injection or tight model polling on the normal path. | P01 feasibility; P04 Claude/Codex and P06 Pi lead/attachment integration; L07 plus independent host/platform security, duplicate/burst/offline tests. Manual catch-up is explicit degraded recovery, never a required native-wake gate pass. |
| R08 | Isolated workspaces and bounded artifacts/logs with authorized path access, retention and safe cleanup; no second physical writer after recovery. | P04, P07; C14–C20, path/symlink and cleanup tests. |
| R09 | Per-user install, explicit setup choice, doctor, update, rollback and uninstall on Linux/Windows/macOS; preserve user configuration/data unless separately authorized. | P05, P08; clean-profile lifecycle, T01–T03, T12–T14. |
| R10 | Local SQLite backup/restore, migrations, schema refusal, recovery, bounded resources, redacted diagnostics and documented power-loss limits. | P02, P07, P08; C19, C20, C30, restored-WAL/migration/fault tests. |
| R11 | Managed/spawned Pi is mandatory alongside Claude/Codex, through pinned native control surfaces in interactive and explicitly selected headless modes; no custom model loop or attach-only substitute. | P03; P05 installed contexts. Real native Windows launch, same-session follow-up/results, interrupt, separate stop and reconnect required; Linux/cross-build evidence cannot pass Windows gates. P06 adds Pi lead-host/attach/integration proof only. |
| R12 | Supported attachment/join is opt-in, authenticated and scoped, limited to pinned cooperative host/provider combinations; no arbitrary process adoption. | P06; ticket replay, identity substitution, disconnect, foreign input and capability tests. |
| R13 | Optional generic external connector uses same local policy/API, durable external IDs, explicit lease/offline ownership and export consent. Local use makes no connector contact. | P06, P07; C28 plus lease/revoke/outage/duplicate/export-denial tests. |
| R14 | .NET 11, exactly Host → Business → DAL, feature-first; published JIT/AOT evidence, measured resource limits and reproducible dependency inventory. | P01–P08; package proof, dependency check, published smoke and PoC measurement workloads. |
| R15 | Public documentation, distribution, compatibility/security lifecycle, notices and operator recovery runbooks; no unlicensed publication. | P08; release checklist, clean-user walkthrough, owner license/publication decisions. |
| R16 | Small authenticated local text-only web console for agent status/output, human follow-up and confirmed agent stop; no terminal emulation or additional production project. | F27; plan/static HTML first, P04 shared projections/control, P05 Host web role, P07 browser/security/platform evidence, P08 public usage. |

Traceability record format: canonical F ID → local R grouping → capability cell → work package →
case/command → environment and artifact hash → result (`planned`, `blocked`,
`failed`, `passed`, or justified `not_applicable`) → reviewer → remaining owner.
Each downstream phase extends this ledger. Partial C/L/T mappings above are
not full coverage. P07 must account for every C01–C32, L01–L12 and T01–T19 and
new full-product cases; omissions cannot become implicit passes.

### Support matrix to freeze

These are **required target cells, all unqualified today**. Native builds/runs
must use exact OS build, RID, CLI, terminal, protocol and host versions captured
in a compatibility manifest. P01 selects one tested version per component;
unknown versions fail capability checks rather than inherit support.

| Baseline platform | Interactive provider and production-context candidate | Managed backends / headless | P01 exit requirement |
| --- | --- | --- | --- |
| Linux x64 | Herdr; per-user systemd daemon outside client/terminal kill scope; desktop launcher if needed. | Claude, Codex, Pi; explicit headless alternative for each. | Real GUI/context probes, UDS, SQLite/native AOT; document logout/linger behavior. |
| Windows x64 | Windows Terminal; per-user logon task candidate, restricted GUI-session launcher if needed. Not LocalSystem or a Session 0 GUI. | Same three backends and two modes. | Real Windows desktop, named-pipe ACL, Job Objects/descendants, `.cmd`/Unicode, locked/logged-out behavior. |
| macOS arm64 | Terminal.app first probe; iTerm2 only by recorded provider decision. Per-user LaunchAgent and GUI-session boundary. | Same three backends and two modes. | Real Apple Silicon desktop, UDS, TCC/Apple Events, per-tab identity/targeted stop, native assets/AOT. |

Pi managed execution is delivered in P03 alongside Claude/Codex. P01 must
establish an official native control surface and a bounded smoke on each
platform/mode before accepting this matrix. Native Windows probes must cover
launch, same-session follow-up/results, interrupt, separate stop and reconnect,
with a real visible selected terminal tab for interactive mode and explicit
headless choice. Record command-wrapper, argument/Unicode, process/handle,
credential/profile and terminal-binding behavior. Linux runs and Windows
cross-builds cannot establish Windows support.
If a candidate cannot support required semantics, freeze is blocked pending a
safe alternative or an explicit owner scope change. Additional architectures,
terminal providers, and OS versions are options, not automatically supported.
Exact OS versions and macOS/provider selection are decided in D01, not invented
from the existing Linux report.

Host integration is a separate axis. Freeze the required Claude/Codex/Pi
lead-host × Linux/Windows/macOS × launch-context matrix in P01, including
cooperative attachment cells. Each promised cell needs native idle-wake feasibility
and its own downstream qualification; MCP connection or managed execution proves
neither. One host is an early checkpoint only, not sufficient for full release.
Missing native transport or platform access blocks the required gate until resolved
or changed by explicit owner scope decision. Manual read/catch-up is explicit
degraded recovery, never a silent substitute. P04 implements Claude/Codex native
integration; P06 adds Pi lead-host and attachment wake. P03 retains managed Pi,
including mandatory native Windows execution.

Use the public [PR #70 design](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70)
and [native-wake research](../../research/native-wake-pr70.md) as reference, not
source-copy/license approval or evidence of our tests. Confirmed public snapshot:
merged to main at `471a17514d041e09b69cb24b910e418da28d2027`; inspected head
`6da4e041f95e62e4c5f3a9ddf8f043c0fe7d769e`. Unlike its opt-in
rollout, native wake is our default standard. Linux Claude uses an owning-session
inbox socket through its own child, with nearest-host validation and inherited
wake environment scrubbed on spawn/resume. One per-reader OS lock fences notifier
ownership. Codex authenticates thread/home registration bound to generation, then
uses bounded `codex queue` with scrubbed environment, trusted working directory
and read-only thread lookup. Persist messages before wake; notice-only payloads
carry no message body or credentials. Coalesce, back off within finite retry/budget
limits and catch up unread data; successful wake is neither ack nor model action,
and failed wake does not fail a committed send.

Reference Windows W1–W3 are passed: native Codex wake works on Windows as
well as Linux. Reuse that feasibility evidence, then test our .NET integration.
Probe remaining gaps early, not at P06:
Claude Windows is unsupported there pending cancellable named pipes; macOS reader
and Pi wake are not covered. These are blockers to investigate, not platform
waivers. Record native Windows/Pi and each required host/platform probe separately;
upstream reported tests, Linux runs and cross-builds cannot pass our gates.

Attachment baseline: explicit join by a cooperative session using a supported
host-side handshake (initial Pi candidate), plus rejoin of managed sessions with
verified provenance. P01 must select at least one independently launched join
combination and prove identity/control feasibility. Join tickets are one-use,
short-lived, principal/session-bound and authority-attenuating; revoke fences
future commands. Existing sessions cannot acquire daemon-owned survival by
joining. Claude/Codex/Desktop attachment without verified cooperative identity
is unsupported, not simulated by PID lookup or screen scraping.

Connector baseline: a versioned generic contract and conformance peer, followed
by P06 opt-in implementation. Unknown third-party servers are unsupported.
No named proprietary connector or replacement worker is required. Default is
disabled, outbound-only when enabled, no remote administrative listener.

### Explicit exclusions and options

No rich analytics/remote-admin UI beyond the scoped F27 console, custom agent
model loop, mandatory cloud/telemetry, billing,
distributed scheduler, broad legacy API compatibility, arbitrary Desktop/process
attachment, exactly-once external effects, seamless process/stdio adoption after
daemon death, or replacing another worker. Extra vendors, architectures,
providers, multiplexing and stronger survival isolation require separate change
requests. These are not substitutes for mandatory full-product work.

## 3. Architecture, delivery, and threat contracts

Exactly three production projects, feature-first: **Host → Business → DAL**.
Host owns CLI/setup, protocol identity, IPC/MCP and lifecycle composition;
Business owns authorization, feature policy, terminals/backends and orchestration;
DAL owns SQL, records, migrations and atomic operations. Host may reference DAL
for composition only. Business calls DAL directly. No repository-port inversion,
extra production assemblies, generic mediator chain, or global type folders.
Temporary C# probes and test projects are not production layers.

### Durable and physical ownership contract

1. One daemon per user/machine, exclusive OS lock, local-disk SQLite only.
   WAL, foreign keys, short transactions, bounded busy handling and preliminary
   `synchronous=FULL`; never hold a transaction during backend I/O.
2. Accept atomically stores job, principal/team/operation-scoped idempotency
   fingerprint and unattempted intent. Respond only after commit. Changed
   semantic payload with same key conflicts. Retain tombstones or explicitly
   reject expired retries; retention cannot silently create new work.
3. Atomic compare-and-claim covers job and conversation dispatch fence. Persist
   generation/correlation before **every** spawn, tab creation or delivery.
   Only never-attempted work dispatches automatically; lost acknowledgment is
   uncertainty, not evidence of nondelivery.
4. Keep `accepted`, `dispatching`, `backend_acknowledged`, `running`, terminal
   outcomes and `needs_reconciliation` distinct. Completion needs authoritative
   lifecycle/backend records with trusted origin and bound attempt/turn IDs.
   Text tags, model reports, silence and terminal status are never sufficient.
5. Final state/result/event commit together under expected-generation checks.
   Foreign/human activity persists `foreign_busy`; only authorized, evidence-bound
   verified-idle reconciliation releases it. No generic busy override.
6. Client loss cannot cancel accepted work. Daemon restart reconciles evidence;
   backend/machine failure can leave uncertainty. Never auto-kill a live TUI.
   New work requires physical idle/termination proof, not only a DB lock release.
   Verify provider, backend/session, PID plus start identity and generation;
   no PID-only adoption or whole-window teardown by reusable name.
7. Turn interrupt preserves interactive session by default; agent stop is
   separately authorized. An unknown interrupt is blocked/unconfirmed. Esc,
   process exit or a request's return code cannot prove targeted native cancel.
   Approved headless termination policy includes descendants; interactive hard
   escalation requires explicit acceptance, never automatic crash recovery.
8. Cost/token limits are advisory unless telemetry and enforcement prove a hard
   bound. Queue, concurrency, byte and retention limits are enforceable local
   policy. Accepted jobs snapshot permissions/budgets; reconnect cannot expand them.

Version IPC, capabilities, configuration, schema and connector contract separately.
Reject unsupported major/schema versions without mutation; explicitly negotiate
minor capabilities. Migrations require backup and restore rehearsal. Restoring a
DB cannot erase surviving process authority: fence and reconcile physical work
before allowing dispatch. Helper spools are bounded/private/replay-protected and
never a second scheduler. Losing spool writes means uncertainty, not success.

### Threat model and verification targets

| Boundary / threat | Required control and negative evidence |
| --- | --- |
| Untrusted client or child claims another team/role | OS-private UDS/named-pipe ACL plus session-bound capability; Business authorization on every operation; C24–C25, stale-generation rejection. |
| Bootstrap theft/replay, inherited credentials | Private single-use expiring nonce exchange; explicit launcher environment allowlist; no secrets in argv/logs/export; reuse and cross-session denial, C29. |
| Malicious prompt, repository hook/plugin or forged completion | Repository configuration is not trusted automatically; preserve backend approval mechanisms; native provenance and attempt binding; forged tag/conflicting ID tests. |
| Stale terminal/socket/PID or old remote owner | Revalidate binding before effects, physical ownership fence, remote generation checks; stale-pane/socket, PID-reuse and lease-loss tests. |
| Flooding/stalled IPC, output or hooks; disk failure | Bounded frame/prompt/queue/log/spool sizes, deadlines and backpressure; no false acceptance/idle; C18–C20 and stalled/flooding peer tests. |
| Artifact traversal/symlink, remote export or destructive cleanup | Workspace-root authorization, race-aware path handling, allowlisted export and explicit consent; no arbitrary server paths; prove unrelated resources unchanged. |
| Update/backup tampering and incompatible rollback | Verified release provenance, private backups, schema compatibility checks and restore rehearsal; reject tampered/incompatible inputs without dispatch. |

Assets include credentials, capability tokens, source/workspaces, results and
execution authority. Trust boundaries include host→bridge→daemon, daemon→backend,
desktop launcher, filesystem/spool and optional remote peer. Same-user agents
with unrestricted filesystem/process access are **not sandboxed** by capabilities.
No defense against administrator/kernel compromise is claimed; retain backend
sandboxing and document residual same-user risk. Unsupported security-critical
controls block the corresponding cell rather than silently weakening policy.

## 4. Ordered vertical work packages

These describe future P01 execution, not work performed by this author. Each
package produces an observable decision and verification artifact, not broad
product implementation. Use isolated C#/.NET harnesses; no production layer-first
buildout. Relative sizes include evidence and review, not external waiting time.

| Package | Result, deliverables and meaningful tests | Host / Business / DAL touchpoints | Dependency; size/confidence |
| --- | --- | --- | --- |
| P01-W01 | Baseline F01–F27 mapped through local R01–R16, candidate cells, decision owners, threat model and case ledger accepted for investigation. Inventory evidence without promoting historical passes. Independent major-plan review precedes probe implementation. | Host public claims; Business policies; DAL invariants. | Shared brief; M/high. |
| P01-W02 | Fix or replace unsafe spike paths in bounded harnesses; publish eight-finding disposition and Claude cancel/origin ADR. Red-first regressions in §5; opposite-family review and re-review. Demonstrate exact supported delivery/result/cancel contract. | Host diagnostics/bootstrap; Business adapters/ownership; DAL atomic claim contract represented in harness. | W01; L/low. |
| P01-W03 | Pin public .NET 11 SDK candidate `11.0.100-rc.1.26425.128`, actual ref pack and restored package/native-asset versions. Minimal MCP→IPC→Business→SQLite slice publishes/runs JIT and AOT on all three platforms; bounded process and crash proof. Save warnings, package locks, binary hashes and reproducible commands. | Host role registration/JSON/MCP; Business process policy; DAL SQLite transaction/native assets. | W02; L/medium-low. |
| P01-W04 | Select platform/provider/service contexts early. Real Claude/Codex first/follow-up/result/interrupt plus T04/T07/T10 in temporary production-style contexts; probe T13/T14, human activity, targeted neighboring-tab safety, credentials and logout. Record OS/RID/provider matrix and launcher decision. | Host user-session/service boundary and doctor; Business terminal bindings; DAL persisted ownership/config contract. | W03; L/low, real machine access. |
| P01-W05 | Pin Pi surfaces and one supported independent join path; prove native wake feasibility across the required host/platform matrix, prioritizing Windows transports and Pi gaps identified in W01. Test authenticated registrations, nearest-host ownership, environment scrubbing, per-reader OS locks, notice-only bounded retries and unread catch-up. Bounded real probes; generic connector contract with deterministic conformance-peer lease/duplicate/export tests, no real remote deployment. Freeze nested authority/budget and resource envelope decisions. | Host host-bridge/join/connector wire contract; Business capabilities/leases/attenuation; DAL cursor/external-ID/schema extension contract. | W04; L/low, provider cooperation. |
| P01-W06 | Consolidated reproducible evidence, owner decisions and independent foundation review; freeze support/version list and hand off requirements with no hidden exceptions. Recheck links/format and ledger completeness. | Host/Business/DAL dependency and responsibility check; no extra project. | W05 and all gates; M/medium. |

W03 package proof must invoke published MCP initialize, tools/list and tools/call,
source-generated serialization, role-isolated bridge startup, SQLite commit and
reopen/recovery, native library loading, private IPC and actual fake-child crash.
Pin official MCP C# SDK, hosting, `Microsoft.Data.Sqlite`, SQLite native bundle and
AOT compiler versions after restore; research does not supply tested pins.
Capture and analyze trim/AOT warnings; suppress only narrowly with a runtime test.
Compile used process signatures against the installed ref pack, not article
examples with different build metadata. `StartAndForget`/lookup is not ownership;
Windows signaling is not Unix SIGINT; macOS lacks `KillOnParentExit`; bounded
stream draining is required. No silent JIT or .NET 10 fallback.

W04 happens in P01, not P05. Test GUI absence, locked session, logout, profile,
PATH, credential store and CLI authentication under actual launch context. Session
helpers may use restricted Host roles but cannot acquire durable scheduling or
unrestricted operator authority. Temporary service configuration is enough for
feasibility; installer qualification remains P05.

## 5. Mandatory spike dispositions and stop/go decisions

All eight review findings remain **open/no-go for promotion** until regressions,
actual fix review and relevant real-platform rechecks pass. Rewriting instead of
copying code does not remove the regression obligation.

| Finding | Required closure evidence / no-go condition |
| --- | --- |
| B01 concurrent sends | Two-process same-job and different-job/same-session barriers prove atomic claim, one attempt and conversation fence. Any duplicate external write blocks. |
| B02 forged origin | Codex requires native client ID plus bound attempt/turn and rejects conflicts, no text fallback. Claude needs trusted delivery-to-hook provenance; forged tag before send/during uncertain attempt stays foreign. Unprovable origin blocks automated Claude completion. |
| B03 reusable teardown name | Unique proven provider/server ownership; fake colliding/pre-existing session is never stopped/deleted. Unproven ownership blocks teardown. |
| B04 stale Codex binding | Validate server process/start identity, socket, thread and live TUI association before send/interrupt; stale/replaced pane/socket fails closed. Warning-and-send is forbidden. |
| B05 missing human-wins fence | Persist completed and active foreign activity; subsequent sends blocked until authorized verified-idle reconciliation. Remove unsafe generic busy override. Real T05/T15 required. |
| B06 history failure means empty | Only pinned pre-first-turn codes in proven empty context may mean no history; materialized-thread errors block sending. Error/lagging-idle fake test required. |
| B07 arbitrary diagnostic RPC | Read-only method allowlist or isolated disposable unsafe probe; production normal sessions reject mutating diagnostic calls before effects. |
| B08 unbounded/lost replay | Input/spool/history/channel/pending-request bounds, deadlines, write-failure visibility and replay protection. Full disk, failed append, flood/stall and outage completion yield verified replay or explicit uncertainty, never false success. |

Also close product-blocking gaps from review observations: private state/config
permissions, environment allowlist rather than denylist, pre-launch attempt intent,
Codex persisted approval waits with no lead, running-tool descendant cleanup,
and separately authorized stop. Tests must cover these before adapter promotion.
Process-kill evidence does not prove hardware power-loss durability. Publish only
sanitized artifacts after content and metadata review.

**Claude decision:** retain mandatory native targeted turn interruption and trusted
origin requirements. Investigate documented CLI/native paths first; a bounded
official SDK adapter is permissible only after explicit dependency/AOT/security
assessment. No private protocol clone, permission bypass, tag-as-origin, Esc-as-native
cancel or whole-agent-kill-as-turn-interrupt. If either gap remains after one
bounded investigation and one reviewed alternative, P01-G03 fails. Owner must
choose a supported alternative, explicitly revise full-product scope, or stop.
“Unsupported” is honest diagnostics but does not satisfy required Claude support.

Codex candidate remote-TUI/app-server path needs pinned fixtures for terminal
status only with `completedAt`, pre-first-turn history errors and lagging idle.
Herdr metadata remains advisory. Existing observations guide tests, not exemptions.

## 6. P01 gate table

All gates currently **planned/blocked, not passed**. Evidence must bind source or
manifest hash, exact command, test ID, expected/observed result, versions, platform,
mode/context, binary hash and sanitized logs. Commands/scripts below are required
future deliverables, not commands claimed to exist.

| Gate | Reproducible verification method | Exact success criterion / evidence class | Blocking condition |
| --- | --- | --- | --- |
| P01-G01 | Review canonical F01–F27 through local R01–R16, threats, matrix and decisions against shared brief; complete case ledger and independent major-plan review. | Every MUST has delivery owner, cell and test; scope decisions explicitly recorded by owner. Documentary only. | Missing mandatory feature, unowned requirement, absent review or owner decision. |
| P01-G02 | Restore pinned SDK/packages; compile ref-pack API probes; publish and run W03 on Linux x64, Windows x64, macOS arm64. Invoke MCP/SQLite/IPC/JSON and fake child paths; compare JIT/AOT. | All three native environments pass; exact native assets/package graph recorded; zero unanalyzed trim/AOT warnings; bridge does not open DB. Real OS plus fake backend, not interactive proof. | Missing environment/toolchain, incompatible API/package/native asset, unreviewed suppression or AOT fallback. |
| P01-G03 | W02 regression suite and opposite-family fix re-review; bounded real Claude/Codex controls on selected pinned versions. | B01–B08 and observation gaps closed; trusted origin and verified native interrupt with preserved TUI; C08/C09, C13–C16, C21–C25, T08/T15/T16/T17/T18/T19 mapped explicitly. | Any blocking finding, ambiguous Claude origin/cancel, unauthorized bypass, unverified stop/approval. |
| P01-G04 | W04 per-platform real T04/T07/T10 under temporary production context; T05/T08/T11/T13/T14/T15 plus headless first-result-interrupt/stop smoke. | Both Claude/Codex TUIs usable by human, correct follow-up/result/binding, no client-owned lifetime or neighbor damage; GUI absence fails visibly; both modes verified. Real GUI/service evidence; L08–L12 feasibility subset only. | Missing machine/human, Session 0/GUI failure, wrong credentials, silent fallback, unowned survivor. |
| P01-G05 | W05 real Pi mode/platform smoke including native Windows launch, same-session follow-up/results, interrupt, stop and reconnect; independent join prototype; required host/platform native idle-wake, authentication/ownership, stale-generation, scrub/lock, disconnect/burst/retry/catch-up probes; deterministic connector peer. | Pi has feasible safe contract in required cells; one pinned independent join and every promised native-wake cell have feasibility evidence, including native Windows and Pi lead-host paths. Committed send survives failed wake; notices contain no bodies/secrets and never ack. Forged/reused tickets rejected; connector off has no contact, stale lease/export denied. | Missing required transport/platform evidence, including Windows/Pi; Linux/cross-build or upstream-test substitution; one-host checkpoint or manual recovery counted as full pass; wake inferred from MCP notification; remote ownership ambiguity. |
| P01-G06 | Run bounded ownership/resource/failure probes with abrupt barriers; inspect state/effect records and threat negatives. Establish tested defaults and maximum envelope. | C01–C10, C14–C20, C24–C27, C29–C30 critical foundation paths pass in real SQLite/process harness; no secret leak or false acceptance; exact limits and measurement manifest approved. Fakes prove faults, not backend support. | Unbounded input/output/queue, double writer, lost durable acceptance, limits unspecified, unsafe rollback. |
| P01-G07 | Independent foundation review and traceability/handoff audit after fixes; owner freeze record. | G01–G06 pass, zero open blocking findings, accepted nonblocking follow-ups have owner/gate; public artifacts reproducible and sanitized. | Any unavailable reviewer/platform, outstanding mandatory capability or claimed approval without record. |

P01 probes are deliberately smaller than complete product qualification. They
retire architecture risks, not entire C/L/T matrices. P03/P05 must rerun against
product code; L03's real-lead three repetitions per Linux mode and full terminal
coverage remain owed. P07 is cumulative qualification, never first security test.

## 7. User/operator acceptance of the foundation

Future P01 acceptance is a witnessed evidence walkthrough plus bounded probes:

1. Show full requirements/support matrix, optional connector off, and explicit
   interactive/headless selection. Missing provider or GUI returns error/wait.
2. On each real platform show managed Claude/Codex TUIs and human input, trusted
   follow-up/result, then interrupt preserving session and separately authorized
   stop without changing neighboring tabs. Show Pi feasibility and joined-session
   limits separately, not as managed-session survival evidence.
3. Kill client/bridge; prove work is not in its kill scope and retrieve result
   through a new authenticated client. Crash daemon separately: uncertain work
   blocks replay and live TUIs are not auto-killed. Show spoofed tag/history error
   and completed human turn cannot authorize false completion or next dispatch.
4. Show approval with decision maker absent, failed spool/disk write, revoked join
   ticket and stale remote generation. No hidden bypass, duplicate or export.
5. Show published .NET 11 AOT MCP/SQLite proof and actual service-session evidence;
   compare what is passed with remaining downstream qualification obligations.
6. Demonstrate native idle-wake feasibility for each required host/platform cell,
   including Windows/Pi, separately from managed execution. Reject stale/wrong-host
   registrations; show bounded notices, failed wake after committed send and unread
   catch-up. Manual recovery is explicitly degraded, not a full-release waiver.

Owner accepts scope/limits and records go/no-go only after evidence and independent
review. This plan is not that acceptance. A Linux/Codex demo alone cannot pass P01.

## 8. Actionable decisions, risks and safe rollback

### Bounded owner decision log

| ID / deadline | Recommendation and required owner action | If unresolved or rejected |
| --- | --- | --- |
| D01, before W03/W04 | Authorize provisioned Linux x64/Windows x64/macOS arm64 test access, exact OS builds, temporary per-user launch contexts and capped model budget. Select tested macOS provider after Terminal.app probe; iTerm2 is one alternative. | No platform freeze; no support inference or silent architecture/provider change. |
| D02, W02 exit | Keep Claude native interrupt/trusted-origin requirements. Fund one bounded native investigation and one reviewed official alternative. | Stop full baseline if both fail; owner may explicitly change scope with downstream impact, never waive safety. |
| D03, before W05 exit | Confirm Pi managed two-mode target, one pinned independent join host (Pi first candidate), complete required native-wake host/platform matrix and Windows/Pi gap dispositions, generic-only connector. | Unsupported specific combinations remain blocked; no arbitrary Desktop/third-party claim. |
| D04, W05 exit | Approve measured resource profile: proposed starter limits 2 active turns globally, finite team/root/backend limits within ancestor/global allocation, root depth 0 and maximum 3 delegation edges, 1,000 queued jobs, 256 KiB prompt, 1 MiB IPC frame, 64 MiB spool per agent, 1 GiB retained logs globally. Set explicit timeouts, retry-key/tombstone retention and artifact quotas from probes. Preserve PoC performance targets as initial measurement targets. | No freeze with undefined bounds; changing failed targets needs visible rationale and rerun, not post-hoc pass. |
| D05, W06 exit | Accept scope/evidence and reviewer dispositions; record full-product go/no-go. Reserve license, signing/distribution identity and publication decisions for P08 owner approval. | No P02 implementation approval; no publication or license selected by agent. |

D04 figures are proposed test inputs, not claims of measured capacity. Queue,
artifact, job time and spool exhaustion must fail/backpressure honestly; cost
ceilings remain advisory where backend telemetry is insufficient. Exact tested
defaults, maxima and retention/retry relationship must replace proposals at freeze.

### Risk and stop rules

Highest risks: unsupported Claude native/origin contracts; Windows desktop/service
separation; macOS provider/TCC identity; .NET RC/package/native-AOT compatibility;
Pi cooperative control and native host wake; unproven Windows Claude named-pipe
cancellation, our Windows Codex integration (reference W1–W3 passed), macOS reader
and Pi transports; stale
physical ownership; disk/spool limits. PR #70 is a design reference, not licensing
approval or transferred test evidence.
Platform access, SDK provisioning, upstream documentation/fixes and reviewer
availability are external waits, not hidden effort reductions.

No-go means pause dependent implementation. Safe adapter replacement requires the
same tests and independent review. Scope reduction needs owner-approved change
control and must retain an honest requirement gap; it cannot retrospectively pass
an unchanged gate. Missing platforms may permit separate Linux research/preview,
not full-product phase completion. JIT fallback or side-runtime dependency requires
an explicit architecture/product decision, never automatic downgrade.

Probe rollback uses disposable state/workspaces and proven-owned processes only;
never operate on unrelated tabs or existing user sessions. Restore temporary
service/config changes only with recorded provenance and authorization. Preserve
failed evidence, quarantine unsafe adapters, and keep existing user workflow
available. Do not restore a snapshot and immediately dispatch while an old runner
might still write. Unknown ownership stays blocked for operator reconciliation.

### Change control

Every change records requirement/cell IDs, reason, old/new guarantee, evidence,
security/schema/protocol impact, phase owners, rollback and required reruns. Owner
approves scope/capability changes; major architecture/security/delivery changes
receive independent plan review before implementation. Code uses pragmatic
red → green → refactor for critical behavior and opposite-family review:
GPT-authored code → Claude reviewer; Claude-authored code → GPT/Codex reviewer.
Review actual diff, context and evidence; re-review fixes. Missing required reviewer
blocks gate. No self-approval or inherited spike exemption.

## 9. Handoff contracts, size and definition of done

| Consumer | Required P01 input and verification artifact |
| --- | --- |
| P02 durable core | Frozen R01/R03/R10/R14, state/transaction/idempotency/ownership/identity contracts, bounds, SDK/package locks and schema/IPC compatibility policy; G02/G06 real SQLite/process artifacts and accepted plan review. |
| P03 managed agents | Qualified Claude/Codex/Pi candidate transports/version cells, including native Windows Pi lifecycle, B01–B08 regression fixtures, Claude origin/cancel decision, approval/replay/foreign-input rules; G03/G04/G05 evidence. Product adapters still require their own TDD and live qualification. |
| P04 orchestration | Principal/cursor/read-ack contract, nested attenuation/budgets, workspace physical ownership and required Claude/Codex native-wake matrix, transport/security fixtures and unresolved scope decisions; R04–R08 ledger and G05/G06 artifacts. |
| P05 platform experience | Early three-platform production-context proof, exact provider/OS/RID matrix, launcher/credential/logout/TCC decisions and explicit-mode config contract; G02/G04 artifacts. No fatal platform discovery intentionally deferred. |
| P06 extensions | Additional Pi lead-host integration and join capabilities (managed Pi already belongs to P03), ticket authority/revocation, attached lifetime limits, generic connector version/ID/lease/offline/export contract; G05 artifacts. Remote authority cannot bypass local API or leak external domain types into Business/DAL. |
| P07 qualification | Complete traceability seed and remaining C/L/T obligations, threat negatives, resource envelope, backup/upgrade/recovery expectations, evidence format and nonblocking follow-ups. |
| P08 release operations | Support-claim rules, dependency/native-asset inventory, public evidence boundary, license/publication decisions still requiring owner, compatibility and rollback requirements. |

Connector contract must set remote generation/lease authority before acceptance.
Default on lost lease: no new dispatch; an active job follows its accepted
pause/interrupt/stop policy and remains ownership-blocked until verified safe.
Local operator can stop for safety; neither remote nor local lead may silently
resume under a conflicting generation. Retry of external command ID maps to same
local operation; results export requires durable acknowledgment and consent.
Protocol reconnect does not authorize a second execution owner.

P01 overall: **L effort, low-to-medium confidence**. W01/W06 are bounded document
and review work; W02/W04/W05 dominate uncertainty; W03 is medium-low until native
packages run. Package order is W01 → W02 → W03 → W04 → W05 → W06. Platform access
can be arranged early, but no dependent implementation bypasses gate failures.
Hours-scale demo is separate; no full-product calendar promise is credible here.

### Exit checklist

- [ ] Canonical F01–F27 mapped through local R01–R16 and all mandatory target cells accepted; exact tested versions and
  support boundaries frozen, including Pi, independent join and wake.
- [ ] D01–D05 resolved at their deadlines; G01–G07 passed with reproducible,
  sanitized artifacts. Unsupported mandatory capability has not been waived.
- [ ] All eight blocking findings and product-blocking observation gaps closed
  and independently re-reviewed; Claude cancel and trusted origin proved.
- [ ] .NET 11/ref-pack/package/native SQLite/MCP/AOT proof exists on real baseline
  platforms; intended service-session risks retired before P05.
- [ ] Every promised native-wake host/platform feasibility gate passed, including
  Windows and Pi; one-host evidence is checkpoint only. No manual-only waiver,
  watcher/keystroke/polling normal path or upstream test counted as our proof.
- [ ] Threat, delivery, physical ownership, recovery, versioning and connector
  contracts handed off with explicit downstream evidence obligations.
- [ ] Independent plan review, owner freeze and nonblocking follow-up records
  exist. No historical review is reused as current approval.
- [ ] Document formatting/local links checked; future runtime execution has
  format/analyzer/build/test/published-binary gates and honest unrun labels.

**Definition of done:** evidence-backed scope and feasibility freeze accepted by
owner and independent reviewer, sufficient for P02 to implement safely without
inventing platform or delivery guarantees. This authored draft does not meet
that definition yet.
