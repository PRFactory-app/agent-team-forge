# P05 — Cross-platform operator, setup, service and distribution experience

## 1. Objective, scope and status

**Status: draft plan; no implementation, review or platform gate is passed.**
P05 delivers the complete installed operator experience for Linux x64, Windows
x64 and one evidence-selected macOS architecture. It consumes P04's complete
local product, not only the Linux demonstration. Execution order remains
P01 → P02 → P03 → P04 → **P05** → P06 → P07 → P08. Parallel document authorship
does not waive predecessor implementation gates.

Binding inputs: [shared brief](README.md), [product scope](../../product-scope.md),
[roadmap](../../roadmap.md), [contributor policy](../../../AGENTS.md),
[architecture](../../architecture.md), [terminal modes](../../terminal-modes.md),
[PoC](../../poc.md), [execution contracts](../../plan.md), and available preceding
plans: [P01](01-foundation.md), [P02](02-durable-core.md),
[P03](03-managed-agents.md), [P04](04-team-orchestration.md), and
[cross-phase synthesis](contracts.md). The latest managed-Pi requirement places
its first-class backend in P03; P06 adds attachment/join, additional lead hosts
(including Pi lead wake) and the generic opt-in connector, not managed Pi.

The assignment requested GPT-6 Astra, medium effort, one author per phase. This
artifact does not certify runtime model selection or independent review. Planning
authorizes no SDK/system installation, service registration, GUI test, model call,
agent delegation, Git operation or publication. All procedures below are future
implementation and verification obligations, not commands asserted to exist.

### Included and excluded

Own F15–F18 and installed-context portions of F04, F05–F09, F19, F22–F24.
Managed Claude Code, Codex and Pi are mandatory P03 predecessors. P05 qualifies
all three installed lifecycles, including native Windows Pi in a visible selected
terminal and explicitly chosen headless mode; attachment or Pi lead hosting is
not a substitute. Linux runs and Windows cross-builds cannot prove this support. Qualify all
P04 operator operations and their policy/authorization behavior through installed
CLI/MCP clients. Deliver explicit setup, production-context doctor, per-user
service/session lifecycle, configuration compatibility, safe MCP registration,
release-like packages, user-controlled update, drain, rollback and uninstall.

P01 owns early feasibility, selected provider/architecture/OS versions and fatal
platform decisions. P03 owns backend protocols, binding/evidence and control
semantics. P04 owns team features, permissions, budgets, wake and workspace policy.
P05 must not repair missing contracts by inventing terminal transports, weakening
policies or introducing a second scheduler. Return incompatible contracts to their
owner through explicit change control. P06 extends attachment/join, additional
lead-host integration and the generic opt-in connector; P07 performs
cumulative qualification; **P08 owns final public release and distribution approval**.

End-user setup/installation and diagnostics remain the main UI. No dashboard,
custom terminal UI, model loop, cloud service, automatic telemetry, mandatory
connector, arbitrary Desktop attachment or additional production assembly.
Additional architectures/providers are optional decisions, not implied support.

Existing .NET 10 spike results are version-specific and have unresolved review
findings until independently repaired and re-reviewed. They do not establish
.NET 11, Native AOT, installers or Windows/macOS production behavior. Target is
.NET 11; public RC1 candidate `11.0.100-rc.1.26425.128` is research, not installed
ref-pack/package evidence. No SDK provisioning occurs in this planning task.

## 2. Entry criteria and predecessor contracts

P05 implementation requires evidence-backed predecessor exits and independent
review of this major platform/security/persistence plan. Draft filenames do not
satisfy entry. Planning can proceed while other authors work; discrepancies below
remain named integration decisions, not reasons to silently modify their plans.

| Input owner | Required entry artifact and safe-start condition |
| --- | --- |
| P01 | Frozen OS/build/RID/provider matrix; actual early GUI and production-context probes, including T04/T07/T10 and T13/T14 feasibility; user launch identity, logout/lock, credential/TCC and bootstrap decisions. Exact provider and architecture selected from evidence, not popularity. Fatal unknowns block predecessor freeze, not defer to P05. |
| P02 | Published .NET 11/AOT core, singleton/private IPC, principal bootstrap, schema/API compatibility, maintenance interlock and consistent SQLite backup/restore primitives. Restored state quarantines possible post-snapshot effects. CLI/bridge never opens runtime job DB. |
| P03 | Qualified Claude Code/Codex/Pi × interactive/headless tuples, including actual native Windows Pi launch/follow-up/correlated results/interrupt/stop/reconnect, native control/results/approval/interrupt/stop/replay, stable terminal/process identities, environment/bootstrap specification and temporary production-context evidence. Relevant safety findings closed and re-reviewed. |
| P04 | Versioned inventory of every public operator operation, request/result/error fixtures, role/grant policy, nested/team limits, receipts/read-ack, wake capabilities, workspace/artifact retention and diagnostics rules. Maintenance semantics must compose with queued work, approvals and survivors. |
| Owner/test operators | Authorized disposable fresh-user profiles on actual Linux/Windows/macOS desktops; approved backend authentication and later live-test budgets; package/test-signing access where required. No tests against personal working sessions or repositories. |

P01 currently proposes macOS arm64 and Terminal.app first probe, with Windows
Terminal as Windows candidate. P05 treats these as **unqualified candidates until
P01's decision artifacts prove them**. No assumption that a Windows Terminal CLI
returns stable tab IDs, that `.cmd` quoting is safe, or that Terminal.app and iTerm2
have interchangeable APIs. No hardcoded provider selection in installer code.

For installed Windows Pi, pin the actual CLI, `.cmd` wrapper and Node executable
versions and invocation chain. Verify launcher quoting for executable/workspace
paths with spaces and Unicode, prompt newlines/metacharacters, argument boundaries
and no unintended shell expansion. Exercise actual user profile, credentials,
private IPC/bootstrap ACLs, inherited handles, Job Object membership/descendants
and the selected user desktop. Test visible selected terminal binding and explicit
headless separately; wrapper exit or a live Node PID is not native result evidence.
Record same-session native turn correlation, interrupt versus stop and reconnect
with stale-client fencing. Missing native Windows access blocks F19 qualification.

Apply the synthesis before W01: two active turns globally and root depth zero
with at most three delegation edges are proposals pending measured P01 freeze,
not competing P04 defaults. P02 lifecycle event sequence remains distinct from
P04 recipient-delivery sequence/cursor and consumer generation; canonical batch
read/ack belongs to P04. Native wake is standard and enabled by default for
supported hosts. One automatic host suffices only for an early checkpoint;
every promised host/platform cell must pass for full release. Manual read/catch-up
is explicit degraded recovery, never a standard replacement or gate waiver.
No normal watcher, keystroke injection or tight model polling. Backend execution
and Codex member wake do not prove lead-host self-wake.

[PR #70 research](../../research/native-wake-pr70.md) records merge to main
`471a17514d041e09b69cb24b910e418da28d2027` and Windows W1–W3 passes:
native Codex member wake works; W3 proves Claude's safe unsupported response,
not Windows-Claude wake. Reference concept works on Linux and Windows; native
Windows-Claude, macOS and Pi transports remain gaps. Our installed .NET integration
tests remain mandatory. On actual Windows, verify each promised native-wake cell
from the selected user/host context, including bounded I/O/cancellation, authenticated
session/generation binding, restart catch-up and visible degraded recovery.
Unsupported transport blocks its required cell; cross-builds cannot pass it.

Before W01 starts, resolve exact compatibility versions, configuration authority,
maintenance/API handshake, supported OS minimums, package format per RID and
service/GUI boundaries. Unknown required provider control or unavailable GUI
access blocks the affected work and full phase exit. A separately authorized
Linux preview remains a preview, not completed P05.

## 3. Installed runtime contracts

### 3.1 Roles, service ownership and desktop lifecycle

Exactly three production projects: **Host → Business → DAL**. Host owns CLI/setup,
service integration, role composition and local transport. Business validates
configuration, maintenance policy, backend/terminal context and operator use cases;
DAL owns configuration records, migrations and atomic durable transitions.
Business calls DAL directly. Host may reference DAL for composition only. A
restricted desktop launcher is a Host process role, not another production project,
database owner, durable scheduler or privileged administrative proxy.

One daemon per user/machine owns accepted jobs outside lead/bridge and terminal
kill scopes. Per-user installation has an explicit autostart choice separate from
launch mode. Starting setup or an MCP bridge must not silently create a daemon
inside that client's lifetime. Service-manager restart does not mean backend
adoption or replay authority. Preserve singleton locking across install roots,
upgrades, logins and concurrent manual/service starts.

| Platform | Required production contract and verification boundary |
| --- | --- |
| Linux x64 | User systemd daemon independent of Herdr/client teardown; authenticated desktop launcher only if P01 requires it. Pin unit stop/kill/restart behavior so stopping/restarting daemon does not automatically kill interactive TUIs through a shared cgroup. Test user-manager logout and restart. Linger, if needed for explicitly selected headless logout survival, requires separate informed authorization; never silently enable it or escalate privileges. |
| Windows x64 | Use P01-selected per-user logon/service arrangement with correct user profile and desktop-session launcher where required. Record token/session identity and Task Scheduler/service restart behavior. Session 0 is not an interactive desktop: reject unavailable interactive context rather than open invisible UI or impersonate another user. Test Job Object membership and descendant teardown; no interactive kill-on-close default. No LocalSystem/root fallback or hidden elevated task. |
| Selected macOS RID | Use P01-verified user/GUI launch arrangement, such as selected LaunchAgent domain. Record launchd job identity, GUI availability, credential store and provider permissions. Test logout/relogin and agent restart rather than assume LaunchAgent survives logout. TCC/Apple Events permissions and signing identity changes must be exercised for selected provider; Accessibility or scriptability is not assumed. |

Define and test a platform-specific lifecycle table for logged-in/unlocked,
locked, logged-out, no GUI, launcher absent, daemon down, provider down and
relogin states. Default interactive launch is waiting/error whenever selected
session cannot be verified available, including locked sessions; no automatic
unlock, permission-prompt approval or headless fallback. P01 may establish a
narrower verified locked-session behavior only through an explicit reviewed
contract. Existing turns follow their accepted policy and actual platform
survival facts; do not infer failure or completion from lock/logout alone.

GUI loss before an effect leaves genuinely unattempted queued work waiting within
P04 deadlines. GUI loss after an attempt marker requires P03 reconciliation, not
relaunch. Re-login refreshes authenticated launcher registration and GUI-session
generation; stale launcher authority cannot be reused. If multiple logins exist,
require one explicitly selected desktop binding, never choose a different user's
or merely the most recent session. Launcher requests carry scoped launch identity,
expiry and expected daemon/session generation; retries follow the durable operation,
not another terminal spawn. Launcher death cannot transfer job ownership to a client.

Document exact logout effects, restart limits/backoff and operator recovery per
platform. Client-crash survival is mandatory and distinct from daemon restart,
provider death and machine reboot. No blanket promise of uninterrupted logout or
machine-failure execution. Accepted state stays durable and uncertainty visible.

### 3.2 Explicit setup, configuration and context doctor

Setup discovers only supported provider/backend candidates and requires explicit
interactive or headless selection. Linux interactive requires Herdr; Windows/macOS
require their evidence-selected visible providers. Noninteractive setup needs
explicit equivalent parameters; omitted choice rejects before activation. Missing
provider, auth or GUI requests remediation/new explicit choice, never substitution.

Setup stages a validated change, previews affected files/services and retention,
then applies after consent. Persist launch mode, provider identifier/version policy,
backend executable selection, autostart/context choice, state/config schema version
and configuration revision. New defaults affect **new agents only**; existing
agents keep actual mode/provider/session and accepted policy snapshots. A setup
change cannot silently expand accepted permissions or reset budgets/deadlines.

Separate installation/bootstrap configuration from daemon-owned runtime settings:
bootstrap locates executable, private state/endpoint and service identity; daemon
Business operations commit runtime settings through DAL. Bootstrap is a versioned
private file, not a second job database. Record a desired revision and applied
revision; dispatch cannot activate a partially applied configuration. Concurrent
setup uses compare-and-swap revision checks; crash recovery either finishes the
verified staged revision or retains previous effective revision. Unknown newer
config/schema refuses mutation and dispatch, with actionable compatible-binary
recovery. Secrets are referenced through approved private storage, not embedded
in ordinary config exports. Final storage shapes/version numbers follow predecessor
schema review rather than being assigned by this draft.

Doctor executes bounded read-only probes in the **actual intended daemon and
child desktop/headless contexts**, not only the invoking shell. Report separately:

- OS user/session, executable and installation identity, singleton/service state,
  GUI/launcher/provider availability and current configuration revision.
- Trusted resolved backend executable and exact version; PATH search precedence,
  HOME/profile, working directory and backend configuration resolution. Reject
  ambiguous/untrusted discovery rather than execute a repository-local lookalike.
- Explicit environment allowlist for daemon → launcher → backend/helper. Rebuild
  PATH from approved locations; pass only needed locale, home, selected config,
  desktop and credential-store references. Do not copy the entire shell/service
  environment, host messaging tokens or unrelated integration credentials.
- Credential store availability and backend login validity for that context,
  including locked keyring/keychain, expired/revoked authentication, Windows user
  profile resolution and macOS permission denial. Never authenticate as another
  account or put credentials in argv, service definitions, logs or reports.
- Backend/provider compatibility and P03 capability results, approvals and P04
  host wake separately. Version detection/login success is not proof of a live
  turn or automatic wake. Doctor does not spend model tokens by default; an
  explicit separately budgeted live validation can produce separate evidence.

If a credential unlock/login requires human input, give an explicit foreground
remediation path and rerun the context probe. Do not automate credential dialogs,
change backend permission settings or bypass repository trust prompts. Doctor
collects bounded redacted evidence, no full environment dump or transcript.

### 3.3 Bootstrap, discovery and safe targeted control

Reuse P02/P03 authenticated bootstrap: private owner-checked, short-lived,
single-use nonce exchange tied to child/session and daemon epoch. Provider-specific
delivery must work without assumed environment inheritance. Atomically consume
nonce; reject expired/reused/cross-session grants, invalidate on restart, clean
expired owned material safely. No secret argv/log values; private paths alone are
not authentication. Launcher/helper receives least authority, never unrestricted
operator delegation. Filesystem checks cover symlink/reparse substitution and
permissions. Same-user processes with unrestricted file/process access are not
sandboxed by capabilities.

Persist/revalidate provider installation and instance identity, GUI-session
identity, tab/pane binding, backend conversation/turn, process start identity and
generation. Titles, PIDs, reusable names and discovery metadata are not ownership.
Before send/interrupt/stop, use P03's verified binding. Missing per-tab identity
or inability to isolate control blocks that provider; never kill a whole shared
window/server to stop one agent. Existing unrelated tabs, same-named sessions and
other installations must survive setup rollback, stop, update and uninstall.
No keystroke injection, scraping, model self-report or tab silence as control or
completion evidence. Human-wins pause and authorized verified-idle reconciliation
remain unchanged in service contexts.

### 3.4 Complete operator CLI and safe MCP configuration

P04's versioned operation inventory is the acceptance source, not a short demo
command list. Provide CLI coverage for service start/stop/status; team creation,
inspection, close and reconnect where supported; grants/revocation; parent/child
and nested delegation; agent start/send/list and job/result inspection; queue and
policy/budget views/changes; messages, canonical read/ack and filtered inspection;
approvals/questions; turn interrupt; separate agent stop; evidence-bound reconcile;
workspace/artifact/log access and safe retention; host wake/catch-up diagnostics.
Expose every P04 public operator use case even when not separately named here.
P05 adds setup/doctor/config, package/update/drain/backup/restore/uninstall controls.

CLI and MCP use identical authenticated Business operations via IPC. Keep mutation
idempotency, receipts and consumer fencing; no direct SQL, role spoofing, numeric
ack bypass or unrestricted diagnostic RPC. Define versioned structured output,
bounded paging, exit codes and machine-readable blocked/unsupported/uncertain
reasons. Human summaries are compact; terminal control sequences in untrusted
names/output are escaped. Secrets stay out of argv; large instructions use safe
input/files under the approved contract, never shell interpolation. Stdio MCP
stdout remains protocol-only. Dangerous purge/stop/restore requires explicit
intent; daemon stop is not synonymous with stop-all-agents.

MCP registration uses a selected supported host config format/version and scope.
Preview a structural merge, make a private byte-exact backup with digest and
permissions, then write atomically only the owned entry after validation. Preserve
unrelated entries and semantic values; preserve comments/order when format supports
lossless edits, otherwise show rewrite impact and require consent. Malformed,
ambiguous or unsupported config is left unchanged. Existing same-name entry with
different provenance is a conflict, not overwrite authority. Handle paths with
spaces/Unicode using host-native argument arrays, not guessed shell strings.

Registration stores executable/arguments and nonsecret bootstrap references, not
operator credentials. Record install ownership, original values and written digest.
Repeat setup is idempotent. Revert/uninstall removes only unchanged owned values;
if another writer edited them, offer conflict resolution/diff and never overwrite
newer changes with an old whole-file backup. Test concurrent edits and crash between
backup/write/receipt; preserve recoverable partial state and give exact remediation.

### 3.5 Packages, maintenance, rollback and retention

Produce reproducible, versioned per-user packages for `linux-x64`, `win-x64` and
P01-selected `osx-arm64` or `osx-x64`. Pin supported OS/native ABI minimums and
external terminal/backend prerequisites. Actual installed Native AOT binaries must
exercise MCP registration/tool calls, IPC, SQLite, native loading and process paths
on each RID; cross-publish and single-file output alone prove none of these.
Capture SDK/ref-pack/compiler/package locks, source/build identity, checksums,
native dependencies (including SQLite), license/notice inventory and analyzed
trim/AOT warnings. Self-contained does not mean no native or external dependencies.
No silent SDK install, JIT fallback, backend upgrade or dependency elevation.

Select package format with P01 evidence and owner input. Test Windows signature
verification and OS warnings, macOS signing/notarization/quarantine/Gatekeeper and
TCC identity continuity where distribution requires them, and Linux package/archive
provenance and native-library resolution. Package metadata and staged artifacts
need a trusted integrity/signature mechanism, not an unauthenticated checksum next
to a download. Actual release-channel signing/notarization verification is owed
where required; absent credentials blocks that distribution gate. Unsigned local
artifacts can provide labelled engineering evidence only. P08 controls final
identity/license/publication, signing custody and public channel activation.

Updates are user initiated; automatic checking is opt-in and never automatic
application or model spending. Preview version/schema/host/provider implications,
size, required downtime and rollback range. Do not silently update backend CLIs.

1. Fetch/stage from an explicitly trusted source; verify package provenance,
   RID/OS, native assets, compatibility and space before changing active files.
2. Enter P02/P04 durable maintenance: reject new execution/delegation admissions,
   pause queued dispatch and new wake-triggered work; keep reads, acknowledgments
   and authorized approval/recovery/interrupt controls available as safe. Preserve
   queued jobs, cursors, reservations and absolute deadlines. Persist maintenance
   across crashes so restart cannot accidentally resume scheduling.
3. Let active work drain under accepted policy. Show bounded wait and blockers;
   timeout offers abort/defer or separately authorized targeted stop, never
   automatically killing interactive TUIs. Existing agents may stay live only if
   verified control/rebind compatibility permits; uncertain survivors block the
   switch or require explicit maintenance quarantine. New code cannot bypass
   their physical ownership claims.
4. With dispatch disabled and one daemon owner, take a SQLite-consistent backup
   using P02 primitives plus required configuration, file-reference and ownership
   manifest. Verify integrity/readability and disk space. Copying a live main DB
   without WAL is not backup. Keep old package and recovery instructions private.
5. Activate staged package atomically using platform-tested locked-file handling;
   migrate through daemon maintenance mode under singleton lock, not client SQL.
   Test interruption before/after switch, migration and health confirmation.
   A failed migration cannot publish a half-working service or fresh empty DB.
6. Recheck daemon/bridge/launcher protocol compatibility, schema, context doctor,
   provider/backend credentials and ownership. Resume only on explicit successful
   health decision; post-switch uncertainty keeps admission/dispatch blocked.
   Old bridges either negotiate safely or fail with actionable upgrade instructions.

Rollback binary only within declared schema/config compatibility. Never edit a
schema version or let an old binary mutate unknown state. Incompatible rollback
uses verified backup restoration into isolated state with active daemon stopped,
newer state/evidence retained and survivors inventoried. Restored acceptance rows
may predate external effects: quarantine before dispatch and reconcile; restoring
files cannot undo external commands or recover post-backup events automatically.
State loss since backup must be shown and explicitly accepted. Prefer fix-forward
when safe rollback cannot be established.

Uninstall first previews exact owned resources, drains or defers, disables owned
autostart/launcher registration, revokes product capabilities and closes only
proven-owned resources under explicit stop intent. If live/uncertain work still
needs binaries/evidence, defer removal or retain recovery components; never strand
it by deleting state. Remove only owned package/MCP entries. Default retain DB,
results/artifacts/logs, configuration, backups and workspaces, subject to already
accepted retention rules; inventory retained locations privately to the operator.
Separate explicit choices cover product-data purge and safe owned-workspace
cleanup. Dirty/user-owned repositories, backend installations, backend credentials
and unrelated terminals are not uninstall targets. Do not promise secure erasure.
Purge refuses active/uncertain ownership and path escapes; revoke authentication
before leaving retained recovery data. Reinstall verifies compatibility and requires
fresh authorization, never silently reactivating old grants or uncertain jobs.

## 4. Ordered vertical work packages

H/B/D means Host/Business/DAL. Each package delivers observable behavior through
only needed projects, tests and public operator instructions. Critical behavior
uses focused red → green → refactor, real temporary SQLite/files and C# scenario
harnesses with deterministic crash barriers. No prose/format snapshot assertions,
private-class tests or arbitrary coverage target.

| Package | Observable result and deliverables | H/B/D touchpoints | Preceding dependency and meaningful tests | Size / confidence |
| --- | --- | --- | --- | --- |
| P05-W01 | Explicit setup persists a validated versioned profile; context doctor explains readiness without spending tokens. Deliver support/context manifest and staged-config contract. | H setup/doctor; B context validation/config activation; D revision/CAS and migration. | P01–P04 exits and plan review; omitted mode, unknown version, stale revision, crash during activation, wrong HOME/PATH/auth, allowlist and secret-redaction tests. | M / medium |
| P05-W02 | Linux installed user service launches real Herdr sessions outside client/service teardown hazards; headless explicitly selectable. Deliver unit/launcher lifecycle and logout runbook. | H user-service/launcher roles; B context/binding policy; D applied installation/session registration. | W01; singleton races, stale launcher/nonce, cgroup stop behavior, logged-out/locked waits, T04/T07/T10/T13/T14 and neighbor safety on actual Linux. | L / medium |
| P05-W03 | Windows selected per-user/desktop arrangement opens correct user's visible Claude Code/Codex/Pi tabs and handles Session 0/profile loss safely; qualifies mandatory installed Pi lifecycle in both selected modes. | H selected autostart/GUI integration; B provider/context checks; D identity/revision records. | W02 and P01 Windows evidence; real Windows Pi `.cmd`/Node launcher quoting, Unicode paths/argv, profile/credentials/ACL/token/session/Job Object tests; spawn/follow-up/correlated native results/interrupt versus stop/reconnect, duplicate start and provider failure; no system fallback. | L / medium-low |
| P05-W04 | Selected macOS user/GUI launch path operates real tabs with verified permission and credential context. | H launch registration; B selected provider/TCC/context; D session/config records. | W03 and P01 macOS decision; real GUI TCC grant/deny/revoke, keychain lock, logout/relogin, targeted stop and no duplicate launcher. | L / medium-low |
| P05-W05 | Every P04 operator operation available through bounded CLI; MCP config install/revert preserves unrelated data. Deliver operation parity and host-config compatibility tables. | H CLI/output/config merge; B existing use cases and merge policy; D configuration ownership/change receipt metadata. | W04; authorization/receipt/idempotency parity, escaping, conflicting/concurrent config edit, failed atomic write, backup/revert and no client DB access. | M / medium |
| P05-W06 | Fresh user installs provenance-verified release-like native package without build toolchain or hidden dependencies. | H package/setup entry points; B prerequisite/capability checks; D initial state compatibility. | W05; actual per-RID AOT MCP/IPC/SQLite/process runs, native-library absence, tampered/wrong-RID package refusal, signing/quarantine and no-admin clean-profile checks. | L / medium-low |
| P05-W07 | Operator safely stages, drains, updates, migrates, health-checks and rolls back without duplicate work. Deliver compatibility window and recovery recipe. | H maintenance/update UX/service switch; B drain/health/ownership; D maintenance state, migration/backup primitives and update receipt. | W06; crash at each switch/migration boundary, disk full, busy binary, stale bridge, active approval/TUI, incompatible downgrade and restored-writer quarantine. | L / medium-low |
| P05-W08 | Uninstall/reinstall has explicit retention and conflict-aware cleanup; unrelated resources survive. | H uninstall preview/consent; B ownership/revocation/retention; D retained-state/cleanup records. | W07; dirty workspace, symlink/reparse escape, live survivor, edited MCP entry, retained-data reinstall, separate purge authorization and interrupted uninstall. | M / medium |
| P05-W09 | Actual fresh-user lifecycle UAT and full installed-context evidence accepted for all required platforms/modes. Deliver operator guide, manifest and P06/P07/P08 handoff. | H published commands; B full feature/context behavior; D persisted recovery/version checks. | W08; all gates, real GUI/backend/host tests, opposite-family review and fixes/re-review, applicable full checks. | L / medium-low |

## 5. Verification gates and traceability

All gates **planned/unrun**. Future evidence must identify exact command/recipe,
binary/package/source digest, OS/RID, provider/backend/host versions, mode, service
and GUI identity class, config/schema revision, expected/observed results and
sanitized logs. Record `passed`, `failed`, `blocked`, `unrun` or reasoned
`not_applicable`; no blanket case pass from a partial mapping. Recipes and harness
commands are deliverables, not asserted existing tools.

| Gate | Reproducible verification and exact success criterion | Evidence class / blockers |
| --- | --- | --- |
| P05-G01 | Audit predecessor manifests and independent plan review; replay selected compatibility fixtures. Every required platform/provider/context has early feasibility evidence, every P04 operation has CLI mapping, and all blocking entry decisions resolved. | Documentary plus predecessor evidence, not new runtime pass. Missing provider decision, prior safety closure or review blocks. |
| P05-G02 | Run setup/config/doctor tests and actual service-context probes with missing choice/provider/auth, hostile PATH and locked credentials. No implicit mode/provider, unauthorized environment value or secret leak; effective revision survives restart and active bindings remain unchanged. | Real files/SQLite and actual intended contexts; T01–T03/T12, C25/C29/C30. Shell-only doctor cannot pass. |
| P05-G03 | Install per-user daemon/launcher and run T04/T07/T10 plus T13/T14 on each actual desktop platform with Claude Code/Codex and supplemental Pi cases below. Locked/logged-out/Session-0 cases return declared wait/error; relogin reauthenticates selected session; one daemon/launcher binding, no neighbor teardown or blind replay. | Real GUI/service/manual evidence mandatory, not temporary units alone or headless CI. Missing platform, credential context or actual launcher test blocks full exit. |
| P05-G04 | Execute complete installed T01–T19 matrix below for each selected interactive backend/provider. Follow-up reaches same conversation, human activity pauses automation, interrupt preserves tab, separate stop is targeted, outage completion is replayed or explicitly reconciled under approved capability. | Live backend/human/provider plus deterministic failure fixtures. Unavailable mandatory control remains blocked, not accepted unsupported success. |
| P05-G05 | Compare every P04 operation through CLI/MCP against the same authorized Business result using versioned fixtures; exercise actual installed controls/read-ack/reconnect. All inventory rows covered; denied cross-team/stale receipts cause no leak/effect, no client opens DB, structured results bounded. | Real IPC/SQLite; C01–C03/C11–C12/C21–C25/C31–C32 and P04 feature negatives. Fake parity complements actual production execution. |
| P05-G06 | Merge/repeat/revert MCP registrations for every selected host format; inject concurrent edit, corruption, disk failure and interrupted write. Unrelated semantic content unchanged, original bytes recoverable, conflicts stop mutation, only unchanged owned values removed. | Real filesystem fixtures and actual installed host initialize/list/call smoke. Unsupported parser/host version or missing backup recovery blocks that registration claim. |
| P05-G07 | Build/package and install on fresh standard-user profiles without SDK; run published Native AOT MCP/IPC/SQLite/native-process paths and both launch modes for all three backends, including native Windows Pi. Reject altered/unsupported packages. All baseline RIDs load declared assets; every warning analyzed; required signatures/quarantine behavior verified. | Real native OS/package evidence; C26/C27/C30, L08/L09/L12 portions. Cross-build/JIT/unsigned local smoke cannot pass missing AOT or distribution-trust paths. |
| P05-G08 | Upgrade known prior supported fixture to candidate under active/queued/approval/uncertain states; crash each maintenance/switch/migration boundary; test downgrade refusal and isolated restore. Preserve accepted identities, unread receipts, limits and ownership; zero blind replay, incompatible schema mutation or auto-killed TUI. | Real SQLite/files/service/package fault runs; C07–C10/C13–C16/C19/C30/C31, T10/T17/T19. Missing consistent backup or physical safety blocks switch/resume. |
| P05-G09 | Run uninstall/reinstall with default retention, explicit purge, foreign/dirty workspaces, edited config and interrupted removal. Only authorized owned resources change, retained data stays readable by compatible recovery, credentials revoked, unknown survivors block destructive cleanup. | Real standard-user installs on each OS plus adversarial path fixtures. Privilege fallback, orphaned recovery dependency or neighbor deletion blocks. |
| P05-G10 | Witness section 6 fresh-user UAT on each OS in both modes; rerun relevant live L cases and every promised P04 native-wake host/platform cell in installed context, including Windows. Native wake defaults on; manual recovery cannot pass a wake cell. Every required lifecycle step and P05-PI01–PI04 passes, including actual Windows Pi in both modes; negative outcomes match contracts, no mandatory cell omitted. | Real human GUI/backend evidence; no model calls without separate budget authorization. Inaccessible desktop or required signing/auth path is blocked, not waived. |
| P05-G11 | Run applicable format/lint/check, Release build/analyzers, tests, native publish/run and document checks; separate opposite-family code review and fix re-review; parent accepts versioned handoff. Zero unresolved blocking findings and all G01–G10 passed. | Future implementation tooling must provide exact reproducible commands. Missing reviewer/platform/gate blocks full P05 completion. No public release approval implied. |

### Terminal case applicability — no omitted T cases

Historical C/L/T IDs and original Claude/Codex case meanings remain unchanged.
Apply their relevant assertions to Pi through P05-PI01–PI04 below, without
renaming T04 or claiming that its original two-backend run covers Pi. Shared setup
checks may reuse one profile-level run if manifests prove identical code/context;
backend-specific controls run separately for all three backends. Headless has separate
L/C coverage; lack of visible-tab behavior there is explicitly not applicable, not
a way to skip interactive requirements.

| Cases | P05 installed-context obligation | Gate |
| --- | --- | --- |
| T01/T02/T03 | Fresh interactive/noninteractive setup without choice rejects; persisted choice survives daemon restart; missing provider or GUI has no fallback. | G02/G03 |
| T04 | Both real TUIs launched from installed production daemon/desktop context, visibly usable, not log tails. | G03/G04 |
| T05/T06 | Human types real instruction; machine follow-up later reaches same verified conversation after required reconciliation. | G04 |
| T07 | Kill actual lead and MCP bridge, not only test client; daemon/agent tabs continue outside their kill scope; new authorized client reads results. | G03/G04 |
| T08 | Turn interrupt preserves intended TUI; separately authorized stop affects only intended agent with neighboring unrelated tabs/windows open. | G04 |
| T09 | Close tab and separately kill provider server; honest interruption/uncertainty, no invented completion or automatic rerun. | G04 |
| T10 | Restart installed daemon with live tabs, including service-manager stop behavior; no auto-kill, verified rebind or quarantined recovery, no duplicate spawn. | G03/G04/G08 |
| T11 | Unicode/newlines/metacharacters intact through each real provider/bootstrap/backend and CLI path; no shell interpretation. | G04/G05/G07 |
| T12 | Change defaults with agents active; only new agents use new mode, old accepted policies and bindings retained. | G02/G04 |
| T13/T14 | Actual installed autostart/service in logged-in desktop opens correct user's terminal; no GUI/lock/logout/Session-0 cannot launch elsewhere or headless. | G03/G10 |
| T15/T16 | Concurrent human/machine activity persists human-wins pause; text/model self-report/silence never completes job. | G04 |
| T17 | Complete during installed-daemon outage; replay authoritative bound evidence or expose reconciliation; no false zero-loss claim. | G04/G08 |
| T18 | Approval with disconnected lead remains observable blocked; no credential/permission shortcut in service context. | G04/G10 |
| T19 | Lost start response retried through fresh installed client returns same operation/binding, not another tab/process. | G04/G08 |

T04/T07/T10 must run in production contexts on **each platform**, even if P01/P03
passed temporary-launch probes. T13/T14 require installed service/autostart evidence.
Fresh-user onboarding extends these cases with absent backend/provider, fresh
permissions, credential login, shell-versus-service environment differences,
package quarantine, update, uninstall and retained-data reinstall.

Reuse L01–L06 in installed Linux contexts, including L03's three successful
real-lead crash repetitions per mode. L08/L12 cover actual Windows/macOS native
core smoke; L09 requires both real headless backends on Windows/macOS; L10/L11
cover real interactive flows. L07 and P04's separate named-host wake scenarios
are rerun for installed supported contexts, not inferred from MCP notifications.
Use P01's promised host/platform matrix: every native-wake cell must pass in
installed context, including actual Windows. One evidenced host is only an early
checkpoint, not full P05 qualification. Historical L07 remains Claude-specific;
Codex and Pi lead-host claims require separate evidence in their owning phases.
Manual catch-up demonstrates explicit degraded recovery, never a native-wake pass.
P02/P04 unchanged C cases retain referenced evidence; changed maintenance,
identity or transport paths require reruns. P07 owns final cumulative C01–C32
and whole-product qualification; P05's partial C mapping is not full coverage.

### Supplemental managed-Pi cases (F05–F09, F19)

These cases add the third backend without changing historical C/L/T identities.
All are planned/unrun. Run PI01–PI03 on Linux x64, Windows x64 and the selected
macOS RID in both explicitly selected modes, with GUI assertions applying to
interactive mode; PI04 adds Windows-specific obligations. Each ledger row records its
platform/mode and installed package/native CLI/provider versions; native Windows
rows are mandatory, never satisfied by Linux or cross-build evidence.

| Case | Installed assertion and historical analogues (not replacements) | Gates |
| --- | --- | --- |
| P05-PI01 | Spawn managed Pi, accept first job, retrieve run-correlated authoritative native result and send same-session follow-up; lost start/response returns same binding, not another process/tab. Exercise approvals where exposed, including a disconnected lead; absent safe capability blocks the promised cell, never bypasses policy. Analogues C01/C08/C09/C23, L01/L02/L09 and T04/T06/T18/T19. | G03/G04/G07/G10 |
| P05-PI02 | Kill actual lead/bridge and reconnect with fresh authorized client; recover result and reject stale authority. Repeat three successful client-crash runs per mode. Separately restart daemon during completion: verified replay/rebind or explicit quarantine, no duplicate work or automatic TUI kill. Analogues C13–C17/C32, L03/L06 and T07/T09/T10/T17. | G03/G04/G08/G10 |
| P05-PI03 | Genuine human Pi TUI input pauses automation until authorized verified idle; native correlated evidence alone completes work. Interrupt preserves intended session/tab; separate authorized stop verifies owned descendants gone and unrelated tabs intact. Explicit headless uses its frozen control contract. Analogues C21/C22, L04 and T05/T08/T15/T16. | G04/G10 |
| P05-PI04 | On actual Windows, exercise installed Pi .cmd/Node chain, quoting/Unicode/argument fidelity, trusted discovery, profile/credentials, ACLs, inherited handles, Job Objects and selected user desktop. Visible selected terminal and explicit headless each pass PI01–PI03. Lock/logout/Session 0/absent GUI and revoked credentials fail safely, no fallback or other-user launch. Analogues C25/C27/C29 and T01–T03/T11–T14. | G02/G03/G04/G07/G10 |

## 6. Fresh-user operator UAT

Future execution requires explicit platform/system-change and live-model budget
authorization. Use disposable standard-user accounts, harmless repositories and
predetermined permissions. Test fresh accounts, not only developer profiles with
preexisting PATH, backend auth, terminal permissions or SDK installations.

1. Verify package provenance and prerequisites, install without elevation or SDK,
   then attempt setup without choice. Show refusal, preview changes and explicitly
   choose interactive provider/autostart. Show absent provider/auth remediation
   without installing alternatives or changing security settings silently.
2. Complete user login/permission prompts manually. Run doctor from service and
   desktop child contexts; compare approved identity/config resolution with the
   invoking shell. Lock credentials or revoke permission and show bounded failure.
3. Launch real Claude Code, Codex and managed Pi TUIs; type in a tab, observe human-wins pause,
   reconcile verified idle, then submit machine follow-up. Open unrelated neighbor
   tabs. Interrupt one turn, separately stop one agent, verify neighbors unchanged.
4. Exercise complete operator inventory using multiple/nested teams, grants,
   messages/read-ack, approvals, policies/budgets and workspace/artifact inspection.
   Deny cross-team/stale-consumer operations. Show default native idle wake for
   every promised installed host/platform cell without watcher, keystrokes or
   tight model polling. Inject failure and demonstrate explicit degraded manual
   catch-up separately; notice success is neither read/ack nor model action.
5. Kill lead/bridge; retrieve results with new authorized client. Restart daemon
   separately with live tabs and outage completion. Show authoritative replay or
   explicit blocked reconciliation and zero duplicate starts on retry.
6. Lock/logout/relogin, lose launcher/provider, and request interactive work without
   GUI. Show declared wait/error and stable accepted identities; reconnect to the
   selected authenticated desktop only. No other-user launch or headless fallback.
7. Change explicit default to headless while an interactive agent remains bound;
   new agents use headless and old TUI remains unchanged. Repeat first/follow-up,
   correlated native result, approval where exposed, client-loss/reconnect and
   interrupt versus stop paths for all three backends. Run P05-PI04 on actual
   Windows, including .cmd/Node quoting, Unicode, profile/credentials, ACLs,
   Job Objects and selected user desktop; do not infer it from another backend.
8. Merge MCP registration beside unrelated entries, edit it externally, rerun/revert
   and show conflict-safe behavior. Inspect redacted diagnostic export and logs.
9. Stage update with queued work, active approval and TUI; drain/defer safely.
   Exercise interrupted migration and incompatible downgrade refusal; recover via
   verified backup quarantine, never replay old external effects. Show compatibility
   checks and healthy intended context before explicit resume.
10. Uninstall with default data retention, show untouched backend credentials,
    dirty workspaces and unrelated tabs/config. Reinstall compatible version with
    fresh authority and uncertain work still blocked. Separately demonstrate
    explicitly authorized safe purge on disposable owned data only.

Record observations and failures per OS/backend/mode/provider/host, not a single
"cross-platform passed" checkbox. Raw credentials, prompts, paths and GUI captures
need separate sanitization before publication; public evidence uses normalized IDs.

## 7. Downstream handoff contracts

| Consumer | Contract and ownership/security/version impact | Required verification artifact |
| --- | --- | --- |
| P06 integrations | Versioned setup extension/capability hooks, config revisions, intended-context discovery/auth and restricted launcher protocol. Attachment/join, additional lead hosts (including Pi lead wake) and connector paths use the same operator/API policy, never inherit managed survival or authority by name. Managed Pi/F19 already arrives from P03 and is installed-qualified here, not deferred to P06. Disabled connector stays local-only. | Installed environment manifest, normalized CLI/API/config fixtures, bootstrap negatives and per-context capability matrix; new integration cells remain P06 tests. |
| P07 qualification | Frozen package/RID/OS/provider/backend/host matrix, lifecycle table, maintenance/drain and retained-data semantics, schema/config compatibility window, physical quarantine and no-blind-replay guarantees. | G01–G11 ledger, T01–T19/L applicability and P05-PI01–PI04 with native Windows Pi proof, reproducible crash/upgrade fixtures, native dependency and signature evidence, fresh-user UAT and remaining nonblocking risks. |
| P08 release/operations | Reproducible package recipes, trusted update verification, signing/notarization requirements, retention/uninstall/rollback runbooks and documented support limits. Public release, license choice, signing custody and distribution channel activation remain owner/P08 decisions. | Staged artifact hashes/inventory/notices, tested installation/recovery instructions, signing test identity/provenance and explicit unfulfilled public-release requirements. |

Add schema only for proven installation/configuration/maintenance receipts missing
from predecessors; use the same DAL migration stream. Version bootstrap, IPC,
launcher, CLI structured output and config separately; negotiate compatible clients
and reject unsafe unknown versions. No migration fabricates terminal identity,
rewrites active mode, resets policy or treats restored queued rows as fresh intent.
Verification bundle is public/self-contained and sanitized, not dependent on raw
private machine logs. Parent owns index/roadmap changes and final cross-phase
reconciliation; this author changes only this file.

## 8. Decisions, risks, stop/go and safe recovery

| Decision / deadline | Required resolution and owner | Stop/rollback rule |
| --- | --- | --- |
| D05-01: before W01 | P01/parent supply tested OS minimums, macOS RID/provider, Windows provider, production launcher and lock/logout tables. Candidates are not selected facts. | Missing native control/identity or GUI access blocks; return fatal feasibility issue to P01, never improvise transport. |
| D05-02: before W01/W05 | P02/P04 owners agree config authority/revisions, maintenance admission and allowed recovery controls, CLI inventory and required wake-host list. Apply synthesis ownership: P01 freezes measured limits (two active/depth three proposed), P02 event sequence is not P04 recipient cursor, and every promised native-wake host/platform cell requires evidence; one host is only an early checkpoint, never full-release coverage. No P05 policy override or manual-only waiver. | Contract mismatch blocks affected slice and G01; versioned change and downstream review required. |
| D05-03: before W06 | Owner selects package format/trust roots/signing access and supported host-config formats from platform evidence. P08 retains public identity/license/channel decisions. | Unsigned/internal artifacts labelled only; missing required trust/platform tests block distribution qualification. No certificate purchase or system change authorized by plan. |
| D05-04: before W07 | P02/parent freeze supported upgrade/rollback range, backup file coverage, free-space limits and recovery/resume authority. | No unsafe migration or downgrade. Preserve newer state and fix-forward if compatible restore/physical safety cannot be proven. |
| D05-05: before W08 | Owner approves default retained resource categories and explicit purge/reinstall authority semantics; P04 retention policies remain binding. | Uncertain ownership or dirty/foreign paths block removal; never broaden cleanup to make uninstall succeed. |

Main risks: desktop identity differs from service identity; provider updates break
stable binding; locked stores/TCC/signature changes invalidate credentials;
service-manager teardown kills terminal-owned work; package/native ABI drift;
concurrent config writers; interrupted update with surviving writers; partial
uninstall leaves recovery unavailable. Mitigations are corresponding negative
tests, pinned compatibility and conservative maintenance, not operational folklore.

**Go** only when all required platform/mode gates and reviewed handoff pass.
**Stop** on unsafe teardown, missing auth/origin, duplicate effects, unknown schema,
secret exposure, implicit privilege/mode fallback or absent required evidence.
Changed scope/guarantees require owner decision, affected F IDs, migration/security
impact and downstream reruns. Major design changes need independent plan review;
all implementation needs opposite-family code review and re-review of fixes.
Unavailable required reviewer is blocked, never same-family substitution or
self-approval. No review occurs during this planning-only assignment.

Safe failure leaves previous compatible installation available where possible,
new admission/dispatch paused, durable state and unresolved evidence retained,
live interactive TUIs untouched and recovery instructions actionable. Restore
only proven-owned configuration/resources. Aborting setup/update does not cancel
accepted jobs; it follows accepted policy and reports actual survivors. Automatic
service restart must respect durable maintenance/quarantine rather than erase it.

## 9. Size, sequencing and definition of done

Overall **L effort, medium-low confidence**. W01/W05/W08 are relatively bounded;
three platform integrations, native distribution and crash-safe upgrades dominate.
P01 evidence should retire fatal feasibility unknowns early, but does not remove
production packaging/UAT effort. W01 → W02 → W03 → W04 → W05 → W06 → W07 → W08 →
W09 is the dependency order; platform fixtures may be prepared in advance against
frozen contracts, not counted as a passed successor gate. No calendar or few-hours
full-product promise.

External waits: actual GUI machines and fresh accounts, supported provider/backend
versions, user authentication/permission interaction, approved test budget, native
build/signing/notarization prerequisites and independent reviewers. More authors
or headless CI cannot replace these environments.

### Exit checklist

- [ ] P01–P04 evidence and contract differences resolved; independent P05 plan
  review and implementation authorization recorded separately from this draft.
- [ ] W01–W09 delivered as feature-first slices within three production projects;
  daemon remains sole durable runtime owner and clients use IPC.
- [ ] G01–G11 passed for Linux x64, Windows x64 and selected macOS RID; actual
  GUI/service T04/T07/T10/T13/T14 and complete T01–T19 applicability recorded;
  P05-PI01–PI04 pass, including native Windows Pi in both selected modes.
- [ ] Full P04 CLI operation parity, safe MCP merge/backups/revert and service-context
  doctor proven, with negative authorization/config/environment tests.
- [ ] Fresh-user install/run/update/rollback/uninstall/reinstall for all three backends
  in both launch modes verified from installed Native AOT packages, not only development runs.
- [ ] Configuration/schema/protocol compatibility, lifecycle, maintenance,
  retention and physical reconciliation documented with recovery fixtures.
- [ ] Applicable format/lint/check/build/test/published platform gates pass;
  opposite-family code review and fix re-review complete; blockers closed.
- [ ] Sanitized artifacts and accepted nonblocking follow-ups handed to P06/P07/P08;
  parent receives unresolved public-release decisions. No P08 release claim.

**Definition of done:** a reviewed, evidence-backed complete three-platform
installed operator experience, safe under declared failure/upgrade boundaries,
ready for P06 integration and P07 cumulative qualification. Writing this plan alone
does not meet that definition.

### Planning artifact validation boundary

Only this Markdown file receives bounded UTF-8/newline/trailing-whitespace,
heading/fence, ordered work-package/gate definition and relative local-link checks.
C/L/T references are checked against source case IDs. These checks cannot prove
runtime behavior, GUI support, package/signature validity, independent approval or
rendering in every Markdown tool. Runtime format/lint/build/AOT/tests and all live
UAT remain unrun. Reproducible document tooling should be added during implementation
setup if absent; no tooling or other file is added in this planning task.
