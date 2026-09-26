# Implementation checkpoint — 2026-09-26

This is an evidence-bound coordination snapshot, not release approval. Runtime
work remains on local feature/integration branches, not promoted to `main`.
No full phase is complete. Public GitHub creation still awaits the owner's
organization confirmation; no remote or push exists.

## Latest integration and active wave — 14:10 UTC

**Reviewed fake-core checkpoint integrated at `81a11b2`.** Core admission fix,
IPC deadline and demo wrapper merged with original ancestry, no conflicts.
Combined gates: 61/61 tests, 19/19 published Native AOT scenarios, 1/1 AOT demo.
[Checkpoint and commands](spikes/m0-fake-core-integration.md). Still Linux fake
backend only, not real-agent E2E or runtime promotion to `main`.

Inspection `9fb08b6` and fake probe `5dc3631` passed bounded independent
re-reviews. Inspection integration is assigned; neither holds the approved demo.

Current six assignments (dispatch snapshot, not live telemetry):

- Claude: `m0-private-file-bounds`, `m0-acceptance-recovery` — independent core hardening.
- Codex: `inspection-integration-and-promotion-check`, `teardown-status-rereview`.
- Pi: `codex-queue-admission-research`, `product-core-consolidation-plan`.

The preceding Claude teardown writer committed `85f3fdd` and was stopped with
`kill_agent`. Public [downstream-delivery research](research/native-downstream-delivery.md)
is complete; native queue admission needs further source qualification. The
approved checkpoint does not wait for this research or consolidation planning.

The earlier source-by-source records below retain their historical review state;
this latest update and newer pinned reviews supersede their pending labels.

## Earlier delivered snapshots and gates

| Snapshot | Observable output | Qualification / next gate |
| --- | --- | --- |
| Durable core `55d3c054`, fault repair `1d3c409` | Real `AgentTeamForge.slnx`, Host → Business → DAL, .NET 11 daemon/SQLite/private IPC/MCP and fake child. | Independent repair verification: 47 tests, Linux AOT, 19 published scenarios pass. **Still blocked:** halt/admission race F1; [re-review](spikes/m0-core-fault-fix-code-review.md). Repair `a8e4352` committed and under focused re-review; conditional integration assigned. |
| Safety/transport/Claude refusal integration `1f68da5` | Three reviewed legacy primitive repairs integrated; 149 tests pass. | Bounded approval only; full adapters not approved. [Review](spikes/m0-safety-lanes-review.md). |
| Demo runner `aba651b` | One-command fake-core bridge-death/fresh-client-result demonstration with guarded test result and private state. | [Approved wrapper](spikes/m0-demo-runner-review.md), conditional on core approval. Not a real-agent demo. |
| Job inspection `d54ff3a` | Authorized read-only CLI/IPC/MCP job listing. | [Changes required](spikes/m0-job-inspection-review.md): live paging overclaim and malformed MCP types. Repair `9fb08b6` under re-review. DB work remains unbounded despite output cap; small-checkpoint qualification, not scalable inspection. |
| IPC client deadlines `6850c0f` | Whole IPC budget, conservative uncertain acceptance after possible write, cancellation does not cancel daemon work. | [Independent bounded approval](spikes/m0-client-deadlines-review.md): 58 tests and 19 published scenarios pass. Does not close core F1 or bound synchronous private-file reads/serialization. |
| Teardown refusal `22e3598` | Refuses destructive Herdr name-based operations without identity-bound provider destruction. | [Review](spikes/m0-teardown-fence-review.md): refusal sound, structured CLI status needs correction; 159 tests pass. Refusal is not working automated teardown. |
| Codex control probe `73dab8d` | C# fake history/turn-race fixtures; no native model call. | [Changes required](spikes/codex-control-probe-review.md): required item fields and conservative pause/fence semantics. Fix `5dc3631` under re-review. Strict human-wins and visible TUI binding remain unproven. |
| Web plan revision `32fc8cc` | F27 English plan, static HTML mockup, explicit delegated authority and reload recovery. | [Design corrections approved](ui/operator-console-plan-rereview.md) as contracts only; user mockup feedback and runtime prerequisites still required. No runtime web UI. |

Review reports distinguish author evidence from independent reproduction. Their
exact source hashes remain authoritative; later fixes require their own review.
The legacy `AtfSpike` is an isolated experimental baseline. Consolidate reviewed
reusable behavior/tests into the real solution and retire superseded runtime
projects; do not delete live sessions or unique recovery evidence as file cleanup.

## Previous dispatch wave (superseded above)

This table records assignments, **not a live activity indicator**. Finished
workers must be retired after their handoff; waiting does not count as active.

| Agent / route | Bounded handoff |
| --- | --- |
| Codex `core-review-and-integration` / native subagents | Re-review `a8e4352`; if approved, merge core + approved IPC + demo into milestone integration and run combined published gates. Do not wait for independent inspection/probe/web lanes. |
| Codex `job-inspection-rereview` / native subagents | Re-review `9fb08b6` for the output-bounded, best-effort fake-checkpoint scope. |
| Codex `codex-probe-rereview` / native subagents | Re-review fake-only probe correction `5dc3631`. |
| Claude `m0-teardown-status` / win-agent-teams | Add structured refusal error status and correct operator instructions, without enabling unsafe teardown. |
| Pi Astra `native-downstream-delivery-research` / native subagents | Inspect user-supplied public commit `5149f3e1280749b52988a26952573cee61d2daff` on `feat/native-downstream-delivery`; assess fit, not implementation approval. |

[Whole-project HTML report](project-status.html) is now available locally; its
timestamped update distinguishes later handoffs from the original capture.
The admission, inspection and probe writers have committed their handoffs and
were stopped with `kill_agent`.

The four previously waiting MCP workers were stopped using `kill_agent`,
including the two legacy Codex workers. New Codex/Pi workers use native
subagents; only Claude uses win-agent-teams. Target five to six useful lanes
when independent work exists; do not create busywork or extend huge contexts.
Semantic runtime fixes remain Claude work followed by independent Codex review.

## Next testable checkpoint

The initial four steps — repair/re-review F1, merge core/IPC/demo, run combined
published gates, and expose the labelled checkpoint — are now satisfied on the
integration branch. Use the commands in the checkpoint ledger linked above.
Next: integrate reviewed inspection, check safe source promotion, and resolve
real native admission/binding contracts in parallel. Semantic conflict fixes
remain Claude work followed by Codex verification.

The real-agent E2E goal still requires a proven-owned visible native TUI, durable
MCP submission, bridge death, fresh-client result and same-conversation follow-up.
A fake child and a green test suite do not establish this goal.

## Important boundaries and external dependencies

- `.tools/dotnet11/` is the authorized isolated SDK
  `11.0.100-rc.1.26425.128`; select per command. Global SDK/PATH unchanged.
  Legacy experiment remains .NET 10 until deliberately retired/migrated.
- [Codex protocol evidence](spikes/codex-native-control-verification.md):
  `turn/start` can steer an active human turn and has no expected-idle guard.
  A second local idle read is not a fix. Public TUI-client/thread attestation
  was not found. Missing native contracts block that capability, not fake-core work.
- Managed Claude, Codex and Pi remain required; native Windows Pi and actual
  visible interactive versus explicit headless modes require their own evidence.
  Linux tests/cross-builds do not qualify Windows/macOS. No such native platform
  acceptance is claimed by this checkpoint.
- Native wake is standard. The public merged PR #70 reports upstream concept
  evidence, not complete local implementation; Windows-Claude safe refusal is
  not working wake support. Pi and remaining native transports need qualification.
- F27 console stays within Host and existing daemon authority. The static HTML
  mockup is not a service, scheduler or runtime capability. User visual feedback
  and design review remain separate gates.
- GitHub organization, publication/license and release signing decisions remain
  owner-dependent. No public release date follows from a planned wave diagram.
- Queued MCP follow-ups have no background dispatcher. Drain only the intended
  pending key; never blindly drain old messages to retired workers. Re-arm the
  one-shot handoff watcher after each consumed notification. Prefer committed
  artifacts over unsupported activity claims or repeated progress polling.
- Keep raw state, transcripts, credentials, evidence, build outputs and worktrees
  ignored. Review staged docs/source for personal paths before publication.
