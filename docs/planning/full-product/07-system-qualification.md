# P07 — Integrated system qualification

## 1. Objective, scope, and current status

**Draft plan, not an audit verdict, release approval, or evidence of passed tests.**
P07 qualifies the integrated full product **after accepted P01–P06 exits**. It
combines their tested vertical slices into a release-candidate evidence baseline
for P08. Testing, threat analysis, security controls, TDD and code review belong
to every feature phase; P07 is not their first occurrence.

Authority: [shared brief and template](README.md), [canonical F01–F27 scope](../../product-scope.md),
[waterfall roadmap](../../roadmap.md), [contributor policy](../../../AGENTS.md),
[architecture](../../architecture.md), [delivery and recovery plan](../../plan.md),
[PoC cases](../../poc.md), [terminal contract](../../terminal-modes.md),
[.NET 11 research](../../research/net11-process-api.md), and
[handoff](../../../HANDOFF.md). Available predecessor plans read for this draft:
[P01](01-foundation.md), [P02](02-durable-core.md),
[P03](03-managed-agents.md), [P04](04-team-orchestration.md),
[P05](05-platform-experience.md), [P06](06-extension-integration.md), and
[cross-phase synthesis](contracts.md). Accepted predecessor execution evidence,
not draft availability, determines entry. The latest requirement places managed
Pi in P03 and installed qualification in P05, not P06. P06 extends attachment/join,
additional lead hosts (including Pi lead wake) and the generic opt-in connector.

Planning assignment requests GPT-6 Astra at medium effort, one author per phase.
This document neither certifies model identity nor launches other agents.
Independent review remains outstanding. Existing spike reports, old reviews and
.NET 10 observations do not qualify the integrated .NET 11 product. Known spike
safety findings require closure and re-review against the promoted revision.

### Included and excluded

Included: integrated security, supply chain, crash/recovery, backup/restore,
upgrade/rollback, bounded load/performance, installed three-platform behavior,
operator acceptance, and traceable go/no-go handoff. The baseline includes
first-class spawned managed Pi alongside Claude Code/Codex from P03, mandatory
native Windows Pi installed qualification from P05 in both selected launch modes,
bounded nesting, supported cooperative attachment/join and the available but
opt-in generic connector. Local-only operation remains independent of a server.

Excluded: rich dashboard beyond the scoped F27 console, mandatory cloud/telemetry, new backend vendors/RIDs,
arbitrary Desktop adoption, distributed scheduler, custom model loop, blanket
exactly-once external effects, or seamless adoption of arbitrary processes and
stdio. Publication, license selection, signing authority and release operation
belong to owner/P08 decisions. This planning task authorizes none of them.

Runtime stays feature-first in exactly **Host → Business → DAL**. Host handles
presentation, identity/IPC/MCP and service lifecycle; Business handles policies,
backends, terminals and orchestration; DAL handles records, SQL and atomic
persistence. No qualification framework becomes a fourth production layer.

## 2. Entry criteria and release-candidate freeze

P07 execution requires accepted evidence, not merely predecessor plan files:

| Input owner | Required input before qualification |
| --- | --- |
| P01 | Approved F baseline, exact support cells, threat model, SDK/package/provider decisions, measurable resource envelope and reviewed design. |
| P02 | Published durable-core evidence; transaction/idempotency/attempt/reconciliation contracts; real SQLite/process fault harness; migration and backup primitives. |
| P03 | Reviewed Claude Code/Codex/Pi adapters in both modes, including native Windows Pi spawn/follow-up/correlated native results/interrupt versus stop/reconnect, native origin/result/control evidence, replay and physical ownership rules; repaired spike blockers with re-review. |
| P04 | Multi-team/nesting authority and aggregate budgets, durable receipt/read-ack, approvals, workspace/artifact safety, default native-wake and explicit degraded read/catch-up contracts. |
| P05 | Release-like packages, actual installed per-user service/desktop launch contexts, explicit setup/doctor, config merge, upgrade/rollback/uninstall and retained-data procedures on all required platforms; P05-PI01–PI04 installed managed-Pi evidence, including actual Windows .cmd/Node launcher, quoting/Unicode, profile/credentials, ACL/Job Object and user-desktop tests. |
| P06 | Named cooperative attached/join combinations and consent/revocation, additional lead-host contracts including independently evidenced Pi lead wake where promised, generic connector protocol/conformance peer, pairing, lease/offline/export rules and disabled-state evidence. |

All predecessors supply exact source/artifact identities, reviewed changes,
failures/unrun cases, schema/API/config versions and reproducible fixtures. A
faulted preexisting test is not ignored as “unrelated”: retain its failing record,
triage cause and impact, repair or obtain an explicit nonblocking disposition.
An unexplained failure on a required safety path blocks entry/exit. Quarantining
a flaky test does not supply missing evidence; use a verified replacement while
repairing the test, or keep the gate blocked. Never report a filtered suite as a
whole-suite pass.

Apply the chosen synthesis; its ownership decisions are not unresolved
contradictions. P01 must still freeze measured limits and evidence-backed cells:

- Two active turns globally, root depth zero and at most three delegation edges
  remain proposals pending P01 freeze, not approved capacity or P04 alternatives.
- P02 global lifecycle event sequence is distinct from P04 recipient-delivery
  sequence/cursor. P04 owns public canonical batch read/ack; migration must retain
  event references and separate run/consumer generations, never a second event
  authority or destructive filtered acknowledgment.
- Native wake is standard and enabled by default for supported hosts. At least
  one automatic host is an early checkpoint only; every promised host/platform
  native-wake cell must pass for full release. Manual read/catch-up is explicit
  degraded recovery, not a frozen manual-only waiver. No normal watcher,
  keystroke injection or tight model polling. Managed Pi execution does not prove
  Pi lead wake, separately qualified through P06; Codex member wake does not
  prove Codex-lead self-wake.

Freeze these contracts and their remaining numeric/migration/version details
before qualification. F01–F27 remains canonical; historical R IDs are aliases.

### Two freezes, not indefinite retesting

1. **Qualification baseline:** accept predecessor exits, review this major plan,
   freeze required cells, workloads, thresholds, test budget, destructive-test
   permissions and upgrade sources. Close missing design decisions first.
2. **RC freeze:** assign immutable candidate identity to source snapshot, package
   locks, SDK/ref pack/compiler/native assets, build recipe/options, binaries,
   schema/config/API/capability versions, fixtures and operator documentation.
   Record hashes and build provenance; a source/archive manifest is sufficient
   without requiring any Git operation. No feature additions during RC testing.
3. Repairs produce a new RC identity, focused regression, opposite-family code
   review and fix re-review. Keep old failed evidence immutable. Define affected
   cells and reruns before accepting replacement results (section 9).
4. Freeze final evidence manifest and candidate after all required gates; hand
   these exact artifacts to P08. Repackaging, dependency changes or signing steps
   that change artifacts require documented deltas and appropriate smoke/security
   revalidation. P08 does not release an untested rebuild under the same label.

Candidate SDK from research is `11.0.100-rc.1.26425.128`, not proof of installed
ref-pack or package compatibility. Use the accepted P01 .NET 11 pin and record
actual restored versions. Any toolchain update creates a new candidate and
requalifies affected platform/AOT paths; no implicit .NET 10 or JIT fallback.

## 3. Evidence contract and bounded test design

### Provenance and artifact snapshots

Maintain one planned qualification ledger, not duplicate reports per feature.
Each row binds F ID, C/L/T or P07 scenario, gate, support cell, execution recipe,
expected assertion, observed outcome, executor/reviewer and immutable evidence
reference. Status is `planned`, `passed`, `failed`, `blocked`, `unsupported`, or
reasoned `not_applicable`; inherited evidence is separately marked with its
provenance and change-impact justification. No unspecified row is green.

Each execution snapshot records:

- RC/package and fixture hashes; test implementation revision; exact commands,
  parameters, seeds/barriers, times and repetition count; prerequisites and costs.
- OS build/RID/architecture, hardware/storage/filesystem, service account class,
  desktop/lock/logout state, terminal/backend/host/control-protocol versions,
  .NET SDK/ref pack/runtime, MCP packages, native SQLite version and build flags.
- Explicit mode, capability/policy/limit profile, authentication method without
  credentials, normalized operation/job/run/generation/cursor identities and
  actual result/effect counts. Test IDs are not private identifiers.
- Package inventories, build logs/warnings, raw metrics, before/after logical DB
  extracts, migration and backup manifests, process/descendant ownership traces,
  authoritative native receipts and manual observer records where needed.
- All failures, retries, skipped/blocked paths, limits of observation and review
  dispositions. Retries remain visible; a later success does not erase a race.

Use synthetic repositories and sentinel credentials, not production data. Keep
raw evidence private with restricted access and bounded retention; publish only
reviewed sanitized derivatives with redaction manifest and hashes. Public records
use relative paths and synthetic IDs, never personal paths, account/device IDs,
secrets or raw sensitive transcripts. Preserve enough correlation for diagnosis;
if redaction prevents reproduction, provide a synthetic reproducer instead.

### Small shared fixtures, strong assertions

Reuse P02's C# barrier-driven process/SQLite harness, P03's native evidence and
terminal fixtures, P04's authority/cursor/quota fixtures and P05/P06's installers
and conformance peer. Add only integration gaps: one controlled scenario may
satisfy several case IDs, but the ledger records each distinct assertion. Tests
assert effects, state, authority, bounds and identities, not prose labels,
incidental formatting, private classes or numerical coverage quotas.

Use actual abrupt process kills at deterministic commit/effect barriers. Exceptions,
graceful shutdown and mocks alone do not prove crash survival. Fake peers drive
repeatable races, floods and malformed inputs; they cannot pass real-agent,
GUI-service, native protocol, wake or installed-platform gates. Avoid multiplying
every fault by every backend when a shared storage rule is identical: test the
shared rule per OS/AOT path and representative adapter integration, then every
adapter-specific ownership, result, approval and interruption contract in its
required cells. Document selection and residual risk, never silently omit cells.

### Scheduling and access cost

| Lane | Scope and cadence during implementation/RC | Cost and limits |
| --- | --- | --- |
| Change CI | Focused TDD regressions, full applicable lean suite, format/analyzers/build, real SQLite, IPC/auth, fake process barriers, dependency/secret checks and documentation links. | No model calls; supported native runners required for OS-specific behavior. |
| Native candidate CI/lab | Published JIT/AOT MCP/SQLite/process paths on each RID; package install/upgrade, bounded stress, storage faults, consistent backup/restore. | Native compiler/SDK and OS runners; isolated volumes and accounts; cross-build does not count as runtime evidence. |
| Booked desktop lab | Actual Herdr/Linux and selected Windows/macOS terminals with real agents, human input, neighboring tabs, service identity, lock/logout, controller/daemon death and wake. | Real desktop access, human operator, backend credentials and approved model budget. Headless hosted CI is insufficient. |
| Controlled resilience/performance lab | Reboot/storage-fault exercises, per-platform soak and same-machine JIT/AOT comparisons, destructive cases on disposable state only. | Reserve hardware/time/disk and cleanup owner; power interruption needs separate authorization and facilities. |
| Final acceptance | Fresh-profile operator walkthrough on each platform plus integrated extension/connector demonstrations. | Independent operator availability and recorded steps; F27 console included, no external cloud account required. |

Book Windows/macOS GUI access early. Missing access blocks required cells; it
does not lower their priority or remove their cost. Real model tests use small
harmless jobs; long load/soak uses fake workers and sampled real integration,
not expensive continuous model calls. Stop spend at approved experiment budget
without claiming the unfinished gate passed.

## 4. Required platform and integration matrix

Baseline RIDs: **Linux x64, Windows x64, one verified macOS architecture**. P01
proposes macOS arm64; accepted P01 decision must confirm it and provider/version.
Pin one supported OS/provider/backend version set initially; extra versions and
architectures require explicit scope and evidence, not a combinatorial promise.

For **each baseline platform**, qualify the published .NET 11 Native AOT package
and its installed production launch path, not merely `dotnet run`:

| Axis | Required coverage and observations |
| --- | --- |
| Core/native | MCP initialize/list/call, JSON registration under trimming, authenticated UDS/named pipe, native SQLite WAL commit/reopen/recovery/migration, process launch/descendants and role-isolated bridge with no DB open. Analyze every trim/AOT warning and narrow suppression. |
| Managed execution | Claude Code, Codex and managed Pi, real interactive and explicitly selected headless; first job, same-session follow-up, authoritative result, approval, interrupt distinct from stop, policy/deadline and client/daemon loss. |
| Pi (F19) | Mandatory P03 spawned managed backend, installed-qualified by P05 on every baseline platform, including actual native Windows interactive/headless. Spawn, same-session follow-up, correlated native results, approvals where exposed, interrupt versus stop and reconnect use the same identity/authorization/recovery contract. Test Windows .cmd/Node launcher quoting, Unicode paths/argv, profile/credentials, ACLs, inherited handles/Job Objects and selected user desktop. Visible selected terminal or explicit headless only. Linux, cross-build, attach-only and Pi lead-host evidence cannot pass F19. |
| Desktop/service | Linux user service outside Herdr/client kill scope; Windows verified per-user desktop launch, never assumed Session 0 GUI; macOS verified user/GUI launch and selected terminal/TCC permissions. Check PATH, profile/home, config and credential-store resolution from actual service context. |
| Terminal | Real human input and machine follow-up, persistent human-wins pause, targeted stop with unrelated neighboring tabs, provider/tab death, GUI unavailable/locked/logged-out, mode change affects new agents only; no fallback. |
| JIT comparison | Exact same .NET 11 source, features, packages, workload and machine; run core and lifecycle parity smoke plus measurement suite. JIT success never substitutes for failed AOT. |

Attachment has a **separate pinned cooperative host/provider matrix** from P06;
managed support does not imply arbitrary attachment. Run each promised join cell
with consent, credential attenuation/revoke, human activity, disconnect/rejoin,
conflicting local authority and external host death. Joining does not confer
managed client-crash survival or permission to kill an independently owned host.

Host wake has its own version/launch-context matrix. Every promised host/platform
cell must demonstrate default native idle wake, authenticated session/generation
binding, notice-only payloads, coalescing, duplicate notifier fencing, bounded
retry and restart/reconnect unread catch-up. Commit messages before wake; notice
success is neither read/ack nor model action. Test final-message failure with no
later send: bounded retry or explicit degraded status, no lost acceptance or
advanced cursor. Manual read/catch-up is explicit degraded recovery, not a pass.
An MCP notification/test-client read cannot prove idle-host wake. Historical L07
remains Claude-specific; Codex and Pi lead-host claims need separate proof.

[PR #70 research](../../research/native-wake-pr70.md) records merge to main
`471a17514d041e09b69cb24b910e418da28d2027` and Windows W1–W3 passes.
Reference concept works on Linux and Windows: native Codex member wake passed;
W3 proves Claude's safe unsupported response, not native Windows-Claude wake.
Windows-Claude, macOS and Pi native transports remain gaps; Codex-lead self-wake
is not proven by member wake. These results do not pass our published .NET
integration gates. Actual native-platform tests remain required for every promised
cell; missing transport or access blocks full qualification.

Connector tests use P06's public generic conformance peer and real transport on
each claimed deployment platform. This qualifies that protocol, not unnamed
third-party products. Test off = no connector contact/export/dependency;
enabled pairing/revocation, redelivery, remote generation/lease races, offline
accepted policy, ownership conflict and explicit export consent. Other network
traffic from agent providers is classified separately. Local-only jobs must
continue without the peer. No mandatory server/cloud infrastructure is added.

## 5. Ordered vertical work packages

H/B/D mean Host/Business/DAL. These are qualification slices and narrowly scoped
repairs to owning features, not a layer-first rewrite. Every package depends on
the previous package's accepted output; preparing fixtures/access does not waive
entry. Sizes exclude external waiting time.

| Package | Observable result, deliverables and meaningful verification | H/B/D touchpoints | Dependency; effort/confidence |
| --- | --- | --- | --- |
| P07-W01 | Accepted RC/requirements matrix and inherited-evidence ledger; verify synthesis freeze/evidence and disposition all preexisting failures. Freeze workloads, numeric bounds, budget and platform reservations. Replay predecessor smoke to confirm fixture compatibility. | H version/doctor claims; B capability/policy freeze; D schema/migration identity. | P01–P06 exits and plan review; M/medium. |
| P07-W02 | Installed native candidate functions on all three platforms; dependency inventory, provenance and initial security negatives qualified. Real setup choice, MCP→job→SQLite path and refusal of incompatible artifacts/config. | H packages/service/MCP/bootstrap; B launch/authority; D native assets/compatibility. | W01; L/medium-low. |
| P07-W03 | Integrated adversarial authorization, revocation and extension controls fail closed. Shared races cover nested grants/budgets, read/ack, Pi, join and connector; real native controls confirm declared semantics. Deliver threat-to-evidence disposition. | H peer identity/nonce/CLI exposure; B grants/leases/paths/adapters; D atomic revoke/claim/receipt/generation. | W02; L/medium-low. |
| P07-W04 | Repeated client/daemon/backend/provider failures, storage faults and maintenance restores preserve honest state and physical fences. Deliver recovery, backup and rollback rehearsal records plus operator runbook corrections. | H independent lifetime/drain/maintenance; B recovery/teardown/evidence; D commit barriers/WAL/backup/migrations. | W03; L/medium-low. |
| P07-W05 | Enforced queue/concurrency/storage/deadline limits and comparable JIT/AOT measurements meet frozen budgets. Deliver raw benchmark/soak records, bottlenecks and capacity envelope, not unsupported savings claims. | H bounded IPC/diagnostics; B scheduling/stream drains/wake; D busy handling/retention/event backlog. | W04; L/medium. |
| P07-W06 | Fresh operators complete setup, real work, controller-death recovery and safe update on each platform; supported-host wake and extension acceptance witnessed. Negative cases produce actionable wait/refusal without hidden bypass. | H setup/doctor/CLI; B user-facing policy/reconcile; D persisted choices/results/receipts. | W05; M–L/medium-low. |
| P07-W07 | Fixes independently reviewed and affected gates rerun; final immutable evidence/RC manifest and explicit go/no-go delivered to P08. | H published package/claims; B capability limits; D supported schema/restore contract. | W06 and all gates; M/medium. |

### W02–W03: security and supply-chain qualification

Inventory direct/transitive NuGet packages, native SQLite/compiler/runtime assets,
installer dependencies and required external CLIs/providers, separating bundled
components from prerequisites. Produce component versions, hashes, sources and
SBOM/notices inputs. Verify package sources/lock files, restore/build provenance,
release integrity and clean reproducible build recipe; if outputs are not
bit-identical, record reasons and verifiable differences rather than claiming
bit-for-bit reproducibility. Check tampered package/update refusal before state
mutation. Supply-chain assessment includes build scripts, hooks and config
sources, not just CVE counts.

Run dependency advisories, secret scanning of source/config/fixtures/packages and
sanitized exports, license compatibility and redistribution/notice review.
Record scanner/database versions and assessment time; refresh advisories before
P08 handoff. Scanner silence is not security proof. Unknown component origin,
unlicensed redistribution or undisposed critical/high exposure blocks handoff.
License choice remains owner's decision; P08 cannot receive a distribution-ready
claim while it is unresolved. No automated tool selects a license on owner's behalf.

Shared adversarial fixtures must exercise:

- Wrong OS peer, team/agent IDs, child-to-lead escalation, cross-team references,
  stale consumer generation, forged/reused/expired bootstrap/join nonce and
  concurrent capability revocation versus acceptance/dispatch/approval/export.
- Nested grants attenuate along every ancestor; concurrent final-slot reservations
  cannot exceed root budgets; cycle/depth/epoch exhaustion survives restart.
  Informational messages do not create duplicate executable jobs.
- Environment **allowlisting**, inherited handles and service credentials: plant
  sentinel host secrets, verify only approved backend authentication reaches its
  intended process, no unrelated host/session capability reaches descendants,
  and no secrets enter argv, logs, exceptions, MCP replies or diagnostic exports.
  Confirm actual provider inheritance, not just direct-child launch behavior.
- Traversal, symlink/reparse/hard-link substitutions where relevant, file swaps
  between validation and use, executable/PATH substitution, config/hook discovery,
  untrusted backup/archive paths and cleanup/export escapes. Assert no access or
  deletion outside authorized owned roots; dirty/user-owned work survives cleanup.
- Native-origin spoofing, stale binding, replayed results, human/foreign activity
  and approval races; terminal text/model self-report never completes a job.
- Connector remote lease expiry/revoke/reconnect while local cancel or dispatch
  races; stale owner cannot create a second writer, export or broaden authority.
  Accepted offline policy is enforced; unknown physical outcome stays fenced.

Private transport/capabilities do not sandbox malicious same-user arbitrary code.
Document residual filesystem/process authority and retain backend permissions;
no blanket permission bypass to make a live gate pass.

### W04: recovery, storage and maintenance

Use abrupt kills separately for lead+bridge, daemon, backend and terminal provider.
Exercise pre-commit, accepted-unattempted, attempted-pre-effect, lost native ack,
completion-before-final-commit and completion-during-outage. Record externally
observed effects independently of DB status. Preserve live interactive TUIs on
daemon recovery; verified rebind or visible quarantine only. Verify PID/start
identity, session/provider provenance, descendants and unrelated tabs. Logical
generation fences alone cannot prove old processes stopped writing.

Test actual bounded-volume disk exhaustion, SQLite busy/corrupt input, missing or
torn evidence spool and failed final write, alongside labelled injected faults.
No false acceptance, empty-state reset, silent data loss or blind retry. A backend
crash/EOF is not success; a result observed during outage needs authoritative
correlated replay or honest uncertainty. Process-kill tests establish process-crash
behavior only. Controlled reboot/power-failure experiments must record storage,
filesystem, flush settings and fault method. VM reset is not proof of physical
power-loss durability; `synchronous=FULL` is not universal hardware proof. No
stronger claim than measured evidence. Lack of power-loss facilities limits the
claim explicitly; it does not waive ordinary crash/recovery gates.

Maintenance rehearsal:

1. Stop admission/new dispatch, fence reconnect/connector ownership changes, and
   drain under a finite deadline. Preserve queued intent and durable cursors.
   Approval/uncertain/live interactive work blocks unsafe maintenance; never
   auto-kill it to force drain success. Explicit operator intent and proven
   ownership govern stopping. Abort safely if drain cannot establish conditions.
2. Take a SQLite-consistent online backup or a documented quiesced/checkpointed
   snapshot that includes committed WAL contents; never copy a live main DB only.
   Bind schema/config, required artifact/backend evidence references and integrity
   hashes in a private backup manifest. Distinguish point-in-time coverage of DB,
   files and backend records; report unavailable references, not invented recovery.
3. Restore to isolated state with dispatch disabled. Validate integrity and
   operation/idempotency/cursor/policy continuity; restore is not a replay permit.
   Fence old capabilities/consumer and remote generations. Inventory survivors and
   post-snapshot effects before releasing conversation/workspace ownership. A job
   unattempted in an old backup may have executed since: quarantine it too.
4. Upgrade each supported prior schema/config/package fixture, including realistic
   queued, active, approval-blocked, uncertain and unread states. Interrupt migration
   at durable boundaries; resume/refuse without partial usable schema. Unknown
   newer schema refuses without mutation. First release uses honest synthetic
   predecessor fixtures; do not pretend prior public releases exist.
5. Rehearse failed update and rollback. Downgrade binaries only against compatible
   schema; otherwise restore verified snapshot with orphan fences and preserved
   newer journal. Do not reverse migration numbers or erase post-backup effects.
   Uninstall retains/deletes data only by explicit choice and never kills or deletes
   foreign resources. Record observed recovery time and possible data-loss window.

### W05: resource and performance baseline

Freeze a finite supported profile before testing: active agents globally/per team,
queue jobs/bytes, nesting and epoch limits, prompt/frame/output/spool sizes,
artifact/log/event retention, IPC concurrency, busy/request/drain timeouts and
supported retry windows. Specify workload duration, offered load, memory metric,
peak/slope ceilings and recovery-time objectives. Unresolved numeric limits block
W05; do not move thresholds after failure without recorded rebaseline and rerun.

Minimum exact benchmark recipe, repeated per platform for .NET 11 JIT and AOT:

- Ten minutes idle with no clients; ten minutes with two bridges and no turns.
- At least 20 warm startups for daemon and each bridge role; report cold starts
  separately with cache preparation. Daemon ready = authenticated IPC usable.
- At least 1,000 durable accepts at 20 sequential accepts/s and corresponding
  commit-to-readable-event measurements, excluding model latency.
- Two deterministic fake workers; burst of 20 queued jobs, maximum two active;
  fixed recorded output bytes/rate and slow-consumer backlog/reconnect profile.
- Capacity run at frozen supported maxima, then controlled overload to verify
  rejection/backpressure, followed by recovery. One 24-hour fake-workload soak
  per platform across queue turnover, output, reconnect and rotation; bounded
  process churn samples handles/FDs, memory and DB/WAL/log growth. Pin fake rate,
  payloads, seed and retention profile before run; shortened soak is incomplete.
- Small equivalent real Claude/Codex/Pi jobs per declared configuration under an
  approved budget. At least five comparable jobs if making live token/efficiency
  observations; otherwise report insufficient sample, never estimated savings.

Initial reference-machine performance criteria retain PoC proposals: idle daemon
plus two bridges below 1% of one logical core; accept p95 below 100 ms; committed
event readable p95 below 250 ms; warm AOT daemon startup median below 1 s; each
AOT host/child bridge warm startup below 500 ms and idle memory below 40 MiB.
Freeze the bridge startup statistic explicitly with the benchmark recipe. Capacity
and steady-state memory ceilings need measured P01 freeze with P04 enforcement; report peak and trend
after warmup/retention cycles, not a short idle sample hiding growth with jobs.
An approved exception records failed original target, rationale and new envelope;
no post-hoc green result under an unchanged target.

Report median/spread/percentiles, raw samples, disk/fsync environment, CPU/wakeups,
accept/event/recovery latency, memory per daemon/bridge role and whole process
tree including agents, terminal helpers and shell descendants. Linux PSS and
Windows working/private memory are distinct metrics; use documented native metrics
on macOS too. Separate cold/warm caches and live model variance. Compare JIT/AOT
on the **same machine**, source, packages, policy, mode and workload; platform
differences are not savings. Without equivalent public comparison workflow, make
no comparative-product claim. Runtime memory cannot prove token savings.

Stress includes stalled/oversized IPC peers, fast output with slow/disconnected
consumers, unread backlog/retention pressure, approval waits, queue/nesting
exhaustion, connector/wake bursts and restart near deadline. Bound memory and
logs while draining output; preserve final evidence or expose uncertainty on
exhaustion. Budgets survive restart, unknown usage is not zero, and advisory cost
caps remain labelled advisory. Unverified interrupt cannot become a claimed hard
deadline; only accepted, proven-owned termination policy permits escalation.

## 6. Gate table

All gates are planned/unrun. Implementation must supply reproducible recipes;
this plan does not claim commands or evidence directories already exist.

| Gate | Reproducible verification and exact success criterion | Evidence class and blockers |
| --- | --- | --- |
| P07-G01 — Entry and provenance | W01 ledger covers F01–F27 and all C/L/T definitions; accepted P01–P06 artifacts and RC hashes match; every old failure and contract conflict has disposition; independent plan review recorded. | Documentary plus inherited execution evidence. Missing P05/P06 handoffs, limits, review or required cells blocks entry. |
| P07-G02 — Native installed matrix | W02 package recipe runs on each real RID; published AOT invokes MCP/JSON/SQLite/WAL/process paths, actual service context and explicit-mode doctor; all required mode/platform cells for all three managed backends pass relevant lifecycle/terminal assertions, including P07-PI01–PI04 and native Windows Pi installed tests. Zero unanalyzed AOT warnings. | Native OS plus real agents/GUI; fake smoke alone insufficient. Missing machine, provider, credentials or safe native capability blocks affected required gate. |
| P07-G03 — Security and supply chain | W02/W03 component/provenance/license review and sentinel/adversarial scenarios: zero unauthorized effects/disclosure/escalation, zero secret leakage, no unsafe path or tampered update accepted; all critical/high exposures resolved or documented false positives. | Real files/IPC/accounts/native launch plus repeatable fakes for races; scanner output alone insufficient. Unknown origin/redistribution rights, unsafe grants or unreviewed findings block. |
| P07-G04 — Integrated execution and authority | Run W03 team/Pi/join/connector scenarios: one scoped operation, bounded ancestor reservations, no unread skip, no second execution owner; revoked/stale identities cannot authorize new effects/export; connector off makes no contact. | Real Pi/cooperative hosts and actual generic connector transport plus race fixtures. Fakes do not qualify live capabilities; unsupported required cells block full exit; Pi attachment or lead wake cannot substitute for the managed backend. |
| P07-G05 — Crash/recovery correctness | W04 barrier kills and actual backend/provider death preserve committed acceptance, authoritative result semantics and physical ownership; no blind retry, foreign teardown or invented completion. L03 retains three successful repetitions per Linux mode. | Actual process/SQLite faults and real native control/replay. Failed repetition remains failure to triage; no reliability claim from a single lucky pass. Power-loss claim limited separately. |
| P07-G06 — Maintenance and rollback | Execute W04 backup/restore/migration/drain/update/rollback on each platform and supported source fixture. Integrity and identities retained within declared snapshot boundary; incompatible schema untouched; zero dispatch until restored ownership is reconciled; no lost committed WAL within backup boundary. | Real packages/files/SQLite with crash barriers; fake faults supplement actual disk-full path. Unproven restore, orphan fence or unsupported upgrade source blocks. |
| P07-G07 — Resource envelope | W05 stress/24-hour soak meets frozen finite bounds and recovery objectives; no linear unbounded completed-job memory growth, skipped unread data or false hard cap. Backpressure and deadline outcomes match accepted policy after restart. | Deterministic fake load on each native platform plus sampled real flows. Missing predeclared memory/slope/rate bounds, unfinished soak or resource leak blocks. |
| P07-G08 — Comparable performance | Execute frozen JIT/AOT recipes with minimum samples/raw data on each platform. Meet recorded thresholds or obtain explicit disposition preserving failed target; comparisons use same-machine equivalent conditions. | Published binaries, native metrics; real-token observations separately budgeted. Incomparable hardware or absent baseline prohibits savings claim, not fabricated measurements. |
| P07-G09 — Operator/host acceptance | Section 8 walkthrough succeeds from clean profile per platform; real human input/controller death, recovery/upgrade and default native idle wake observed for every promised host/platform cell, without normal watcher, keystrokes or tight model polling; explicit degraded recovery tested separately. Negative paths actionable with no fallback/bypass. | Human/GUI/real agent evidence plus fresh operator record. Passive reads cannot pass wake; missing operator/access or required host blocks. |
| P07-G10 — Candidate handoff | Full applicable format/check/build/test/published gates and delta reruns recorded for final RC; blocking findings closed and fixes independently re-reviewed; final evidence/support/runbook bundle accepted for P08. | Independent review and owner go/no-go, not author self-approval. Unexplained test failures, stale evidence, missing license decision or required cell blocks full release recommendation. |

## 7. Complete traceability

The following mapping accounts for every canonical case; it does not mark any
case passed. Each ledger row must retain original expected outcomes and required
platform/backend/mode/repetition details. Reuse a predecessor run only if exact
artifact applicability and rerun analysis justify it. Final installed live
acceptance runs on the RC; old Linux spikes never qualify current packages.

### C01–C32

| Cases | Distinct assertions retained | P07 gates |
| --- | --- | --- |
| C01, C02, C03 | Lost response same operation; changed payload conflict; concurrent identical requests one durable identity/effect. | G04, G05 |
| C04, C05 | Active work survives client kill; queued eligible work dispatches without client. | G05, G09 |
| C06, C07, C08 | Before commit no acceptance; accepted-unattempted safe single dispatch; started attempt before spawn/launch/prompt remains uncertain absent evidence. | G05 |
| C09, C10 | Lost backend ack never blindly resent; final-write failure reconciles authoritative result or exposes uncertainty. | G05, G06 |
| C11, C12 | Lost read repeats unacked IDs; lost/retried/forged ack never skips unread batch. | G04, G07 |
| C13 | Old-generation evidence cannot change current run. | G04, G05 |
| C14, C15, C16 | Reused PID not adopted/killed; uncertain survivor blocks new work without auto-killing TUI; owned orphan descendants stopped before new writer. | G03, G05, G06 |
| C17 | Backend death/EOF/incomplete JSON gives failure or uncertainty, not completion. | G02, G05 |
| C18, C19, C20 | Slow reader cannot stall bounded drain; full/busy/corrupt/lost commit fails closed; queue/output limits reject/backpressure. | G05, G06, G07 |
| C21, C22, C23 | Idempotent interrupt before/during/after turn distinct from stop; timeout escalation only by accepted policy with confirmed outcome; absent approver blocks or explicitly unsupported, never bypassed. | G02, G04, G07, G09 |
| C24, C25 | Team/role substitution denied; invalid/reused nonce/credential/version/oversize request rejected before effects with bounded error. | G03, G04 |
| C26, C27 | Second daemon refused without takeover; Unicode/newline/metacharacter prompt preserved without shell interpretation. | G02, G03, G05 |
| C28, C29 | Fake connector duplicate uses same local operation; diagnostic/failure secrets absent. C28 alone does not qualify F21 real transport/leases. | G03, G04 |
| C30, C31, C32 | Newer schema refused unchanged; retried reconciliation evidence-bound with new-key attempt only after physical safety; reconnect fences stale consumer reads/acks. | G04, G05, G06 |

### L01–L12

| Cases | Required live assertion | P07 gates |
| --- | --- | --- |
| L01, L02 | Codex and Claude first-result-follow-up in same session, both Linux modes; extend product lifecycle qualification to required platform cells. | G02, G09 |
| L03 | Both backends active; real lead and bridge die; new lead retrieves results; three successful repetitions per Linux mode. | G05, G09 |
| L04, L05, L06 | Both backends/both Linux modes: native turn interrupt with process inspection, no-lead approval, daemon outage with replay/reconciliation and no auto-kill/blind retry. | G02, G05, G09 |
| L07 | Actual idle Claude lead wake plus burst, duplicates, disconnect/catch-up. Codex/Pi named-host claims need separate proof. | G04, G09 |
| L08, L12 | Actual Windows/macOS published AOT, IPC, fake process/client crash/recovery; selected macOS architecture recorded. | G02, G05 |
| L09 | Real Windows/macOS headless Claude/Codex start-result-interrupt/stop; mandatory full-product support, not merely an early recommendation. | G02, G09 |
| L10, L11 | Real Windows/macOS interactive agents with all applicable terminal cases; service qualification includes T13/T14, not just PoC subset. | G02, G05, G09 |

### T01–T19

| Cases | Required assertion on actual applicable platform/provider | P07 gates |
| --- | --- | --- |
| T01, T02, T03 | Explicit setup choice, persisted after restart, missing provider/GUI gives wait/error without fallback. | G02, G09 |
| T04, T05, T06 | Real visible Claude/Codex TUIs, genuine human input, correct same-conversation machine follow-up. | G02, G09 |
| T07, T08 | Lead/bridge death preserves tabs; interrupt keeps intended TUI; separately authorized stop leaves neighbors untouched. | G05, G09 |
| T09, T10 | Closed tab/dead provider gives honest interruption/uncertainty; daemon restart never auto-kills live TUI or duplicate-spawns. | G05, G09 |
| T11, T12 | Prompt text intact without shell interpretation; changing default mode preserves existing bindings. | G02, G03, G09 |
| T13, T14 | Actual installed user/desktop service launches correct user's TUI; absent desktop waits/errors, never another identity/headless. | G02, G09 |
| T15, T16 | Human/foreign pause persists until authorized verified idle; self-report/text/silence cannot complete job. | G03, G04, G09 |
| T17, T18, T19 | Outage completion authoritative replay or explicit uncertainty; no-lead approval observable/unsupported without bypass; retry start same binding, no second tab. | G02, G05, G09 |

### Supplemental managed-Pi cases — third backend, unchanged historical IDs

C01–C32, L01–L12 and T01–T19 retain their original meanings, backend counts and
repetitions. In particular, L01–L06/L09 and T04 still name Claude/Codex; their
passes alone do not qualify Pi. These additional cases bind P03 adapter and P05
installed evidence to the RC. Run all required Linux/Windows/macOS cells with
visible selected interactive terminal and separately explicit headless mode;
record applicability per assertion, not a blanket two-backend case expansion.

| Case | Required integrated assertion | P07 gates |
| --- | --- | --- |
| P07-PI01 | Managed Pi spawn → first job → correlated authoritative native result → same-session follow-up on all baseline platforms/both modes. Verify approvals where exposed with a disconnected lead, duplicate-start/lost-ack semantics and denial of forged/stale correlation. No attach-only surrogate. Supplements C01/C08/C09/C23, L01/L02/L09 and T04/T06/T18/T19; consumes P05-PI01. | G02/G03/G04/G09 |
| P07-PI02 | Actual lead/bridge death and fresh-client reconnect retrieve Pi results without stale authority or duplicate work; three successful repetitions per mode/platform. Separately crash daemon/backend/provider, finish during outage and verify replay/rebind or explicit fenced uncertainty; no automatic TUI kill. Supplements C13–C17/C32, L03/L06 and T07/T09/T10/T17; consumes P05-PI02. | G02/G04/G05/G06/G09 |
| P07-PI03 | Genuine Pi TUI human input wins and pauses automation until authorized verified idle. Native interrupt preserves session/tab; separate authorized stop verifies descendants stopped and neighboring sessions untouched. Headless tests its explicit policy separately. Text/model self-report cannot complete work. Supplements C21/C22, L04 and T05/T08/T15/T16; consumes P05-PI03. | G02/G03/G05/G09 |
| P07-PI04 | Actual installed Windows Pi .cmd/Node launch chain passes quoting/Unicode paths/argument boundaries, trusted discovery, user profile/credentials, ACL/private bootstrap, inherited handles, Job Objects/descendants and selected user-desktop binding. Run PI01–PI03 in both modes; locked/logged-out/Session-0/absent-GUI and revoked-credential negatives never launch elsewhere or fall back. Supplements C25/C27/C29 and T01–T03/T11–T14; consumes P05-PI04. Linux, cross-build and fake-only runs cannot pass. | G02/G03/G05/G09/G10 |

### F01–F27 and full-product additions

| Features | Integrated evidence beyond or alongside C/L/T | Packages / gates |
| --- | --- | --- |
| F01, F02, F09 | Daemon independence, atomic durable intent/result and layered recovery guarantees; C01–C10/C13–C19/C26/C31 plus installed context. | W02/W04; G02/G05/G06 |
| F03, F04 | Authenticated CLI/MCP roles, version/config/schema compatibility and bounded protocol; C24–C26/C30/C32, revoked identities and actual AOT registration. | W02/W03; G02/G03/G04 |
| F05, F06, F07, F08 | All three managed backends with two-mode native lifecycle and true TUI/human ownership; live and terminal matrix, no headless or PID-only substitute. | W02/W04/W06; G02/G05/G09 |
| F10 | Multiple/nested teams: cycle/depth denial, sibling isolation, concurrent ancestor-budget allocation, parent loss and revocation across queued/active children. | W03/W05; G04/G07 |
| F11, F12 | Durable messages/receipt generations and filtered-read safety; default native idle wake in every promised host/platform cell, explicit degraded catch-up and duplicate/burst/offline tests; C11/C12/C32/L07. | W03/W06; G04/G09 |
| F13, F14 | Bounded queues/deadlines/approvals/usage; workspace writer exclusivity, race-aware path access, artifact/log rotation/export and missing/corrupt file references. | W03/W05; G03/G06/G07/G08 |
| F15, F16, F17, F18 | Clean-profile operator CLI/setup/doctor, actual service/GUI identity, package/config merge/update/rollback/uninstall and explicit retention decisions. | W02/W04/W06; G02/G06/G09 |
| F19 | P03 first-class spawned Pi and P05 installed lifecycle; P07-PI01–PI04 prove native Windows plus remaining baseline cells, same job/authorization, correlated native results, follow-up, interrupt/stop and reconnect. Pi lead wake is a separate F12/P06 contract, never a substitute. | W02–W04/W06; G02/G03/G04/G05/G09/G10 |
| F20 | Cooperative attachment/join consent, one-use scoped tickets, revocation, stale binding/host death, limited lifecycle authority; no arbitrary adoption. | W03/W04/W06; G03/G04/G05/G09 |
| F21 | Generic connector real transport, off/no contact, pairing/export consent, durable remote IDs, stale lease/dual-owner/offline/reconnect races. | W03/W05/W06; G03/G04/G07/G09 |
| F22, F23, F24 | Consistent WAL backup and migration/restore fences; threat/supply-chain/security assessment; native .NET 11 MCP/SQLite/process execution on each RID. | W02–W04; G02/G03/G05/G06 |
| F25, F26 | Sanitized documentation/notices/license inputs, performance/raw resource and upgrade/recovery runbooks, compatibility claims and maintenance evidence. P08 still owns authorized publication/distribution and ongoing operations. | W05–W07; G03/G08/G09/G10 |

## 8. Operator acceptance steps

A fresh operator, not only the implementing author, follows shipped CLI/setup
instructions with disposable state and approved model tasks on each platform.
Record actual actions/results; do not assert exact prose strings.

1. Install candidate in clean user profile; choose interactive explicitly and
   inspect persisted provider/mode. Omit choice, deny GUI permission, remove
   provider or use absent desktop: receive actionable wait/refusal, no fallback.
   Doctor must test service-context backend authentication, not borrow shell state.
2. Spawn real managed Claude Code, Codex and Pi sessions, send small tasks through a
   real lead's MCP path, then follow up in the same conversations. Show genuine
   human input, persistent foreign-busy pause and authorized idle reconciliation.
   Repeat core operation in separately chosen headless profile. On actual Windows,
   witness P07-PI04 .cmd/Node quoting/Unicode, profile/credentials, ACLs, Job Object
   and selected user-desktop tests; correlate native Pi results, not wrapper exit.
3. Kill controller/lead plus bridge, not daemon. Work continues under accepted
   budgets; a pending approval stays blocked. Fresh authorized controller retrieves
   results and unacked messages; stale controller cannot ack or acquire lead power.
   Show compact CLI diagnostics and the F27 text console, not an analytics dashboard
   or model polling loop.
4. Demonstrate bounded nested delegation and reject sibling control/excess depth;
   interrupt one active turn preserving TUI, then separately stop one owned agent
   without touching neighboring tabs. Missing interrupt confirmation stays visible.
5. Crash daemon separately while interactive work finishes. Rebind/replay verified
   evidence or show quarantine; new work remains blocked until physical ownership
   is safe. Show backend/provider death and failed storage as different conditions.
6. Run named idle-host wake separately from passive retrieval. Burst/duplicate
   notifiers stay bounded; reconnect catches up without skipping events. Witness
   default native wake in every promised host/platform cell without watcher,
   keystrokes or tight model polling. Inject wake failure and show explicit degraded
   manual read/catch-up, not a replacement standard path. One host is only an early
   checkpoint; any failed or unsupported required cell blocks full release.
7. Join a supported independent session with consent, revoke it and show authority
   loss without killing foreign host. Enable connector only by explicit pairing;
   duplicate command and lease loss never create two owners. Denied export sends
   no data; disabling connector restores no-contact local-only operation.
8. Exhaust a configured queue/log allowance; see bounded backpressure and useful
   recovery steps. Drain, backup, upgrade, simulate failure and restore/rollback
   using runbook; orphan fences prevent replay. Uninstall preserves data by default
   according to accepted P05 contract and honors explicit retention choice.

Usability fails if instructions require undocumented privileges, silent permission
bypass, hidden manual DB edits or author-only knowledge. Record comprehension and
recovery outcomes, not a large subjective UI survey. Technical evidence may be
shared with live cases, but unobserved human steps cannot inherit automated passes.

## 9. Triage, change impact, risks and decisions

### Severity and go/no-go rules

- **Stop immediately / release blocker:** unauthorized execution/disclosure,
  leaked credentials, false completion/acceptance, blind duplicate effect, loss of
  committed state beyond stated evidence boundary, foreign teardown, concurrent
  physical writers, approval bypass or unsafe migration/restore. Isolate affected
  candidate/cell, preserve evidence, stop new effects and repair before rerun.
- **Major / blocker:** any required configuration cannot perform its acceptance
  flow, hard resource bound fails, unexplained crash/leak, missing backup recovery,
  incompatible installed context, critical/high exploitable dependency or license
  prohibition. No full release with these unresolved.
- **Nonblocking:** bounded cosmetic/docs friction or measured low-risk deviation
  not invalidating a required contract. Requires severity rationale, owner,
  explicit risk acceptance, follow-up and P08 visibility; author cannot self-waive.
- **Bug:** product contradicts supported contract. **Unsupported:** capability has
  no verified implementation/protocol. **Blocked:** evidence unavailable due to
  access, budget, reviewer/toolchain or unresolved decision. Required unsupported
  and blocked rows both prevent full exit; neither is a pass or merely a bug to
  hide. `not_applicable` requires predicate-based justification, e.g. terminal
  input in a headless-only scenario, never missing required platform access.

Owner may authorize an explicitly narrower preview with exclusions and updated
scope/dependent plans. It is not P07 full-product qualification under unchanged
F baseline. No silent shrinking of required matrix, sample counts or budgets.

### Evidence invalidation and rerun blast radius

Every repair records affected F/case IDs, code/packages/config/schema and threat
boundaries, old/new candidate hashes and proposed reruns. Independent reviewer
checks impact justification; retain unaffected evidence only by explicit link.

| Changed area | Minimum rerun scope |
| --- | --- |
| SDK/compiler/MCP/native SQLite/common packaging | All three published AOT core/install smokes, relevant JIT parity/performance, dependency/provenance refresh and affected service/live paths. |
| Acceptance/schema/idempotency/claims/recovery | Shared crash matrix, migration/backup/restore and cursor/budget invariants on native platforms; representative real adapter outage tests, expanding to every impacted binding. |
| Identity/grants/bootstrap/path/export policy | All affected roles/platform ACLs and sentinel/race negatives; nested/join/connector revoke tests; service environment and diagnostic redaction where shared. |
| Backend/provider/host protocol | Every affected mode/platform/version cell's live lifecycle, approval/control, foreign input/outage replay and wake; no old provider evidence borrowed. |
| Scheduling/buffers/retention/deadlines | Bounds/soak/performance and crash/backlog/cursor tests; real interaction smoke where scheduling or output timing changed. |
| Installer/config/update/signing payload | Fresh-profile install/doctor/update/rollback/uninstall per impacted platform; package integrity/provenance and actual service/GUI smoke. |
| Documentation only | Links/sanitization plus affected operator walkthrough; no unrelated runtime rerun unless instructions/config semantics changed. |

Final RC receives full applicable lean suite and installed cross-platform smoke
regardless of focused fix reruns. Repeat full soak after resource/lifetime changes;
otherwise justified unchanged soak evidence may be retained. Test/harness changes
also invalidate evidence they produced. Failure clusters return to owning phase
for correction; major architecture/security/delivery redesign needs plan review
before implementation, not a patch disguised as qualification.

### Open decisions and external risks

| Decision / risk | Required disposition and timing |
| --- | --- |
| Accepted predecessor execution artifacts pending | Consume P03 managed-Pi, P05 installed/native Windows and P06 attachment/lead-host/connector evidence before W01; plans alone do not pass entry. |
| Synthesis freeze/evidence pending | P01 freezes measured limits and selected wake cells; P02 event identity remains distinct from P04 recipient cursor/public read-ack. Chosen ownership is resolved, numeric/migration/capability evidence still required. |
| Exact OS builds, macOS architecture/provider and Pi/join/wake cells | Freeze from P01/P03/P05/P06 evidence before W02; access loss blocks, not scope deletion. |
| .NET 11 RC/toolchain/native assets drift | Exact tested pin; prerelease risk and support decision recorded; changes trigger requalification. |
| Drain/backup RPO/RTO, retry windows, memory/soak limits | Numeric workload-specific objectives approved before W04/W05; no invented universal guarantees. |
| Destructive lab/platform/operator/model budget | Explicit approval and reserved access before execution; no real-user state or uncontrolled spending. |
| Power-loss facilities unavailable | Limit durability claims to process/storage experiments actually run; separately approve any stronger hardware claim/testing. |
| License, redistribution/signing and public evidence permissions | Owner/legal decision before release-ready handoff; P08 publication still separately authorized. |
| Independent reviewers or safe native contracts unavailable | Gate blocked; no same-family substitution for required code review or fake live pass. |

Overall effort **L**, confidence **medium-low** until installed platform and P06
contracts are accepted. W01/W07 are bounded M integration/review work; W02–W05
are L risk-bearing work; W06 is M–L with real human/access cost. Sequence remains
W01 → W02 → W03 → W04 → W05 → W06 → W07. CI/native lanes may run independent
cells concurrently after freeze; defects invalidate downstream evidence by impact,
not by schedule convenience. External waits include machines/desktops, SDK/native
prerequisites, vendor contracts, approved credentials/spend and reviewers. No
calendar certainty or hours-scale full-product promise follows from this plan.

### Qualification rollback and safe containment

Disable admission/new dispatch for affected capabilities; keep authorized reads,
diagnostics and recovery available. Preserve state, failed artifacts and journals.
Do not auto-kill live interactive agents or clean uncertain workspaces. Retain
physical writer fences; safely stop only proven-owned work under explicit policy.
Rollback uses compatible binary/schema or verified isolated restore as in W04,
never a status edit or automatic resend. Keep previous supported operator workflow
available without silently switching modes. A reverted candidate still needs
support/evidence consistency; rollback does not erase external effects.

## 10. P08 contracts, exit checklist and validation limits

P07 introduces no new public API/schema by default. Repairs follow owning phase's
versioned migrations/contracts; breaking capability, security or recovery changes
return through explicit change control. Hand P08:

- Immutable final RC binaries/package/source/build manifests and evidence index;
  exact .NET/package/native/CLI/provider/host pins and supported matrix, including
  first-class P03/P05 managed Pi and actual Windows P07-PI01–PI04 proof.
- F/case traceability with pass/fail/blocked/unsupported distinctions and inherited
  evidence links; remaining power-loss, attached lifetime and same-user limits.
- Versioned API/config/schema/capability and supported upgrade-source matrix;
  drained maintenance, backup/restore, orphan fencing, revocation and rollback
  runbooks with measured recovery/data-loss bounds.
- SBOM/dependency/license/notices inputs, advisory/secret scan provenance,
  security findings and accepted nonblocking risk ownership; public sanitization
  manifest distinct from restricted raw evidence.
- Raw comparable performance/resource results and approved claim wording;
  supported-host wake distinguished from passive read; no unmeasured savings.
- Review/fix/re-review records, operator acceptance and explicit owner go/no-go.
  P08 owns authorized signing/publication, final distribution/onboarding checks
  and ongoing compatibility/security lifecycle; this handoff does not pass P08.

### Exit checklist

- [ ] Accepted P01–P06 inputs, independent P07 plan review and frozen decisions exist.
- [ ] P07-W01–W07 complete and P07-G01–G10 supported by final-candidate evidence.
- [ ] All F01–F27 and C01–C32/L01–L12/T01–T19 obligations accounted for, including
  P07-PI01–PI04, nesting, cooperative join, connector and installed T13/T14;
  native Windows Pi in both modes is mandatory, no fake/Linux/cross-build pass.
- [ ] Required three-platform published AOT/MCP/SQLite/service/real-agent cells
  qualified, missing access not waived and preexisting failures not ignored.
- [ ] Security/authority, supply chain/license, crash, WAL restore, migration,
  physical orphan fences, stress/soak and comparable measurement gates satisfied.
- [ ] Real operator/controller-death acceptance and every promised native-wake
  host/platform cell pass; explicit degraded recovery and residual threat/durability
  limits documented. Manual-only behavior cannot waive a required wake gate.
- [ ] All blocking findings resolved; separate opposite-family code review and fix
  re-review complete; major design fixes independently plan-reviewed first.
- [ ] Full applicable format/lint/check, build, tests and published/platform delta
  gates recorded; no unrun required gate or stale RC artifact relabelled as passed.
- [ ] P08 receives immutable bundle, rollback instructions, explicit decision and
  named nonblocking follow-ups; license/publication authority remains explicit.

**Definition of done:** an evidence-backed integrated candidate suitable for P08's
release decision, not merely a written plan or an attractive demonstration.

### Planning-only validation boundary

This authoring task changes only this Markdown plan. Appropriate checks are bounded
UTF-8/readability, final newline/trailing whitespace, Markdown structure, relative
file-link existence, W/G identifiers and C/L/T/F mapping against source definitions.
They cannot establish runtime correctness, complete rendering, independent review,
security verdict, SDK/AOT compatibility or platform support. No code/test/build,
SDK installation, model run/change, Git operation or other-agent invocation is
part of this task. Reproducible documentation check tooling remains an implementation
setup follow-up where absent; planned execution gates above remain unrun.

## F27 scope addition — operator text console

F27 is an additional full-release obligation: qualify status/output freshness and bounds, authenticated follow-up/stop parity, XSS/CSRF/Origin/Host defenses, browser disconnect survival and selected real OS/browser contexts. Static HTML mock behavior does not prove runtime security or backend capability. Map console plan work packages/tests into the same evidence ledger.

See [the console plan and HTML mockup](../../ui/operator-console-plan.md). This explicit
user addition supersedes earlier blanket dashboard exclusions; richer analytics,
remote administration and terminal emulation remain outside scope.
