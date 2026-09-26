# Product core consolidation plan

## Status and decision boundary

This is a bounded plan, not implementation approval or a source promotion.
The major path refactor requires independent plan review before code changes.
Follow [contributor policy](../../AGENTS.md), [architecture](../architecture.md),
[handoff](../../HANDOFF.md), and the [full roadmap](../roadmap.md).
No phase exit, real-agent capability, or platform support follows from moving files.

Read-only inspection used the integration worktree at `.worktrees/m0-integration/`:

- `81a11b2` records the reviewed runnable fake checkpoint in
  `docs/spikes/m0-fake-core-integration.md`. Its tested source is `22a4e79`:
  Linux x64, SDK `11.0.100-rc.1.26425.128`, 61/61 tests, 19/19 published
  Native AOT scenarios, and 1/1 published demo. These are recorded results,
  not tests rerun by this planning task.
- The inspected tree already contains job inspection from `9fb08b6`, merged
  separately at `289ef5b`, with its pinned re-review carried at `f9b0dd5`.
  That addition is not part of the earlier 61-test evidence. Its integration
  owner must supply combined gates before selecting it for promotion.
- Main remains documentation-only with untracked legacy runtime files. Those
  local files are neither approved source inputs nor safe deletion candidates.
  Integration also contains legacy ancestry; a whole-branch merge would exceed
  the bounded fake-core promotion.

**Do not block today's runnable fake checkpoint on this plan.** Continue using
its existing solution and scripts in the integration checkout. An explicitly
sanitized, authorized source-only checkpoint can be promoted at its current
`spikes/m0-durable-core/` path before consolidation if needed; it already uses
`AgentTeamForge.slnx` and the real project names. Do not wait for job inspection,
legacy retirement, web UI, or real adapters. Prefer one direct canonical import
once plan review is complete, rather than creating two lasting runtime copies.
“Publishable” here means a candidate source set for publication review, not
permission to commit, merge to main, push, or release.

## Smallest canonical layout

Lift the existing durable-core subtree without redesigning it:

```text
AgentTeamForge.slnx
global.json
Directory.Build.props
Directory.Packages.props
nuget.config
.editorconfig
src/
  AgentTeamForge.Host/
  AgentTeamForge.Business/
  AgentTeamForge.DAL/
tests/
  AgentTeamForge.Tests/
scripts/
  verify.sh
  published-smoke.sh
  demo.sh
docs/spikes/                 concise historical decisions and evidence
```

Exactly three production projects, plus the existing test project. Retain
Host → Business → DAL; Host's direct DAL reference remains composition-only.
Bridge/client roles must not open the job database. Keep existing `Features/Jobs`,
`Features/Recovery`, `Features/Agents/Backends`, Host transport/hosting, DAL
SQLite/migrations, test scenarios and support in place within their projects.
No fourth Contracts project, generic adapters, repository ports, mediator,
namespace rewrite, or simultaneous schema/protocol change.

The source already has product project/assembly identities; do not rename them.
Keep `SpikeProfile`, `SpikeProfileFile` and `SpikeRig` initially: their names do
not create `.Spike` production projects, and renaming them adds no capability.
Preserve the explicit test-profile fence and fake-only executable roles. Moving
fake scaffolding does not make it a production backend or general setup flow.

Retain `net11.0`, the exact SDK pin with roll-forward disabled, central package
versions, public-only NuGet configuration, analyzer/AOT settings, and Linux
platform annotation. No SDK installation, package upgrade, retargeting, or
Windows support annotation change belongs in this slice. Root MSBuild/NuGet/SDK
files can affect retained legacy projects: verify their effective settings and
keep any necessary legacy isolation explicit, without retargeting old evidence.

## Source, tests, and retirement inventory

All durable-core source names below are relative to
`spikes/m0-durable-core/` at the selected immutable source commit. Test names
are relative to `tests/AgentTeamForge.Tests/`. Carry complete reviewed files,
not hand-picked methods that omit safety checks.

| Source or tooling | Regression evidence to carry and rerun | Disposition |
| --- | --- | --- |
| DAL `Features/Jobs/{JobStore,JobRecords}.cs`, `Migrations/Schema.cs`, `Sqlite/*` | `Features/Jobs/SchemaTests.cs`, `AcceptJobTests.cs`, `Scenarios/CrashBoundaryScenarios.cs` | Move with real SQLite acceptance, idempotency, generation and durability checks intact. |
| Business Jobs `AcceptJob`, `AdmissionGate`, `DispatchJob`, `GetJob`, `JobContracts`; Recovery `RecoverOnStartup` | `AcceptJobTests`, `AdmissionFenceTests`, `DispatchJobTests`, `DispatchFaultTests`, `Features/Recovery/RecoverOnStartupTests` | Move together; preserve admission/attempt/uncertainty boundaries. |
| Business `Features/Agents/Backends/*`, `SpikeProfile.cs`; Host fake backend and setup features | `Scenarios/CoreFaultScenarios`, `CrashBoundaryScenarios`, `PrivateBoundaryScenarios`, scripted backend support | Keep fake-only profile, bounded child execution and fault injection. No real adapter import. |
| Host `Program.cs`, `Features/Jobs/*`, `Hosting/*`, `Transport/*` | `Scenarios/ClientLifetimeScenarios`, `PrivateBoundaryScenarios`, `Transport/IpcClientDeadlineTests` | Preserve role separation, credential boundary, daemon independence and deadline qualifications. |
| Optional approved job inspection: Business `ListJobs`, DAL query changes, Host endpoint/CLI/MCP/wire changes | `Features/Jobs/ListJobsTests`, `Scenarios/JobInspectionScenarios` | Include only the complete reviewed change plus combined integration evidence; otherwise use `81a11b2` baseline. Do not copy the moving working tree. |
| Solution, three production projects, test project, build/package/SDK/feed/editor files | Restore, format, warning-clean Release build, full tests, AOT publish | Lift as a coherent build unit, preserving executable modes and project references. |
| `scripts/{verify,published-smoke,demo}.sh`, test `Support/*` | Published process scenarios and exactly one successful demo test | Move once; retain `ATF_HOST_BINARY`, `ATF_DEMO_BIN`, `DOTNET` and bounded owned-process cleanup. |
| Durable-core `README.md`, `REPORT.md`, `*-REPORT.md`, `CORE-FAULT-FIXES.md`, `JOB-INSPECTION-FIXES.md` when selected | Source-bound reviews and integration record | Curate concise public evidence under `docs/spikes/`; do not import raw logs or erase unique findings. |
| Superseded durable-core runtime subtree | Full canonical gates and file manifest comparison | Remove duplicate tracked runtime/build/test/scripts only after canonical replacement passes review. Preserve historical evidence paths where linked. |

The separate `spikes/m0-interactive/` tree is **not superseded by fake tests**.
Its `.NET 10` evidence and safety-review qualifications remain separate. The
integration tree has additional repaired tests absent from the local untracked
copy. A later retirement manifest must pin each selected repaired source and
review, rather than treating either tree as universally authoritative.

| Legacy source family | Unique tests/evidence that prevent immediate deletion | Later destination or decision |
| --- | --- | --- |
| `Codex/JsonRpcPeer`, `UdsWebSocketTransport` | `JsonRpcPeerTests`, `JsonRpcTransportBoundsTests`, `UdsWebSocketTransportTests` | Reviewed protocol helpers beside Business agent backends; port tests before removing originals. |
| `Codex/ClientMessageId`, `CodexThreadEvidence`, `CodexCommands` | `CodexBindingTests`, `CodexHistoryEnvelopeTests`, `CodexThreadEvidenceTests` | Preserve authoritative identity/correlation and history-envelope regressions; separate Host commands from Business policy only when adapter slice requires it. |
| `Claude/*`, `ClaudeCommands` | `ClaudeHookTests`, `ClaudeHookRunnerTests`, `ClaudeInboxTests`, `ClaudeCapabilityGateTests` | Preserve isolation/capability limits; no assumption that prior feasibility establishes safe control. |
| `Delivery/*`, `State/SpikeState`, launch/session intent | `DispatchClaimTests`, `JobReconcilerTests`, `JobTagTests`, `SessionIntentTests` | Map behavior to accepted durable contracts; do not promote a competing file-based scheduler/state authority. Retain tests until equivalent behavior is demonstrated. |
| `Herdr/*`, `Os/*`, `Launch` | `HerdrOwnershipTests`, `HerdrCliBoundsTests`, `ProcessIdentityTests`, `ProbeProgram` | Keep ownership, environment and bounded-process evidence; later agent/terminal integration slice. |
| `AtfSpike.slnx`, `AtfSpike` projects, `scripts/{atf-spike,bridge-kill,gates}.sh`, legacy reports | Remaining protocol suites, live-session dependencies and unique recovery evidence | Retain outside the canonical solution until every needed behavior has a reviewed replacement or an explicit approved non-promotion disposition. Then retire projects/scripts, not unique evidence. |

Retirement is file-by-file and snapshot-bound. Record source hash, destination
or disposition, replacement test, review, and live dependency for each candidate.
Do not delete by age, name, uncertain ownership, or broad `spikes/` glob. No
`git clean`, process kill, state migration, or worktree removal is part of source
consolidation. Preserve live sessions, launch working directories, ignored local
records and recovery artifacts. Process cleanup needs separate authorization
and verified ownership; Git history alone cannot preserve untracked evidence.

## Path, documentation, and test-tool updates

The lift preserves relative project references and the scripts' parent-directory
root calculation. Verify rather than invent replacement tooling:

- `AgentTeamForge.Tests.csproj` copies the built Host apphost and native SQLite
  assets through a relative `../../src/` target. Keep that relationship and test
  a clean build; stale `bin/obj` must not conceal missing inputs.
- `SpikeRig` defaults to the test output's `atf`, with a published-binary override.
  Keep both paths working. Do not couple test discovery to a particular worktree.
- Validate script execute bits, SDK discovery from a linked worktree and clean
  checkout, output under root `artifacts/`, ignored `.run/` and `evidence/`, and
  exact demo filter selection. No empty test match may pass.
- Root `.gitignore` already covers build outputs, artifacts, evidence, SDKs,
  runtime state and credentials. Check relocated paths without broadly staging
  ignored files. Root config inheritance into legacy remains an explicit check.
- Update current run instructions in root `README.md`, `HANDOFF.md`,
  `docs/implementation-status.md`, `docs/project-status.html`, and active
  integration/demo plans that reference the old runtime path. Inspect links in
  the carried durable-core README, which currently reaches documents through
  `../../docs/`. Update architecture's implementation-status wording narrowly;
  do not rewrite scope or declare phases complete.
- Preserve historical commands, source hashes and result counts in pinned
  reviews/integration reports. Add a current-location note instead of rewriting
  old evidence as though it ran from the new root. Keep one current command
  location and explicit historical references, not duplicate runtimes.

## Safe Git promotion boundary

This section describes future authorized work only. This task changes one plan
file and does not stage, commit, move source, switch main, or push.

1. Create a dedicated promotion feature worktree from an agreed clean main
   commit. Freeze an immutable source commit and explicit path allowlist:
   durable-core source/tests/tooling plus individually cleared evidence/reviews.
   If job inspection gates are pending, select `81a11b2`; do not delay the fake
   checkpoint to consume it. Record excluded later inputs for a follow-up.
2. Inspect the source tree and its review lineage read-only. Do not merge the
   entire integration branch or cherry-pick a merge whose parents import legacy
   source. Import only allowlisted tracked blobs with modes into the clean
   branch, preserving source commit/blob identities in a promotion manifest.
   Exact extraction mechanics belong to the implementation lane.
3. Compare the extracted subtree against its pinned origin before mechanical
   relocation; review relocation/config/path deltas separately. No unrelated
   fixes or untracked local legacy files enter the candidate. Never use broad
   `git add .`, forced ignored-file addition, or history rewriting as cleanup.
4. Inspect both the final staged tree and newly reachable commit history for
   unrelated source, credentials, private references, raw evidence and generated
   files. Verify the destination ancestry does not acquire legacy parents merely
   because the source checkpoint had them. Review/source provenance is recorded
   without importing that ancestry.
5. Only after review, fresh combined gates and separate merge authorization may
   the independent integrator advance the milestone. Publication remains a
   separate authorized action with license/publication decisions intact.

## Disjoint waves and the actual bottleneck

Use fresh bounded sessions and dedicated feature worktrees. Claude implements;
separate Codex sessions review code. Codex GPT-6 Sol, high, integrates approved
snapshots and runs combined gates; integration is not review approval. Semantic
conflicts return to a Claude writer and independent re-review. Staffing and spawn
routes follow the current `AGENTS.md` rules (all orchestrator workers via
win-agent-teams). Retire completed
workers after handoff. This plan assigns work; it launches no agents.

| Wave / lane | Exclusive ownership and useful parallel work | Dependency / exit |
| --- | --- | --- |
| 0: checkpoint integration | Existing Codex integrator owns current milestone branch and job-inspection combined gates only. | Runnable fake checkpoint continues unchanged; no dependency on this refactor. |
| 0: plan review | Separate independent reviewer owns consolidation verdict, including root-config inheritance and publication boundary. | Approve major path-refactor plan before any consolidation implementation. |
| 0: evidence inventory | Claude owns later retirement manifest/curated evidence draft only; reads legacy source, does not edit or delete it. | Can run alongside plan review and current gates; Codex reviews inventory. |
| 1A: canonical source lift | One Claude writer owns root solution/config and `src/` plus `tests/` relocation as one coherent unit. No behavior changes. | Approved plan and frozen input; path map handed to other lanes. |
| 1B: tooling relocation | Another Claude owns only root `scripts/` and necessary ignore deltas, against 1A's fixed path contract. | Independent worktree; no edits to projects, source, tests or shared build props. |
| 1C: current documentation | Claude owns current README/status/handoff and active-path updates, excluding historical evidence owned by inventory lane. | Uses fixed path map; verifies final commands after integration. |
| 1D: review/verification | Separate Codex reviewers inspect frozen 1A/1B inputs and run bounded checks; inventory/docs review can overlap. | Findings return to owning writer. Use useful active lanes within the `AGENTS.md` cap, not idle quotas. |
| 2: canonical integration | Independent Codex integrator owns integration branch, tree/history inspection and combined gates. | Serialize accepted inputs; no competing writes to destination index or manifest. |
| 3: legacy migration/retirement | Separate Claude protocol/terminal slices with disjoint files and regression ownership, each with Codex review. | Requires accepted backend contracts and explicit per-file retirement evidence; not a prerequisite for waves 0–2. |

True parallelism exists in protocol inventories, read-only review, isolated
platform probes, docs and scripts against a frozen path contract. Splitting a
mechanical lift into DAL, Business and Host writers adds coordination rather
than throughput. Source selection, shared build props, final source promotion,
merge conflict resolution, and combined gates are serialized authority points.
More agents cannot remove those bottlenecks. Keep already-approved checkpoint
execution independent and feed the integrator small, snapshot-bound inputs.

## Focused acceptance gates

1. Independent major-refactor plan approval, then opposite-family code review of
   actual import/relocation/config diff. Re-review fixes; missing reviewer blocks
   merge, not use of the previously approved checkpoint.
2. Verify allowlist, source/blob mapping, project references, three production
   projects, test discovery, no extra legacy ancestry/files, and no source lost
   before retiring the duplicate durable-core tree.
3. Run moved `scripts/verify.sh`: exact SDK, restore, format verification,
   warning-as-error Release build/analyzers, full tests, Linux x64 Native AOT
   publish, published process scenarios. Run `scripts/demo.sh` against that
   published binary and require exactly one passed scenario. Run shell syntax
   and diff whitespace checks. Use fresh outputs and private owned test state.
4. Preserve admission fencing, atomic acceptance/attempt boundaries, uncertain
   recovery/no replay, credentials, client-death survival, deadline and fault
   regressions. Record actual selected-source counts; do not reuse 61/19 as
   expected totals after job inspection or later changes. Pure relocation needs
   equivalence evidence; any behavior change needs focused red/green/refactor.
5. Check local documentation links and current commands, ignored artifact paths,
   and legacy SDK/package inheritance. If legacy effective settings change,
   resolve isolation and rerun affected tests; do not silently upgrade them.
6. Record snapshot, SDK, platform, commands, binary hash, failures/unrun gates and
   existing review qualifications. Linux fake/AOT results do not prove real
   agents, interactive terminals, native wake, power-loss safety or memory gains.

Windows/macOS remain future native qualification gates, not deletion of scope.
Managed Claude/Codex/Pi, especially native Windows Pi launch, same-session
follow-up/results, interrupt/stop and reconnect, require real platform tests.
Interactive selected visible terminals and explicit headless choice remain
mandatory; native authenticated wake needs separate host/platform evidence.
Missing access blocks those cells. Cross-builds and this path refactor cannot
establish them. Web console implementation and architecture expansion are out
of scope.

## Immediate next boundary

Keep the current fake checkpoint runnable. Obtain independent review of this
plan, select the pinned promotion input, then authorize a source-only canonical
lift and focused verification. Defer legacy removal until its unique protocol
coverage and live dependencies have explicit dispositions. No source movement,
commit, main update, push, runtime test, or process cleanup occurs in this
planning task.
