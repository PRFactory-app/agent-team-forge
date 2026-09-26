# P08 — Public release and sustained operations

## 1. Objective, scope and status

**Status: draft plan, not release authorization or passed acceptance.** Deliver a
public, installable, maintainable AgentTeamForge release after P07 qualification:
reviewed source and notices, reproducible native packages, truthful onboarding,
reversible deployment, and an operational owner. Primary features are **F18,
F25 and F26**; release acceptance covers **F01–F26**, not only these three.

Follow the [shared planning brief](README.md), [product scope](../../product-scope.md),
[roadmap](../../roadmap.md), [architecture](../../architecture.md),
[contributor policy](../../../AGENTS.md), [handoff](../../../HANDOFF.md) and
[cross-phase synthesis](contracts.md). Managed spawned Pi is first-class mandatory
P03 scope alongside Claude Code/Codex, including native Windows execution; P05
qualifies installed contexts. P06 extends attachment/join, additional lead-host
integration (including Pi lead wake) and the generic opt-in connector, not the
managed Pi backend.
[The implementation plan](../../plan.md), [PoC cases](../../poc.md) and
[terminal contract](../../terminal-modes.md) define safety semantics reused below.
No planning document, earlier review or successful spike is proof of a released
product. P05–P07 handoffs must arrive as accepted evidence, whether their plans
are authored concurrently or later; this plan does not invent their gate IDs.

P08 finishes distribution and operational acceptance of P05 packaging and P07
qualification. It does not rebuild those subsystems, replace system testing with
document checks, or add a dashboard, telemetry, mandatory remote service,
distributed scheduler or custom model loop. Exactly three production projects
remain: **Host → Business → DAL**. Documentation and release metadata require no
new production assembly.

Current evidence is exploratory. The [interactive spike README](../../../spikes/m0-interactive/README.md)
explicitly excludes raw `evidence/`, `.run/`, terminal captures and transcripts
from public clearance. Its ignore rules exclude runtime/build files but not all
raw evidence. Earlier .NET 10/Linux observations do not establish .NET 11, Native
AOT, Windows or macOS support, and unresolved spike safety findings require
independent re-review before promotion.

This task writes this plan only. It does not initialize Git, create branches or
worktrees, publish, select a license, provision signing identities, handle
credentials, install software, change code or spawn agents. Requested planning
profile is GPT-6 Astra/medium, one author per step; that request is not evidence
of runtime model selection. Independent review remains a separate future gate.

## 2. Entry criteria and predecessor inputs

Document preparation may occur in parallel. Release execution starts only when:

1. **P01–P06 mandatory gates have accepted evidence and P07 signs off its release
   candidate.** Here, “signed off” means recorded reviewer and owner acceptance
   with scope, date, snapshot and unresolved findings; it does not mean a
   cryptographic signature. No blocking correctness/security findings remain.
2. P07 supplies a candidate manifest: source snapshot, package hashes, exact
   SDK/toolchain/native dependency versions, RIDs, backend/provider/host matrix,
   API/config/schema versions, applicable C/L/T results, security and resource
   evidence, acceptance records, limitations and approved nonblocking follow-ups.
   Sanitized reports reference locally retained raw evidence without publishing it.
3. P05 supplies actual package/install/service definitions, user-context paths,
   config merge rules, supported upgrade paths, backup/restore and uninstall
   procedures, including P05-PI01–PI04 installed Pi evidence. P03 supplies managed
   Claude Code/Codex/Pi lifecycle contracts, including native Windows Pi; P07
   supplies integrated P07-PI01–PI04 proof. P06 supplies attachment/join, additional
   lead hosts (including Pi lead wake) and generic opt-in connector contracts,
   consent/revocation behavior and compatibility evidence. Missing handoffs block.
4. The owner authorizes the release work and supplies decisions in W01. Platform
   access, real desktop sessions, backend accounts and bounded model-test spend
   are separately authorized. No consent is inferred from writing this plan.
5. This major release/security/lifecycle plan receives independent review.
   Subsequent implementation uses opposite-family code review, with fixes
   re-reviewed, per contributor policy. The author cannot self-approve.

A candidate change invalidates affected evidence. Packaging-only changes still
need packaging/install checks; runtime, dependency, SDK, schema or capability
changes return to the owning phase and P07 for impact-based requalification.
The release owner records why any retained evidence still applies.

Use the synthesis-selected contracts, not obsolete claims of unresolved conflict:
P01 freezes measured limits (two active turns globally and root depth zero with
at most three delegation edges remain proposal inputs until frozen). P02 global
event sequence is distinct from P04 recipient-delivery sequence/cursor and public
canonical read/ack. Native wake is standard and enabled by default for supported
hosts. One automatic host is enough only for an early checkpoint, not full release:
every promised host/platform native-wake cell must pass. Manual read/catch-up is
explicit degraded recovery, never a standard replacement or manual-only waiver.
No normal watcher, keystroke injection or tight model polling. Managed Pi support
never proves Pi lead wake; Codex member wake never proves Codex-lead self-wake.
Freeze and runtime evidence remain prerequisites, not passes from synthesis prose.

[PR #70 research](../../research/native-wake-pr70.md) records merge to main
`471a17514d041e09b69cb24b910e418da28d2027` and Windows W1–W3 passes.
Reference concept works on Linux and Windows through native Codex member wake;
W3 proves Claude's safe unsupported response, not Windows-Claude wake.
Windows-Claude, macOS and Pi native transports remain gaps. Our published .NET
integration and final-package tests are still required; upstream evidence alone
cannot establish public support or close any required cell.

## 3. Ordered vertical work packages

Execute packages in order. Sizes are relative effort, not calendar commitments;
confidence describes planning certainty, not test success. A named real person
must accept each owner role before execution; one maintainer may hold several.
All tests below are proposed acceptance work, not commands or gates already run.

### P08-W01 — Accept candidate and make owner decisions

- **Depends on:** accepted P07 handoff and Section 2. **Size M; confidence medium.**
- **Observable result:** owner-approved release charter ties intended claims to
  an exact candidate, support matrix and explicit publication permissions.
- **Touchpoints:** Host package/CLI/service identity; Business capability and
  lifecycle limits; DAL schema/read-write compatibility. No code change implied.
- **Deliverables:** name/CLI/package-ID collision check and owner confirmation of
  AgentTeamForge branding, provisional `atf` name and public namespace; license
  choice after provenance review; public destination and channel decisions;
  maintainer, security contact, reviewer and release approver; patch/support
  commitments, signing choice and .NET servicing policy. Do not select these on
  the owner's behalf. Resolve redistribution rights for bundled components.
- **Tests:** reconcile candidate hashes and F01–F26 ledger against P07; reject a
  missing required platform/capability or unsigned-off review, not just mark it
  unsupported. Record unresolved decisions as blockers with an owner.

### P08-W02 — Prepare a disclosure-safe public source boundary

- **Depends on:** W01 authorization and license/name decisions.
  **Size M; confidence medium.**
- **Observable result:** public source can be built without private material and
  contains only owner-approved disclosures.
- **Touchpoints:** Host config/diagnostic samples, Business backend examples,
  DAL test data/migration fixtures; no new runtime behavior.
- **Deliverables:** reviewed source inventory, `.gitignore`, publication allowlist,
  license/notices/attribution inventory and sanitized fixtures. Exclude raw
  `.run/`, experiment `evidence/`, DB/WAL/SHM files, backend transcripts, sessions,
  captures, environment dumps, user config, credentials, caches, build output,
  logs and runtime worktrees by default. Publish reviewed summaries/fixtures
  through a distinct allowlist, not a blanket exception for an evidence tree.
  Ignoring a file neither untracks it nor removes it from history.
- **Procedure:** scan current files including ignored/untracked material and
  generated release inputs for secrets, personal/private paths or source,
  licensing problems and unauthorized third-party copies. Review fixture origin
  and attribution manually; synthetic examples must not reproduce raw prompts or
  identifiers. Preserve required source notices and upstream license texts;
  rewritten code is not automatically free of attribution obligations.
- **Repository action, only once separately authorized:** if Git is absent,
  prepare a sanitized staging snapshot, inspect its exact file manifest, then
  initialize the agreed base branch from approved files only. If Git already
  exists, audit it first; do not reinitialize or silently rewrite history. Use
  feature branches and dedicated worktrees for major changes after initialization.
  Authorize public repository creation and each initial publication separately.
- **Tests:** scan the proposed commit/tree, every reachable branch/tag/history
  object, and relevant local reflogs/unreachable objects for accidental retention;
  audit remote refs before publication where present. Inspect source archives,
  package content, debug paths and CI logs too. Future commits, tags, dependency
  updates and releases repeat current-tree/history scans, with human review of
  findings and bounded documented suppressions. A clean scan is not proof of no
  secret. A finding stops publication; revoke exposed credentials first, then
  obtain consent for cleanup/history rewriting and re-scan all publication refs.

### P08-W03 — Produce reproducible, attributable native packages

- **Depends on:** W02 source boundary and W01 support baseline.
  **Size L; confidence medium; native toolchain/platform access is external wait.**
- **Observable result:** Linux x64, Windows x64 and one explicitly selected,
  verified macOS architecture have release-ready native artifacts.
- **Touchpoints:** Host publish/installer/service assets; Business backend and
  terminal compatibility metadata; DAL migrations and native SQLite packaging.
- **Deliverables:** locked .NET 11 SDK, package/dependency locks, build instructions,
  OS/toolchain/native linker versions and minimum OS/libc requirements; per-RID
  SBOM including native and transitive dependencies, notices and build provenance.
  Record source digest/commit, recipe, builder, inputs, output digests and test
  evidence. A source checksum before authorized Git use can identify a candidate;
  Git initialization must not fabricate prior history.
- **Tests:** independent clean builds per supported RID compare unsigned payload
  hashes. Investigate nondeterminism; do not call differing payloads reproducible.
  Explicitly separate timestamped signatures/notarization envelopes from payload
  comparison and retain their final artifact hashes. Exercise actual Native AOT
  binaries with MCP registration/serialization, local IPC, SQLite/native loading,
  process/terminal control and intended service context on each real OS. A
  cross-build or `dotnet run` is insufficient. Check archive traversal, symlinks,
  executable modes and install permissions. SBOM must describe shipped contents,
  not merely the declared dependency graph.
- **Toolchain policy:** existing research names .NET 11 RC1 candidate
  `11.0.100-rc.1.26425.128`; verify current official release/support information at
  release time. Do not install or retarget experiments through this plan. Default
  stable-channel gate requires an owner-approved supported GA toolchain. If .NET
  11 remains prerelease, a separately approved prerelease-channel go-live records
  SDK/native security and servicing risks, support horizon, pinned inputs and
  migration owner; it does not silently qualify as stable full-product release.
  RC-to-GA or servicing changes require rebuild, AOT/native tests and affected P07
  requalification. Do not describe .NET 11 as LTS without official evidence.

### P08-W04 — Stage artifacts and establish consumer verification

- **Depends on:** W03; W01 distribution/signing consent.
  **Size M; confidence medium.**
- **Observable result:** a user obtains the intended candidate through a documented
  channel and can reject corrupted, substituted or unsupported packages.
- **Touchpoints:** Host install/update verification; Business compatibility checks;
  DAL version requirements included in release manifest. Reuse P05 mechanisms.
- **Deliverables:** immutable versioned candidate downloads, manifest, checksums,
  SBOM/provenance, notices, known limitations and installation instructions.
  Owner selects hosting and any package-manager feeds; extra feeds are not
  mandatory. Define preview, candidate and stable channel semantics, retention
  and withdrawal behavior. No overwrite of a released version with new bytes.
- **Integrity versus authenticity:** checksum comparison detects differing bytes
  but does not prove publisher identity when package and checksum share a
  compromised origin. Document the trusted download origin and its limitations.
  Optional signatures require explicit identity/account/key consent, trust-root
  distribution, protected key storage, rotation/revocation and verification
  instructions. Platform signing/notarization required by a chosen distribution
  channel becomes a blocking channel requirement; unsigned alternatives must be
  explicitly selected and warn accurately, never advise bypassing OS security.
- **Tests:** download from the staged channel and compare final artifact identity
  to the manifest. Corrupt a package, alter manifest metadata and supply an
  untrusted/expired/revoked signature where signing is used; verification must
  reject as defined. Check wrong RID/version and downgrade refusal. Promotion
  moves the exact tested package bytes: source revision alone is not identity.
  Final signing/notarization occurs before clean-profile qualification; any later
  package change creates a new candidate requiring affected gates again.

### P08-W05 — Prove public onboarding and examples from clean profiles

- **Depends on:** W04 staged final artifacts.
  **Size L; confidence medium; real desktop/backend access is external wait.**
- **Observable result:** a new operator succeeds using only public instructions,
  downloaded packages and declared prerequisites, without developer caches or
  another project's source.
- **Touchpoints:** Host install/setup/doctor/CLI/MCP; Business team/agent operations,
  provider selection and permissions; DAL persisted config, jobs and read/ack.
- **Deliverables:** quickstart, full setup guide, CLI/MCP registration reference,
  safe example workspace, architecture/contributor guide, support matrix and
  troubleshooting. All command examples use implemented names verified during
  execution; `atf` remains provisional until W01. Explain model cost, backend
  login, permissions, local data locations and user-context service startup.
- **Tests:** on fresh standard-user profiles on real Linux, Windows and selected
  macOS hardware/VM architecture with actual GUI access, obtain W04 downloads;
  verify, install, explicitly choose interactive provider, run doctor in actual
  daemon/desktop context, register MCP and complete Section 6. Repeat explicit
  headless onboarding for all three managed backends. Actual Windows Pi must pass
  spawned lifecycle, same-session follow-up, correlated native results, interrupt
  versus stop and reconnect, with .cmd/Node launcher quoting, Unicode paths/argv,
  user profile/credentials, ACLs, inherited handles/Job Objects and selected
  user-desktop tests. Visible selected terminal and explicit headless are separate
  required cells; Linux tests, cross-builds, attachment or Pi lead hosting do not
  establish Windows managed Pi support. No silent fallback or root/system default. Record the
  profile isolation, prerequisites, installed versions, package digest and
  observer evidence; developer-machine success does not qualify.
- **Negative cases:** absent backend/authentication/GUI, wrong provider version,
  denied desktop automation, incompatible client, insufficient file permission,
  and conflicting existing config produce bounded actionable failure without
  overwriting user choices, leaking credentials or launching another mode.
  Connector disabled means no connection/export. Diagnostics remain local and
  redacted; generating a preview/export file needs explicit operator action and
  sending it needs separate consent. No automatic telemetry or issue uploads.
- **Evidence boundary:** P07 crash/security/load results may be reused only for
  identical applicable inputs. W05 is a new consumer-channel, clean-profile
  smoke, not a renamed P07 run. Sample correctness is tested by executing
  meaningful paths, not asserting prose strings or imposing coverage quotas.

### P08-W06 — Rehearse upgrade, rollback, uninstall and restore

- **Depends on:** W05 and accepted P05/P07 lifecycle contracts.
  **Size L; confidence medium.**
- **Observable result:** supported version transitions preserve policy and data;
  failures cannot run an old binary over an incompatible newer DB or surviving
  new-version processes.
- **Touchpoints:** Host update/drain/service/MCP/config lifecycle; Business
  admission closure, ownership, approvals and reconciliation; DAL online backup,
  schema compatibility and migration transactions.
- **Deliverables:** explicit source→target binary/API/config/schema matrix,
  reversible and irreversible migration classification, backup/restore recipe,
  rollback decision tree, retention choices and uninstall/reinstall instructions.
  An initial release may test a preserved qualified predecessor candidate and
  incompatible-schema fixture, labelled as such, not claim an imaginary prior GA.
- **Tests on every required real platform:** verify package, close new admissions
  and autonomous dispatch, drain within a declared timeout, resolve or retain
  approvals/uncertain attempts, and inventory all owned runners/helpers/terminals.
  Never kill a live interactive TUI automatically. If safe physical exclusivity
  cannot be proven, leave it live, quarantine its session/workspace and block the
  transition. Obtain explicit permission for any targeted termination.
- **Safe transition:** create and validate a consistent SQLite backup through
  the supported WAL-aware mechanism, plus config/schema/credential-reference
  metadata and an inventory of separately retained files. Do not copy a live DB
  file alone. Preserve private permissions. Stop/fence old owners; migrate and
  start only a compatible single daemon, then run doctor/MCP/job smoke. Crash
  before/after migration and simulate full disk/backup failure; failed prerequisites
  stop before destructive change.
- **Rollback:** stop new admissions and reconcile/quiesce new-version processes
  first, including descendants. Use old binary on current state only if explicit
  read/write compatibility was tested; otherwise restore matching binary, config
  and consistent backup to an isolated recovery location before controlled
  activation. Never overwrite the only newer state. Fence ownership generations,
  revalidate credentials and reconcile effects since backup; database restore
  cannot undo external file/API effects or authorize replay. Refuse automatic
  restart while any old/new runner's physical ownership is uncertain. If no safe
  downgrade exists, stay stopped/quarantined and forward-fix.
- **Uninstall acceptance:** preview affected services, registrations and owned
  files; retain data by default, require explicit deletion choice. Remove only
  owned service/MCP entries, not shared backends, user workspaces or foreign
  terminals. Verify no owned autostart remains, no stale authority grants access,
  retained backup restores with compatible version, and reinstall restores
  intended config without reviving uncertain jobs. Revocation is not undone by
  restoring old credential material.

### P08-W07 — Exercise local operations and security response

- **Depends on:** W06. **Size M; confidence medium.**
- **Observable result:** a maintainer/operator resolves incidents using public
  runbooks without raw transcript disclosure or unsafe process adoption.
- **Touchpoints:** Host local status/doctor/export; Business ownership,
  reconciliation, budgets and credential policies; DAL backup, retention and
  evidence-bound transitions. Fix gaps in owning slices, not a second scheduler.
- **Deliverables:** compact handbook for service startup/logout/reboot, bounded
  logs/storage, disk-full, output pressure, stuck approval, lost IPC, unknown
  delivery, backend outage, daemon crash, quarantine, backup verification and
  recovery. Explain client-crash survival versus daemon reconciliation versus
  backend/machine recovery; no exactly-once external effects or PID-only adoption.
- **Tests:** tabletop plus focused real-binary drills from Section 6. Human/foreign
  activity pauses automation; authoritative correlated evidence or verified idle
  is required before authorized reconciliation. Preserve live interactive TUIs;
  fenced DB updates alone do not prevent an orphan writing files. Separate turn
  interrupt from stop and distinguish advisory spending limits from enforcement.
- **Security operations:** inventory credential scopes and storage without values;
  test rotation and revocation for local client/agent capabilities, attachment,
  connector pairing and optional release keys. Old credentials must fail,
  reconnect must reauthorize, and restore must not resurrect revoked grants.
  Backend credential rotation uses the backend's documented procedure, not a
  guessed internal API. Compromised channels stop promotion and issue warnings.
- **Privacy/intake:** public issue template requests version/RID/mode/provider,
  minimal sanitized reproduction and optional reviewed export; prohibit tokens,
  raw DBs and transcripts. Provide a tested private security-report channel and
  fallback, triage owner, severity policy, advisory process, affected-version and
  mitigation fields. Owner chooses realistic acknowledgement/patch targets before
  launch; do not invent response SLAs. Test report receipt and redaction with
  synthetic secrets. Consent to file an issue is not consent to publish raw logs.

### P08-W08 — Establish maintainable compatibility and patch policy

- **Depends on:** W07. **Size M; confidence medium.**
- **Observable result:** an upstream release or security report has a bounded
  decision path, regression check and accountable maintainer.
- **Touchpoints:** Host compatibility/doctor and package updates; Business pinned
  backend/terminal/host capabilities; DAL version compatibility and migrations.
- **Deliverables:** published matrix with exact tested versions or justified
  tested ranges: OS/RID, launch context, backend/version (Claude, Codex, Pi),
  interactive/headless, terminal/version, control/result transport, MCP host,
  wake, attachment/join and connector contract. Mark supported, experimental,
  unsupported and blocked distinctly; required blocked rows prevent full release.
  Include minimum OS/native deps, schema/API limits, last test and evidence IDs.
  Publish mandatory managed Pi/F19 separately from optional deployment of the
  connector and from P06 attachment/lead-host capabilities. Native Windows Pi
  rows cite actual launcher, user-desktop and lifecycle proof, not Linux evidence.
  Every promised host/platform wake row cites native idle-wake evidence with wake
  enabled by default; manual read/catch-up is documented only as explicit degraded
  recovery. Unsupported/refused transport blocks required support, not a pass.
- **Policy:** pin reproducible test inputs; unknown backend/terminal versions fail
  capability validation safely or require a documented limited mode, never
  silently gain support. Owner sets supported release lines, update cadence,
  security patch/backport horizon, EOL notices and deprecation lead time based on
  capacity and upstream servicing. No claim of indefinite maintenance.
- **Tests:** canary selected upstream updates in disposable profiles/workspaces
  before promoting support. Run protocol contract smoke, real turn/follow-up,
  approvals, authoritative completion, interrupt/stop, foreign activity and
  reconnect/ownership checks for affected combinations; include host wake and
  connector-off/offline cases where affected. Synthetic contracts catch framing
  changes but cannot prove real terminal behavior. Keep pinned known-good builds
  available; on upstream regression quarantine affected capability, warn users,
  file a sanitized upstream report and requalify a fix. Do not auto-downgrade
  backend sessions or reuse unsafe versions merely to make tests green.
- **Patch contract:** every hotfix has a new immutable version, provenance/SBOM
  delta, impact-based tests and opposite-family review. Emergency urgency does
  not waive ownership/security tests. Major contract changes return through plan
  review and affected phase gates. Track accepted nonblocking debt with owner,
  next review trigger and explicit user-visible limitation.

### P08-W09 — Run staged release, stop/go and public verification

- **Depends on:** W08; all applicable gates before public exposure below.
  **Size M; confidence medium; publication authorization is external wait.**
- **Observable result:** approved users receive tested bytes with honest scope,
  or promotion stops with a recoverable state and clear notice.
- **Touchpoints:** Host channel/package identity and onboarding; Business
  advertised capabilities; DAL declared migration/rollback compatibility.
- **Deliverables:** release checklist, manifest, owner decision and changelog with
  features, breaking changes, security fixes, migration instructions, support
  limits and rollback route. Stage sequence: private candidate → explicitly
  authorized labelled preview/canary → stable promotion → post-download check.
  Each public step requires owner approval, source/license/disclosure clearance,
  exact artifact hashes and runnable recovery; no accidental publish from CI.
- **Tests:** select canary users/platforms, bounded duration/workload and stop
  conditions before inviting use. Collect feedback by explicit local report,
  not telemetry. Any corruption, data loss, unauthorized action, ownership breach,
  unsupported required combination or artifact mismatch stops promotion. Less
  severe issues need recorded owner disposition; silence is not acceptance.
- **Promotion:** compare candidate, canary, installed and final-channel digests,
  including signatures/envelopes. Do not rebuild to change a channel label.
  After authorization, download published artifacts again, verify identity and
  repeat install/doctor/MCP/basic-job smoke per platform from clean profiles.
  Channel mismatch means withdrawal, investigation and a new candidate; do not
  silently replace bytes. Withdrawal halts new installs/updates and announces
  affected versions; it does not remotely kill users' local jobs.

### P08-W10 — Transfer maintainership and close release

- **Depends on:** W09 and all release gates. **Size S; confidence medium.**
- **Observable result:** a real owner can reproduce release and incident work;
  full-product status follows evidence, not the existence of this document.
- **Touchpoints:** Host release/service instructions; Business safety/capability
  contracts; DAL versioned migration/restore responsibilities. No added layer.
- **Deliverables/tests:** owner rehearses locating source/build evidence, verifying
  downloads, processing one synthetic incident, restoring a backup and preparing
  a no-publication patch candidate. Record access responsibility without secrets,
  recovery of publishing authority, delegation/absence policy and unresolved
  follow-up owners. A single-maintainer project may state limited availability;
  absent maintainer acceptance blocks a claim of sustained supported operation.
  Complete Section 9; hand versioned contracts to future maintenance releases.

## 4. Contracts and feature traceability handed to maintenance

Release manifest and evidence index bind source, final package digests, build
inputs, notices/SBOM/provenance, test environments, support matrix and owner
acceptance. Store raw evidence privately under bounded retention; public
summaries must be independently understandable without local personal paths.

| Contract handed forward | Required invariant / verification artifact |
| --- | --- |
| Distribution identity | Immutable version/RID/channel mapping; exact installed bytes match approved manifest; checksum and optional signature trust model stated. W03/W04/W09 records. |
| API/config/schema | Explicit compatible readers/writers, client handshake, migrations and restore sets; unknown newer schema refused. W06 transition matrix and negative results. |
| Lifecycle/ownership | Daemon owns accepted work; clients do not open DB; committed intent precedes effects; uncertain delivery not blindly replayed; no PID-only adoption or automatic interactive teardown. P07 acceptance plus W05–W07 smoke. |
| Capabilities | Pinned backend/mode/provider/host contracts, human pause, attachment consent, optional connector export/offline policy; no implicit support expansion. W08 matrix. |
| Security/privacy | Local redacted diagnostics, explicit export/send, credential revocation survives restore, reviewed publication boundary. W02/W07 reports. |
| Maintenance | Named owner, issue/security routes, supported lines, patch/EOL/deprecation policy, canary and release procedure. W08/W10 accepted handbook. |

Whole-release checklist below consumes P07 qualification; it is not a replacement
for complete phase evidence. Every row requires a release-manifest reference and
pass/blocked disposition. A partial C/L/T mapping never implies full coverage.

| Features | Required release check and P08 responsibility |
| --- | --- |
| F01–F04 | Durable independent daemon, atomic intent/results, authenticated IPC/MCP, version/bounds enforcement. Reuse P07 fault evidence; W05/W06 verify installed-client separation and compatibility. |
| F05–F09 | Claude Code/Codex/Pi managed lifecycle, true interactive and explicit headless modes, owned targeting, client survival and honest recovery. W05/W07 exercise actual packages; no inherited spike approval. |
| F10–F14 | Multi-team/nested permissions, durable messages/read-ack, default native wake for every promised host/platform cell and explicit degraded catch-up, budgets/approvals and workspace/artifact bounds. W05 examples plus P07 policy/resource evidence. |
| F15–F17 | Operator CLI, setup/doctor, intended user/desktop service contexts on all required platforms. W05 clean-profile results. |
| **F18** | W03–W06/W09: packages, safe config merge, verification, install/update/drain/rollback/uninstall/restore. G03–G06/G09. |
| F19 | First-class P03 spawned managed Pi, P05 installed qualification and P07-PI01–PI04 integrated evidence, including mandatory native Windows launch/follow-up/correlated native results/interrupt versus stop/reconnect in both selected modes. W05/W08 and P08-PI01–PI03 verify final packages; Linux/cross-build/attach-only/Pi lead-host evidence cannot substitute. |
| F20, F21 | Consented supported attach/join and opt-in generic connector. W05/W08 examples and declared combinations; P06/P07 acceptance mandatory, disabled connector causes no contact. Additional Pi lead-host wake belongs to F12/P06, separate from F19. |
| F22–F24 | WAL-safe backup, restore/migration compatibility, security and .NET 11 reproducible Native AOT with native assets. W02/W03/W06/W07 and P07 qualification; JIT-only evidence insufficient. |
| **F25** | W01–W05/W09/W10: owner-selected license, notices, public source/docs/examples/artifacts, exact support matrix and contributor guide. G01–G05/G09–G11. |
| **F26** | W06–W10: recovery/operations, measured resource report, upgrade regressions, compatibility/patch policy and owner handoff. G06–G11. No unmeasured efficiency claims. |

## 5. Acceptance gates

All gates remain **planned/unrun**. Each execution record includes input hashes,
versions, platform/context, reproducible recipe, expected/observed outcome,
reviewer, limitations and sanitized artifact references. Proposed scripts are not
claimed to exist. Missing access blocks rather than waives a required gate.

| Gate | Reproducible method and exact success criterion | Evidence type and blockers |
| --- | --- | --- |
| **P08-G01 — Entry and authority** | Reconcile P01–P07 acceptance and all 26 feature rows with candidate; record owner license/name/channel/support decisions and independent plan review. Every mandatory predecessor gate accepted; no blocking finding or unowned required decision. | Documentary review, not runtime or cryptographic signing. Missing P07 acceptance/owner consent blocks. |
| **P08-G02 — Public disclosure and rights** | Execute W02 current-tree/full-history/package/log scans and manual origin/license review against exact proposed public inventory; approved fixtures only, all findings resolved or justified false positives, required notices present. Repeat before publication. | Scan plus human review. Ignore rules or cleaned prose alone fail; inaccessible history, unclear rights or credential exposure blocks. |
| **P08-G03 — Reproducible native supply chain** | Two clean builds per required RID reproduce unsigned payload; final artifacts match SBOM/provenance and notices. Actual AOT/MCP/IPC/SQLite/process smoke passes on each OS. Apply W03 SDK/GA policy. | Real Linux x64, Windows x64, selected macOS architecture; synthetic protocol checks supplement. Cross-build/JIT/spike-only evidence fails. |
| **P08-G04 — Distribution verification** | Fetch W04 staged final packages; verify manifest identity and checksum; validate selected signing trust policy. Tampered/wrong-RID/incompatible versions and invalid signatures when used are rejected before activation. | Actual channel/package tests; controlled tampering fixtures. Unconsented identities, absent channel-required signing or unknown trust model blocks. |
| **P08-G05 — New-user onboarding** | W05/Section 6 executed solely from public docs and staged final packages on clean standard-user profiles, both declared modes and required combinations. Team/MCP/results and default native idle wake work in every promised host/platform cell; manual recovery cannot pass wake. Negative setup cases fail safely, no implicit mode change or data export. | New real-platform/GUI/backend evidence, not P07 relabelling. Relevant T01–T19 and L01–L12 retain historical meanings; P08-PI01–PI03 add managed Pi final-package/native Windows proof. Missing native Windows Pi or another required combination blocks full release. |
| **P08-G06 — Lifecycle safety** | Execute W06 transition/failure matrix per OS, validate matching backup restore and uninstall/reinstall. No unsafe admission, concurrent old/new owner, old-binary/new-schema access, revoked-authority revival, or unconfirmed replay. | Real installed binaries and SQLite; injected disk/crash/version cases. Reuse P07 deep fault results only for unchanged inputs. Unsafe/untested rollback blocks. |
| **P08-G07 — Operations and privacy** | Run W07 recovery/rotation/export/security-report drills. Authorized reconciliation respects physical ownership; revoked credentials fail; synthetic secrets absent from default diagnostics and reviewed export; test report reaches named contact. | Real binary drills plus tabletop credential/key incident steps. C08–C11, C15, C21, C24, C30–C32 and T10/T15–T19 are relevant subsets, not full qualification. Missing contact or automatic upload blocks. |
| **P08-G08 — Compatibility maintenance** | W08 matrix has evidence for every required row; run canary contract and real control smoke for all three backends, including actual Windows Pi launcher/lifecycle after affected updates, simulate upstream regression and route a patch decision. Support/EOL/deprecation commitments accepted by owner. | Fake protocol regressions plus real backend/terminal/host checks. Unknown versions never silently supported; no test access/maintainer blocks required claims. |
| **P08-G09 — Promotion and post-release identity** | Owner approves staged stop/go; hashes of qualified, canary and promoted artifacts equal; fresh public downloads pass verification and clean-profile install/doctor/MCP/job smoke on each OS. | Actual consumer channels and platforms. No publication authorization means blocked, not simulated pass; any byte change returns to candidate qualification. |
| **P08-G10 — Maintainer handoff** | W10 owner rehearsal locates/reproduces release inputs, handles synthetic incident and verifies recovery; all required policy/access responsibilities accepted, nonblocking follow-ups assigned. | Practical dry run and owner acceptance. Empty role names or aspirational SLAs fail. |
| **P08-G11 — Full-product closure** | Review all P01–P08 mandatory gates, F01–F26 ledger, accepted reviews/re-reviews, public docs/links, final artifact identities and limitations. All mandatory gates pass; no unresolved blocking finding; owner explicitly approves full-product designation. | Cumulative evidence, not a new runtime claim. Any mandatory gap restricts designation to an explicitly scoped preview. |

### Supplemental Pi release cases — unchanged historical C/L/T identities

Original C/L/T cases, including Claude/Codex-only L cases and T04, retain their
IDs, backend meanings and repetition requirements. They do not become Pi tests
by renaming a report. These additional planned/unrun cases consume P03/P05/P07
proof and add P08 final-channel evidence; they do not replace deep qualification.

| Case | Final-package release assertion | Gates |
| --- | --- | --- |
| P08-PI01 | Fresh-profile installed managed Pi spawn, first correlated authoritative native result and same-session follow-up on each baseline OS in visible selected interactive terminal and explicit headless. Exercise approvals where exposed with a disconnected lead, human-wins pause, native turn interrupt versus separate targeted stop and unrelated-neighbor safety. Complements L01/L02/L04/L09, T04–T06/T08/T15/T16/T18; consumes P05/P07 PI01/PI03. | G05/G08/G09/G11 |
| P08-PI02 | On actual Windows final downloads, verify .cmd/Node launcher chain and versions, quoting/Unicode paths/argv, profile/credentials, ACL/private bootstrap, inherited handles/Job Objects/descendants and selected user desktop. Run PI01 and PI03 in both modes. No GUI/lock/logout/Session 0 and revoked credentials produce declared safe outcomes, no invisible TUI or fallback. Complements C25/C27/C29 and T01–T03/T11–T14; consumes P05/P07 PI04. Linux/cross-build evidence cannot pass. | G03/G05/G08/G09/G11 |
| P08-PI03 | Kill actual lead/bridge and reconnect through fresh authorized client to Pi result and same session; stale client cannot act. Separately rehearse daemon-loss replay or fenced reconciliation, no duplicate spawn/blind resend or auto-killed TUI. Preserve managed-Pi ownership during update/rollback. Complements C08–C10/C13–C16/C32, L03/L06 and T07/T09/T10/T17/T19; consumes P05/P07 PI02 and retains their required repetition evidence. | G05/G06/G07/G09/G11 |

## 6. User and operator acceptance demonstration

Use harmless synthetic tasks in isolated example workspaces. Explicitly approve
live model spending and destructive fault tests. Capture versioned outcomes,
not raw prompts/session transcripts. Repeat applicable steps across supported
backend/mode/provider/host combinations for Claude Code, Codex and managed Pi;
one backend is not proof of another. P08-PI01–PI03 require actual Windows Pi
proof in both selected modes; attachment or lead-host Pi is insufficient.

1. New user downloads/verifies candidate, installs without elevated daemon
   privileges, chooses real interactive mode (Herdr on Linux, selected visible
   terminals on Windows/macOS), authenticates backend through its own flow, and
   runs doctor in intended service/desktop context. Separately choose headless;
   absent GUI must not make that choice for the user.
2. Register thin MCP bridge in a supported host, inspect authenticated identity,
   create two isolated teams, authorize a bounded child/nested delegation, and
   submit a small job with idempotency key. Check status, approval wait/deny/allow,
   authoritative result and artifact; wrong-team control fails. Read then ack
   messages; reconnect does not skip unread events. Witness default native idle
   wake in every promised host/platform cell without watcher, keystrokes or tight
   model polling. Test bounded coalescing/retry and restart catch-up; notice success
   is neither read/ack nor model action. Demonstrate explicit degraded manual
   recovery after failure separately. One host is only an early checkpoint;
   failed or unsupported required wake blocks full release, never manual-only support.
3. Kill the lead/bridge, not the independently owned daemon. Accepted work stays
   within policy; new client retrieves committed result. Duplicate same-key request
   finds same operation; changed payload conflicts. Follow-up reaches same
   conversation. Observe real human TUI input, foreign-activity pause, authorized
   verified-idle reconciliation, targeted turn interrupt and separate stop. No
   text marker, model self-report or terminal silence establishes completion.
4. Run public managed-Pi examples through P08-PI01–PI03, including actual Windows
   .cmd/Node/user-desktop tests and native correlated lifecycle results. Separately
   follow supported attach/join and additional lead-host examples with explicit
   consent, scoped credentials and revocation; Pi lead wake needs its own evidence.
   Verify default local-only operation before
   enabling an optional connector against a documented public contract fixture;
   exercise approved integration with its real supported counterpart when claimed.
   Disable/revoke and verify no further contact/export or foreign ownership.
5. Inject daemon loss during a bounded job; inspect recovery/quarantine and leave
   uncertain delivery unreplayed. Preserve surviving interactive TUI until consent
   and verified evidence permit action. Use P07 deep C01–C32 evidence for remaining
   failure windows, rather than reenacting the entire qualification matrix here.
6. Follow W06 backup/drain/upgrade/rollback and uninstall/reinstall/restore paths.
   Show refusal with incompatible schema or a live uncertain owner. Demonstrate
   config merge without clobbering user changes and prove backup restoration does
   not replay external effects or reinstate revoked authorization.
7. Trigger a safe doctor failure and create local diagnostics with synthetic
   secrets. Inspect redacted output before explicit export; nothing is sent by
   default. Submit only a sanitized synthetic issue/security report through the
   chosen channels, then rehearse upstream regression and patch preparation.

Documentation tests target these observable contracts and executable examples,
plus links, flags/config keys, package names and version consistency. Avoid
assertions on individual prose/UI strings, incidental formatting or numeric
coverage quotas. Runtime fixes require risk-driven TDD, applicable format/lint,
build/test/published-binary checks and opposite-family review; doc checks alone
cannot approve them.

## 7. Decisions, risks and stop/go controls

| Open decision or risk | Accountable role and resolution | Stop/go / safe fallback |
| --- | --- | --- |
| License, public namespace, final CLI/package names and repository/channel authorization | Product owner in W01, with origin/rights evidence from W02. No choice is made here. | No public source/package until approved; keep staging private. |
| Real release maintainer, security route, supported lines and response capacity | Owner names actual person and accepts W07–W10 handbook. | No supported-release claim with unowned maintenance; limited preview remains labelled. |
| macOS architecture/provider and exact OS/backend/terminal/host combinations | Consume P01/P03/P05/P06 decisions and P07 evidence, including mandatory native Windows managed Pi distinct from Pi lead wake. | Missing Windows/macOS GUI access blocks full release, never deletes requirement. |
| .NET 11 prerelease/GA, AOT/native vulnerability or servicing gap | Release owner verifies current upstream support, accepts W03 policy and requalification impact. | Hold stable promotion; separately consented prerelease preview only, or return to scope/design decision. |
| Signing/notarization identity and channel requirements | Owner selects channel/trust model and authorizes identity/key use separately. | Do not create credentials or bypass OS checks; change channel only by visible decision. |
| Secret/private-source/license finding in current files or history | Maintainer stops publication, revokes exposed credentials, assesses disclosure and gets consent for remediation. | No “fixed by ignore rule” closure; re-scan source/history/artifacts and issue advisory if already exposed. |
| Backup cannot reconcile side effects or unsafe old/new ownership | Operator retains both states, stops admission and quarantines affected workspaces. | No automatic DB overwrite, replay or downgrade; forward-fix if safe restore is impossible. |
| Upstream backend/terminal changes after qualification | Compatibility maintainer runs W08 canary and updates matrix only after evidence. | Freeze support promotion, warn and isolate affected capability; unknown protocol is not success. |
| Package mutation or compromised channel after canary | Release owner compares digests, withdraws channel/version and invokes key/credential incident runbook. | New candidate and affected requalification; never replace bytes under an existing version. |
| New runtime/schema/security change during P08 | Owning phase records affected F IDs, migration and evidence impact; independent plan review if major. | Return to relevant phase/P07, then resume P08. Earlier acceptance is not a waiver. |

Stage decisions are recorded, not implicit. Before public preview: publication
rights/privacy/integrity, safety and recovery gates apply even if feature scope
is narrower. Before stable promotion: all mandatory prerequisite gates through
G08 pass for full baseline. After publication: G09–G11 and owner acceptance close
P08; a failed post-download smoke requires withdrawal/notice rather than a
premature “full product” declaration. Nonblocking follow-ups need owner and
trigger; release-blocking safety findings cannot be deferred as maintenance.

## 8. Relative estimate and sequencing assumptions

Overall **L**, medium-to-low confidence until P07 artifacts, real platform access
and owner decisions exist. W03/W05/W06 dominate technical work; source-origin
review or upstream regressions can dominate elapsed time. W01/W02/W04/W07–W09
are M, W10 is S. These estimates exclude fixing an unqualified predecessor.

One responsible author/operator per ordered step; independent review is a
separate session, never self-approval. Platform testers can supply evidence but
cannot bypass the sequence. External wait includes macOS/Windows GUI access,
backend accounts/test budget, legal/license decisions, signing/notarization and
hosting authorization, independent reviewers and maintainer availability. No
invented delivery date or promise of full release within hours.

## 9. Exit handoff and definition of done

- [ ] P01–P07 acceptance tied to unchanged or explicitly requalified candidate;
      P08 plan review complete; implementation reviews/fix re-reviews accepted.
- [ ] W01 decisions and real maintainers recorded; public/Git/release/signing
      actions separately authorized as applicable, with no credentials in records.
- [ ] Current files, all publication history/refs, source archives, packages and
      logs pass disclosure/license review; raw experiments remain private;
      sanitized examples, proper source attribution and notices ship.
- [ ] Reproducible native artifacts, per-RID SBOM/provenance, trust instructions
      and checksums match actual installed and promoted bytes on all required OSes.
- [ ] Public quickstart, CLI/MCP examples, support matrix, contributor/build guide,
      troubleshooting and operations/security handbook pass risk-driven checks;
      clean-profile results are new P08 evidence, not recycled P07 labels.
- [ ] P08-PI01–PI03 pass for all required managed-Pi cells, with actual native
      Windows final-package launch/follow-up/correlated results/interrupt/stop/
      reconnect and launcher/context tests; no Linux/cross-build substitution.
- [ ] Every promised host/platform native-wake cell passes final-package acceptance;
      public support describes native wake as default and manual read/catch-up as
      explicit degraded recovery, never a standard-path substitute.
- [ ] Upgrade/drain, matching-state rollback, WAL-safe backup/restore and uninstall
      acceptance pass; no unsafe old/new process/schema combination is enabled.
- [ ] Local-only diagnostics/export consent, credential rotation/revocation,
      issue/security reporting, advisory and patch procedures demonstrated.
- [ ] Compatibility canary, upstream regression, support/EOL policy and maintenance
      handoff accepted by a real owner; nonblocking follow-ups explicitly assigned.
- [ ] Applicable format/lint/build/tests/native-platform gates and bounded doc/link
      checks recorded honestly; unavailable or unrun checks remain visible.
- [ ] P08-G01–G11 and every mandatory prior phase gate pass, F01–F26 evidence ledger
      is complete, and owner approves full-product status.

Only then may the roadmap's final status become **full-product release accepted**.
This file does not update that status. A Linux demo, JIT-only build, prerelease
preview, partial platform set or limited integration release retains explicit
scope exclusions and cannot stand in for completed P08 or the full baseline.
