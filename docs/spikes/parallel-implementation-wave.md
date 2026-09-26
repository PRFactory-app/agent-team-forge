# Near-term parallel implementation wave

## Decision and evidence boundary

Eight phase plans do **not** yield eight immediately independent implementations.
Allocate **four useful coding writers**: canonical source, tooling, offline Codex
queue conformance, and the already-running paging repair. Add **one bounded
current-command documentation writer** after ownership handoff; do not count that
as a fifth coding feature. Three further coding slots remain blocked below.
There are three new coding launches, not eight new writers, available now after
the input locks specified here. Do not duplicate the existing repair worker.

This allocation preserves ordered **P01 → P08 acceptance**, while separating
implementation dependencies from whole-phase sequencing. It proposes bounded
streams, not full-phase implementation approval. Parent owns dispatch and any
baseline change; this document starts no agents, live backends or platform tests.

Inputs: [roadmap](../roadmap.md), [cross-phase contracts](../planning/full-product/contracts.md),
[P01](../planning/full-product/01-foundation.md), [P02](../planning/full-product/02-durable-core.md),
[P03](../planning/full-product/03-managed-agents.md), [architecture](../architecture.md),
[consolidation plan](product-core-consolidation-plan.md), and [contributor policy](../../AGENTS.md).
The consolidation review is at commit `e1d77ea1532dafea6ab1d0ccf26dfd2a624ad2b2`,
path `docs/spikes/product-core-consolidation-plan-review.md` (available in
`.worktrees/core-consolidation-review/`). Approval covers the bounded lift only,
subject to provenance, legacy config isolation, Host checkpoint disposition,
relocated gates and file-by-file retirement conditions.

### Immutable inputs, not moving branch tips

| Alias | Exact commit / input | What it permits |
| --- | --- | --- |
| M | `bf4ffc1b5392afe8de878b66b897d3b6521623ae` | Clean documentation ancestry for canonical feature branches; no untracked main files imported. |
| S | `81a11b27fb67d2ec600ace67586c092829927f8f` | Selected reviewed fake-core source. Recorded 61 tests, 19 published scenarios, one demo; not rerun here. |
| I | `765fe1df5a04eb232cf8f1706085440de39f93f6` | Inspection repair base only: 73/74 full tests passed, paging test failed; AOT/scenarios/demo do not override failure. |
| H1 | `fdf24027cf385c54f2d7c278ec2bc0eb576b7032` | Private-file hardening, Codex review reported running; not approved input. |
| H2 | `d714348938061b27a06f693d19c0f822ef7e6a66` | Acceptance-outcome hardening, Codex review reported running; not approved input. |

Select S now; do not wait for I or H1/H2 to become green. Those remain separate
follow-ups, with their findings visible. The Gantt HTML at `0ef8af0525b42ae940b0b5b88011a9ea0815fe7a` awaits review;
no rewrite or second Gantt writer. `docs/research/codex-queue-admission.md` is an
untracked research input (Git blob hash `866ed48cc23dc429f454ca2a2d77a205744d20d6`):
parent supplies that read-only copy before W4 dispatch. Its public Codex source pin is `36650394c5b38c2990ccf2a3457165ca3e9d9726`
(version 0.157.1). `docs/research/managed-pi-control.md` arrived during this task;
read snapshot blob `c5632e53eeea9c45b58ed64107e7a11100856927` pins Pi 0.87.1 at
`f07218c4d4bbc12bef056a7058c3dd49dfe41abe`. It establishes research, not approval:
RPC is headless; visible control needs a proposed same-process extension, a
language exception and reviewed authentication/correlation/replay contracts.
Agent statuses above are supplied handoff facts, not live telemetry checked here.

## Dependencies that matter

| True dependency / serialized authority | Artificial whole-phase wait to avoid |
| --- | --- |
| One frozen source map and one owner of solution/build configuration. | Root scripts and current run instructions need the map, not completion of every source edit. |
| One reviewed schema/migration stream; approved authority, retry and recovery contracts before effects. | Offline queue tests need neither Windows feasibility nor an installed product. |
| Qualified native binding, finality and foreign-activity policy before real dispatch. | A future Pi adapter need not wait for Claude implementation if both consume a frozen shared backend contract. Today Pi's implementation/security contract is unapproved. |
| Shared immutable contract fixtures before independently implementing P02 slices. | Storage compatibility work need not wait for all event/consumer features merely because P02-W07 follows W06. It still needs its own reviewed migration/restore contract. |
| Combined review and native-platform evidence before phase acceptance. | Missing macOS access need not stall the approved Linux fake-core lift. It still blocks the relevant phase/platform exit. |

P02 section 4/9 and P03 W06/W07 currently express serial package order. Parent
must explicitly accept scoped dependency exceptions/change control before widening
implementation; this file does not silently amend them. Frozen contracts, not
optimistic stubs, enable concurrency. New persistence/security/delivery contracts
need independent plan review **before runtime coding**, even in a test profile.

## Writer allocation and exclusive files

`D` means `spikes/m0-durable-core/` at S or I; `T` means
`tests/AgentTeamForge.Tests/` under the relevant root. Listed directories include
only the assigned subtree, not neighboring shared files. All unlisted files are
read-only. Each lane gets a feature branch matching its worktree suffix under
`wave/`; no writer edits main or a shared integration checkout.

| Lane / worktree / immutable base | Readiness and exact ownership | Exit and integration order |
| --- | --- | --- |
| **W1 Canonical source** — `.worktrees/wave-core`, base M, import S | **Launch now after recording S selection.** Own root `AgentTeamForge.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `nuget.config`, `.editorconfig`; imported `src/` and `T` files from S only; `docs/spikes/canonical-core-import.md`. No new queue files from W4. Preserve three projects and fake-only roles. Own any narrowly justified legacy SDK/feed isolation files, explicitly enumerated in the import manifest before editing; no legacy source change. | Compare allowlisted blobs/modes before relocation; record exclusions/tree/source mapping. For Host `Features/Jobs/JobsEndpoint.cs` checkpoint, record the review-permitted narrow temporary exception, fake-only reachability and removal trigger; do not mix an unreviewed behavioral fix into the lift. Build/test clean relocated tree and inspect config inheritance. **Integrate first**, after independent Codex review. |
| **W2 Executable tooling** — `.worktrees/wave-tools`, base M, import S | **Launch alongside W1** using the fixed layout above. Own only `scripts/verify.sh`, `scripts/published-smoke.sh`, `scripts/demo.sh`, narrowly necessary `.gitignore` changes, `docs/spikes/canonical-tooling.md`. No csproj, source, test-support or build-prop edits. | Preserve executable bits, exact SDK gate, linked-worktree discovery, published binary overrides and nonempty demo selection. `bash -n`, ignore-path checks; full runtime checks after W1 composition. **Integrate second**; absence of root source on M is a test dependency, not a reason to idle. |
| **W3 Current commands** — `.worktrees/wave-commands`, base M | **Small handoff, documentation support only.** Parent releases exact files: `README.md`, `HANDOFF.md`, `docs/implementation-status.md`, `docs/architecture.md`, `docs/spikes/m0-integration-plan.md`, `docs/spikes/e2e-demo-plan.md`. No `docs/project-status.html` or Gantt files; existing owner updates those after review. Own only current-location/command/status edits, not historical report rewrites. | Validate local links and commands against W1+W2 candidate; preserve old hashes/counts and fake-only limits. **Integrate third**. Stop after command handoff; no inventory-only make-work to retain a writer. |
| **W4 Codex queue fake conformance** — `.worktrees/wave-queue`, base S | **Launch after hashed research handoff.** Own new `D/T/Features/Agents/Backends/CodexQueueConformanceTests.cs`, `D/T/Support/CodexQueueModel.cs`, and `docs/spikes/codex-queue-fake-conformance.md` only. Use existing `IJobBackend`, dispatcher, `JobFixture` and `ScriptedBackend` read-only. No production transport, daemon wiring, package or schema changes. | Tests model public queue auto-start, not busy rejection. Exercise existing core uncertainty/no-replay behavior where expressible; explicitly label protocol-only counterexamples. Full unit suite/format/build, deterministic barriers, no sleeps or native Codex launch. **Integrate fourth**, as reviewed allowlisted test blobs mapped to root T; never merge S ancestry. |
| **W5 Paging repair (already staffed)** — `.worktrees/m0-paging-test-fix`, base I | **In progress; do not launch replacement.** Existing Claude owns `D/T/Features/Jobs/ListJobsTests.cs` and its repair report `docs/spikes/m0-paging-test-fix.md` (confirm report ownership with parent). Fix fixture ordering, not production paging semantics; do not expand into W1 source edits. | Deterministic IDs/order distinguish FIFO acceptance from UUIDv7 keyset order. Repeated focused tests plus full gates, Codex re-review and reviewed inspection source required. **Later follow-up** after canonical baseline; map the complete inspection slice, not the test alone. |
| **W6 P02 storage maintenance** — reserved `.worktrees/wave-storage`, planning input M; runtime base **unset, blocked** | **No writer yet.** Proposed ownership after review: `src/AgentTeamForge.DAL/Migrations/`, `src/AgentTeamForge.DAL/Sqlite/JobDatabase.cs`, new Business/Host `Features/Maintenance/`, `T/Features/Maintenance/`, `T/Scenarios/StorageMaintenanceScenarios.cs`. Parent must allocate migration IDs and the shared Host composition edit separately. | Unblock with approved P02-W07 scoped plan: compatibility checksums/range, consistent backup, maintenance interlock, restored-work quarantine, fixture schema and API. Then pin accepted canonical commit before worktree creation. Real previous-schema upgrade/interruption/refusal/isolated-restore tests; no fake full-feature credit. **After core**, independent of W7 only after shared schema/composition handoff. |
| **W7 P02 scoped acceptance** — reserved `.worktrees/wave-authority`, planning input M; runtime base **unset, blocked** | **No writer yet.** Proposed ownership after review: new H/B/D `Features/Authority/`, `T/Features/Authority/`, `T/Scenarios/ScopedAcceptanceScenarios.cs`; later `Features/Jobs/{AcceptJob,JobContracts,JobsEndpoint,JobStore}.cs` in their existing projects. No migrations or shared composition while W6 owns them. | Unblock with independently reviewed P02-W02 authorization/bootstrap/revocation matrix, semantic fingerprint/default version, retry/epoch/tombstone decision and numeric limits; W6/schema owner supplies approved migration and stable storage calls first. Pin canonical runtime base and exact file set then. Red tests: cross-scope denial, replay/revocation, equal/conflicting retries, atomic quotas. **After schema handoff**, not eight-phase waterfall completion. |
| **W8 Managed Pi feasibility** — reserved `.worktrees/wave-pi`, planning input M; runtime base **unset, blocked** | **No writer yet.** Reserve only new `T/Features/Agents/Backends/PiControlConformanceTests.cs`, `T/Support/PiControlFixtures.cs`, `docs/spikes/pi-control-conformance.md` for offline preparation. No production adapter/extension or live launch authorized. | Research has arrived, but is not implementation approval. Unblock with its hashed handoff plus independently reviewed scoped fixture/probe plan: `agent_settled` finality, non-correlating ordinary events, lost reply, human input, abort versus stop and replay gaps. Approve the proposed TypeScript extension exception before extension coding; review its bootstrap/controller-generation contract before runtime work. Pin canonical test base. Offline fixtures first; real Windows visible-tab and explicit-headless qualification remain mandatory and separately authorized. |

W6–W8 reservations are **not executable assignments**: unknown future commits are
not fabricated hashes. Parent must replace “unset” with an immutable accepted
commit and freeze exact new files/contracts before launch. W6/W7 cannot write
concurrently to shared migrations, job records or Host composition. A small
reviewed schema handoff can unlock parallel feature work; a promise that it will
exist cannot. No extra docs or duplicate negative tests are added to reach eight.

## Queue slice: concrete bounded value, not a pretend adapter

W4 covers human-first busy retention, queue-first human steering, automatic drain
after human completion, lost add response, start-before-delete crash, late add
after revoke, and two consumers. Add acceptance is not final completion; client
message ID is correlation, not idempotency; empty queue and `deleted: true` do not
prove nonexecution. A held test barrier replaces timing assumptions.

The current `IJobBackend` separates Start/Deliver and exposes Ack/Result/error/EOF;
it has no native queue revocation, human observation or strict-pause capability.
Do not extend that interface inside W4. Assert core quarantine/no redispatch using
its real current APIs; keep native-ordering counterexamples in the test model.
Model-only passes establish neither native behavior nor missing runtime controls.

Public queue source establishes atomic idle admission, **not strict pause after
foreign activity until explicit reconciliation**. Preserve that blocked gate.
No weaker queue contract is promoted as product behavior. A production queue
adapter requires a separately reviewed disposition of strict pause, binding,
revocation and outage receipts. More live probes cannot invent a missing primitive.

## Review, integration and decision critical path

1. **Parent locks inputs immediately:** S source, M ancestry, W1/W2 fixed path map,
   W3 file release, W4 research content hash. Existing paging and H1/H2 reviews
   continue independently. No waits for a new all-product plan.
2. **Separate Codex reviews run beside writers**, snapshot-bound per lane. Existing
   hardening reviewers retain H1/H2; paging gets re-review when its fix is frozen.
   No duplicate review worker or implied approval from a passing filtered test.
   Codex can review W2's script diff before W1 finishes; combined proof comes later.
3. **Independent Codex GPT-6 Sol, high integrator** creates milestone branch
   `integration/canonical-wave` from M, imports reviewed W1 → W2 → W3 → W4 inputs.
   W1 carries only source allowlist from S, not its legacy parents. Integrator
   verifies tree/history and source modes; semantic fixes return to Claude and
   independent re-review. Integration is not its own code-review approval.
4. Run fresh-output pinned-SDK restore, format, warning-clean Release build,
   full tests, Linux x64 AOT publish, published scenarios and exactly one demo
   through relocated scripts. Record actual counts and binary hash, not inherited
   61/19 or 74/20 expectations. Check legacy effective SDK/framework/packages/feeds
   before and after root config inheritance, native asset copying and script modes.
5. Fold reviewed paging/inspection and H1/H2 as small mapped follow-ups, rerunning
   combined gates and re-reviewing changed safety paths. H2 touches the same Host
   checkpoint as W1's temporary exception: parent explicitly closes/remaps that
   exception; no silent conflict resolution. W1 must set its removal trigger to
   the acceptance-seam follow-up, before any non-fake endpoint promotion, and prove
   normal operations go through Business and client/bridge roles never open SQLite.
   No branch-wide legacy merge.
6. In parallel, obtain **specific** W6/W7 contract decisions and W8 research handoff.
   Independent major-plan review precedes their runtime changes. Keep blocked
   writers unspawned; retire finished Claude workers after captured handoff via
   parent-owned `kill_agent`. Missing native Windows/macOS access stays blocked.

Retirement is later: no source lift deletes the interactive tree, unique evidence,
untracked state or live sessions. No main merge, commit, push, installation,
agent execution or process cleanup is performed by this planning task. Publication
and full phase acceptance remain separate. Native wake is still required and
separately qualified; fake backend/queue tests establish no lead-host wake.

## Next five actionable launches (parent dispatches)

1. **Claude W1**, M + S + review e1d77ea: exact root build files, imported `src/`
   and T allowlist above, `docs/spikes/canonical-core-import.md`. Freeze provenance
   and checkpoint exception first; produce canonical buildable fake core.
2. **Claude W2**, M + S `D/scripts/{verify,published-smoke,demo}.sh` + W1 path map:
   write only root scripts/ignore delta and `docs/spikes/canonical-tooling.md`.
3. **Claude W4**, S + hashed queue research + existing `IJobBackend.cs`,
   `DispatchJob.cs`, `JobFixture.cs`, `ScriptedBackend.cs`: write the two named
   queue test files/report. Offline only; no native agents or new contract.
4. **Claude W3**, once parent releases six named current-command documents:
   M + W1/W2 relocation map. Leave Gantt HTML with its existing owner/reviewer.
5. **Codex canonical source reviewer**, once W1 supplies immutable candidate,
   import manifest and clean test evidence: review exact W1 diff against S and
   e1d77ea conditions. Run in a separate native-subagent worktree/session; no
   implementation edits. This is deliberately not a fictitious fifth new coder.

Validation of this artifact: bounded Markdown/whitespace/local-link checks only;
no runtime gates rerun, no independent approval claimed for this allocation.
