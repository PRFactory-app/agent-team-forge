# Handoff to the Claude orchestrator

**Prepared 26 September 2026. Start here before older status documents.**
The user is transferring orchestration to Claude because the previous coordinator
repeatedly allowed completed workers to sit idle instead of dispatching the next
useful wave. No replacement orchestrator has been spawned by this handoff.

## 1. Immediate objective and operating rules

Deliver a testable tool quickly, not more planning for its own sake. Maintain a
ready queue of small, bounded implementation/review/integration tasks. The user
requested **at least five to six useful parallel workers**, and asked whether
there could be eight Claude writers. Eight phase plans are not eight independent
implementations: distinguish real contract dependencies from unnecessarily serial
whole-phase ordering. Do not count waiting workers as active or invent busywork.

- Claude writes runtime/test code; Codex GPT-6 Sol, high, reviews and integrates.
  Semantic merge corrections go to a fresh Claude writer, then Codex re-review.
- Spawn Claude Code Opus/medium through **win-agent-teams**. New Codex and Pi
  workers use **native subagents**, not win-agent-teams. Pi Astra/medium plans
  and researches. If the next harness lacks those tools, state the routing gap;
  do not silently use the rejected orchestration route.
- Capture each worker's immutable commit, report, gates and remaining findings;
  then call `kill_agent` for finished MCP workers. Native subagents terminate
  after their task. Reuse reports, not huge implementation sessions; hand off
  well before roughly 200k context tokens.
- Pragmatic TDD on critical behavior. No prose/formatting assertion battery.
  Major architecture/security/durability changes need independent plan review;
  small fixes need code review, not another full plan cycle.
- Runtime and automated tests are C#/.NET. Minimal shell glue is allowed.
  Static HTML/CSS visualization is explicitly authorized. A future TypeScript
  Pi extension is a proposed exception, **not yet authorized implementation**.
- All project documents are English; conversation may be Swedish.
- No private company/repository references in public docs. Do not access those
  repositories. Use only this repository and explicitly relevant public sources.
- Do not claim working platforms, native agents, native wake, or a phase exit
  from fake tests, source research, a cross-build or another project's results.

Read [AGENTS.md](../AGENTS.md) for the rest of the contributor contract.

## 2. What exists — and what emphatically does not

**No P01–P08 phase is complete.** All eight have detailed plans, totaling 65
work packages. P01 feasibility/design is in progress; P02 has only a bounded
Linux fake-child experiment; P03 has exploratory protocol/legacy spikes.
P04–P08 product implementation has not started. F27 has a reviewed design and
static mockup, not a runtime web console.

There is an independently approved runnable **Linux fake-core checkpoint** on
`integration/m0-e2e` at **`81a11b27fb67d2ec600ace67586c092829927f8f`**:

- `AgentTeamForge.slnx`, exactly three runtime projects: Host → Business → DAL.
- Independent daemon, SQLite acceptance/idempotency/attempt/result, private
  IPC and thin MCP, bounded fake-child execution and conservative recovery.
- Combined **61/61 tests, 19/19 published Native AOT scenarios, 1/1 AOT demo**.
- Source/runtime is not yet promoted to `main`. Later integration snapshots
  are not automatically substitutes for this green checkpoint.

The promised real-agent demo is **not achieved**: owned visible Codex TUI,
MCP submission, bridge death, fresh-client result, same-conversation follow-up.
Managed Claude/Codex/Pi, native Windows Pi, actual Windows/macOS qualification
and native wake remain requirements, not delivered capabilities.

Architecture remains feature-first vertical slices in exactly three projects,
**Host → Business → DAL**, not Clean Architecture. Business directly references
DAL. Only daemon owns runtime DB. Host includes CLI/MCP/setup/daemon/web roles;
no fourth UI assembly, custom model loop or second scheduler.

## 3. Worker ownership at transfer

All completed Claude MCP workers have been stopped, including the last
`codex-queue-conformance` author. No new workers are to be launched by the old
coordinator after the transfer request.

**At the initial transfer check one native Codex review was running; it has
now finished with bounded approval. No worker remains active at transfer.**

| Worker | Worktree / branch | Ownership |
| --- | --- | --- |
| `canonical-source-review` (native subagent `sa-42`) | `.worktrees/canonical-source-review`, `review/canonical-source` | Finished: bounded approval of `3f0b0c3`, review commit `a86dbe4`; no blocking findings. |

Do not duplicate the completed source review. Its [committed report](spikes/canonical-source-review.md)
is copied into main with only the personal SDK path normalized for publication.
The original review branch/commit preserves the exact reviewer artifact. Tooling
still needs its own review and combined integration still needs fresh gates.

The worktrees `.worktrees/canonical-tooling-review` and
`.worktrees/m0-composition-fix` exist but **no workers were launched in them**.
They are ready for the next coordinator. Do not infer activity from a worktree.

## 4. Highest-value next wave

### A. Get canonical root source ready for `main` — do not wait for later fixes

The canonical lift plan is independently approved with bounded conditions:
[plan](spikes/product-core-consolidation-plan.md),
[review](spikes/product-core-consolidation-plan-review.md), review commit `e1d77ea`.
It deliberately selects the green **81a11b2** source, not the newer blocked tree.

| Input | Commit / worktree | Status / next action |
| --- | --- | --- |
| Root source/config lift | **`3f0b0c3`**, `.worktrees/canonical-core-source`, `feature/canonical-core-source` | Claude handoff complete. 58 allowlisted source blobs/modes imported from 81a11b2 into root solution/config/src/tests without legacy source ancestry. Config-only legacy isolation and a narrowly documented fake-profile Host/DAL test-hook exception. **Codex approved at `a86dbe4`**: 61 tests, 19 published scenarios and demo passed independently. Read the source report and copied review. |
| Root scripts | **`7bd020a`**, `.worktrees/canonical-core-tooling`, `feature/canonical-core-tooling` | Claude handoff complete; **review not started**. Cwd-independent scripts, SDK discovery, unique publish/evidence paths and nonempty TRX guards. Author reports 61 tests, 19 AOT scenarios and demo on a scratch canonical fixture. Read `docs/spikes/canonical-tooling-report.md`. |

Start a fresh independent Codex tooling review in the prepared review worktree.
Once both are approved, create a clean `integration/canonical-wave` from current
main, merge the reviewed source and tooling branches, and run fresh combined
format/build/full tests/AOT/published scenarios/demo. These branches have clean
documentation ancestry; **do not merge the legacy integration branch wholesale**.
Semantic conflicts require a Claude fix, not unreviewed Codex behavior changes.

After combined approval, advance the canonical source through the normal local
integration/main gate and update current README/commands/status. Do not wait for
inspection, new authority features, web UI or real adapters to expose this
explicitly labelled fake-core checkpoint. Publication/license is a separate gate.

The root lift does not delete retained legacy source, worktrees, live sessions
or recovery evidence. Retirement is file-by-file after replacement coverage.

### B. Repair the newer milestone composite separately

`integration/m0-e2e` is now **`cd60824388cce505cb6af928ba797a1d3e266593`**.
It includes reviewed inspection, paging correction and two reviewed hardening
fixes, but **the combined build is broken**:

> `CS7036`: `AcceptanceOutcomeTests` passes three arguments to the integrated
> four-argument `JobsEndpoint` constructor (inspection added `ListJobs`).

Restore/format passed; combined build failed; full tests/AOT/scenarios/demo were
not run on this composite. Start one small Claude composition-test fix in
`.worktrees/m0-composition-fix` (`spike/m0-composition-fix`, base `cd60824`).
Preserve both feature paths, review the exact fix with Codex, then rerun combined
gates. Do not weaken/delete tests or describe earlier test counts as this tree's
results. See branch-local `docs/spikes/m0-promotion-check-2026-09-26-integration.md`.

Reviewed inputs already merged into that milestone:

| Input | Source | Evidence / limitation |
| --- | --- | --- |
| Job inspection | `9fb08b6` | Bounded approval; live best-effort keyset paging, not a snapshot. Output capped; DB work not bounded/scalable. |
| Deterministic paging test | `e821e8f` | Codex approved: 11 targeted, 74 full tests. Removes invalid UUIDv7 same-millisecond ordering assumptions. |
| Private-file reads | `fdf2402` | Codex approved: 72 tests, 22 published scenarios. Single descriptor, regular-file/owner/mode/size checks; Linux x64 only. |
| Post-commit response | `d714348` | Codex approved: 65 tests, 21 published scenarios. Honest `outcome_unknown`; untested COMMIT-error qualification remains. |

Reviews for the hardening fixes are on `review/m0-hardening` at `fe79286` and
copied into main docs. After canonical promotion, port later approved increments
with an explicit old-to-root path map and their complete tests, not legacy ancestry.

### C. Queue conformance ready for review

Claude committed **`52648dd`** on `spike/codex-queue-conformance`, worktree
`.worktrees/codex-queue-conformance`, then was stopped. **No independent review yet.**
Only two C# test/model files plus `docs/spikes/codex-queue-fake-conformance.md`.
Author reports 76 tests (61 base + 15 new); read the report for all gates.
The tests exercise existing core uncertainty/no-replay via real SQLite and model
native queue counterexamples separately. They neither implement nor prove a
native adapter. Review first; later import only these test/report blobs into the
canonical layout. Do not merge that branch's legacy ancestry into main.

### D. Contract review can unlock more genuine writers

New concrete draft plans are **not implementation approval**:

- [Native queue probe](spikes/native-queue-probe-plan.md): two bounded C# lanes;
  actual native execution requires ownership/isolation authorization. Strict
  human-pause remains unchanged; a weaker non-steering checkpoint is explicit.
- `docs/spikes/scoped-acceptance-contract.md`: W7 authority/bootstrap/revocation,
  semantic fingerprint/epochs, errors, storage and tests. Draft was untracked at
  the start of handoff; preserve and review it rather than assuming accepted.
- `docs/spikes/storage-maintenance-contract.md`: W6 compatibility, SQLite backup,
  maintenance interlock and restore quarantine. Same draft status. Allocate a
  single migration/schema owner; do not launch competing schema writers.
- [Parallel wave allocation](spikes/parallel-implementation-wave.md): useful
  ownership inventory, **not live staffing**. Its earlier review/activity labels
  are superseded by this handoff. Four useful coding lanes plus documentation
  were identified then; three further slots required reviewed contracts.

Freeze shared APIs/schema and exact canonical base before parallel implementation.
Whole P03 completion is not a technical prerequisite for all Pi work, for example,
but an unreviewed authentication/control contract still blocks runtime coding.

## 5. Native delivery findings that change the next technical direction

Use the public user-supplied repository and exact commits, not private references.

1. [PR #70 native wake](research/native-wake-pr70.md) is merged upstream at
   `471a17514d041e09b69cb24b910e418da28d2027`. Native wake is our standard, not
   ordinary watcher/model polling. Upstream concept evidence is not our platform
   qualification; Windows-Claude refusal is not working Claude wake support.
2. [Downstream delivery](research/native-downstream-delivery.md), user pin
   **`5149f3e1280749b52988a26952573cee61d2daff`** on
   `feat/native-downstream-delivery` in public
   `mikaelliljedahl/agentic-coder-teams-mcp`: pin itself changes documents;
   helpers exist but integrated delivery/mailbox/recovery were unfinished.
   Reported live idle Windows Codex queue delivery; Claude pipe-owner observation
   without writing. Separate queued/presented/completed and never resend uncertain
   native work. Do not assume feature branch merged into main.
3. **Important newer finding:** [Codex queue source](research/codex-queue-admission.md)
   at Codex 0.157.1 / `36650394c5b38c2990ccf2a3457165ca3e9d9726` really uses atomic
   `start_turn_if_idle`. Busy queued input waits; it does **not** steer the
   currently active human turn. Direct `turn/start` does steer and remains unsafe
   for that purpose. Stop researching whether those two paths are equivalent.
4. Queue still auto-drains after the human turn; it does not provide strict
   human precedence or pause-until-explicit-reconciliation. If the queue wins,
   later human input can steer its turn. Delete/absence is not universal proof
   of nonexecution; start-before-row-delete crash and multi-runtime uncertainty
   remain. Visible-TUI/thread binding is still separate. A probe cannot create
   a missing native guarantee; any narrower product policy needs explicit review.
5. [Managed Pi research](research/managed-pi-control.md): RPC is headless, not
   the visible TUI. A same-process extension is the credible interactive control
   path, but authenticated IPC, correlation/finality, replay, ownership and
   Windows still need concrete qualification. Proposed TypeScript exception is
   not blanket authorization. Do not silently substitute RPC for interactive Pi.

## 6. HTML report delivered

The user found the old epic status misleading. Claude replaced it with a
relative-wave Gantt and explicit implementation states:
[**docs/project-status.html**](project-status.html).

- P01 in progress; P02 bounded experiment only; P03 exploratory spikes;
  P04–P08 not started. No phase complete.
- Gray/hatching is planned, green only the reviewed bounded checkpoint.
- Separate plan readiness from implementation; 65 packages/27 features retained.
- Mobile stacked fallback fixed disappearing sticky labels; mixed-status header
  is neutral rather than green.
- Source `0ef8af0` + correction `4cc8726`, independently approved in review
  `20c8c68`; promoted to main as `bbe283f` + `97bffb9`, review docs at `a22d707`.
- HTML is a documentary snapshot, not live telemetry. It accurately records the
  earlier `765fe1d` failed 73/74 inspection gate. Newer integration build failure
  and future fixes need a later labelled update, not rewritten historical proof.

## 7. Repository, toolchain and GitHub

Main at handoff initially **`a22d707`** contains docs, not canonical runtime.
Expect this handoff commit afterward. Main also has **untracked** original
`spikes/m0-interactive/` runtime files; do not broadly stage or clean them.
Worktrees, `.tools`, artifacts, logs, runtime DB/state and credentials are ignored.
Never `git add .`, force-add ignored evidence, or delete unique untracked evidence.

Authorized isolated SDK: `<main-checkout>/.tools/dotnet11/dotnet`, version
**`11.0.100-rc.1.26425.128`**, official archive checksum previously verified.
Select DOTNET/DOTNET_ROOT/PATH per command; no global SDK changes. Legacy uses
existing .NET 10.0.401. Canonical scripts discover the shared SDK through Git's
common directory. No new installs, services or real model probes are authorized
merely by the local source-lift/review work.

To reproduce the older green fake checkpoint without disturbing current branches,
create a dedicated checkout/worktree at **81a11b2**, then from its
`spikes/m0-durable-core/`:

```bash
DOTNET="$(git rev-parse --path-format=absolute --git-common-dir)/../.tools/dotnet11/dotnet" ./scripts/verify.sh
DOTNET="$(git rev-parse --path-format=absolute --git-common-dir)/../.tools/dotnet11/dotnet" ATF_DEMO_BIN="$PWD/artifacts/linux-x64/atf" ./scripts/demo.sh
```

The relocated tooling changes publish output to a unique directory; use its
printed binary path rather than assuming `artifacts/linux-x64/atf` after the lift.

Git was initialized with user approval, on main, personal Git identity selected.
There is **no remote, no push, no public repository yet**. The desired organization
name `PRFactory` was occupied; `prfactory-dev` was suggested and the user was
asked to create the organization manually. Creation has not been confirmed.
Use the personal GitHub account if authorized to proceed; do not silently use
a different active CLI account. No project license chosen. Publication requires
owner decisions and source/history hygiene; local feature work is authorized.

## 8. Coordination traps — avoid repeating them

- `send_message`/`follow_up_agent` may return queued for a busy worker.
  **There is no background dispatcher.** Reconcile the exact idempotency key and
  deliver it when the target is resumable. Never blindly drain old pending keys
  addressed to already-retired agents.
- Prefer exact committed reports over `read_messages`; that inbox was often
  empty even after workers said they sent a handoff.
- MCP watch is one-shot. Re-arm the tool-provided owner-bound watch after each
  consumed checkpoint; a single old watcher does not supervise the whole project.
  Use current returned watch arguments, not copied personal paths/process tokens.
- The next review/integration task should already be prepared before its writer
  finishes. Close the handoff loop promptly, then retire the writer.
- Do not report a count from stale tables as current activity. Inspect actual
  worker state and distinguish active, waiting, stopped and planned lanes.
- Do not let optional later work hold the already reviewed bounded checkpoint.
  Conversely, do not call a red combined build green because inputs passed alone.

## Transfer-time final update

The final source review completed at `a86dbe4` with bounded approval; tooling
review and combined canonical integration remain next. No MCP or native worker
remains running. The last completed Claude author was stopped with `kill_agent`.
No new workers or orchestrator were spawned after the transfer request. The two
new W6/W7 contract drafts are preserved in the handoff commit as **unreviewed
proposals**, not approved runtime work. Local runtime source remains off main.
