# M0 parallel execution and integration

## Target and limits

Deliver a runnable Linux checkpoint quickly: independent .NET 11 daemon,
SQLite acceptance, MCP bridge, client death, fresh-client result retrieval and
idempotent retry. Start with the real fake-child process; add a real Codex/Herdr
adapter only after its relevant safety findings pass independent review. This
is not full P02/P03 or Windows/macOS/Pi qualification.

The complete backlog is the [eight-phase roadmap](../roadmap.md). This document
allocates current work; it does not replace epic contracts or invent approval.
The [durable-core review](m0-durable-core-plan-review.md) conditionally approves
only S1–S4 Linux fake core. C2–C4 are now incorporated in its plan. C1 is satisfied
by explicitly authorized isolated .NET 11 RC1 SDK provisioning; compile/package
and AOT evidence still require execution.

## Parallel lanes

| Lane / branch | Writer and ownership | Deliverable / integration gate |
| --- | --- | --- |
| `spike/m0-durable-core` | Fresh Claude Opus/medium; only `spikes/m0-durable-core/` and necessary local build configuration. | Three-project fake-core slice with focused TDD, reproducible client-loss demo and report. Independent GPT review before promotion. |
| `spike/m0-codex-safety` | Fresh Claude Opus/medium; interactive spike launch ownership, Codex history/binding/foreign-turn logic and their focused tests. Do not edit transport/Herdr CLI files owned below. | Close applicable re-review findings or explicitly block unsolved native protocol gaps; do not advertise unsafe dispatch as supported. |
| `spike/m0-transport-bounds` | Fresh Claude Opus/medium; interactive spike JSON-RPC/WebSocket and bounded Herdr CLI I/O plus focused tests only. | Whole-operation deadlines, atomic pending quotas, bounded output and honest uncertain-write classification. |
| `integration/m0-e2e` | Independent Codex GPT-6 Sol, tier high; merge coordinator, not another feature author. | Small reviewed commits merged in dependency order, combined gates, conflict record and runnable checkpoint instructions. |

Each branch has its own worktree. No two agents write the same worktree or shared
contract. The integration branch may retain a clearly marked unapproved source
snapshot to make diffs/reviews possible; it cannot claim that baseline is safe.
Do not publish feature branches or raw evidence automatically.

## Handoff and merge order

1. Parent supplies pinned source baseline and file ownership. Authors use
   pragmatic red/green/refactor on business-critical failures, not text assertions.
2. Each writer reports an early bounded checkpoint, exact commit, affected files,
   commands/results, remaining blockers and contract changes. Local feature
   commits are allowed; no force pushes or rewriting another writer's work.
3. Independent GPT review covers Claude implementation. Review blockers return
   to a fresh bounded writer when appropriate, not an indefinitely growing session.
4. Codex integrator merges reviewed transport and safety fixes independently of
   the fake-core lane. Then merge the reviewed fake core. Run format/build/tests
   after each merge and rerun combined failure scenarios; record source hashes.
5. Conflict resolution must preserve both slices' invariants and tests. Sol-authored
   executable resolutions receive Claude review. Escalate semantic contract
   conflicts rather than choosing whichever branch makes compilation easier.
6. A fresh adapter-integration slice connects the reviewed real Codex path to the
   reviewed durable core. Keep fake-core and live-agent claims separate. A new
   contract change receives focused plan review, not another full roadmap cycle.
7. Use `AgentTeamForge.slnx` and `AgentTeamForge.Host`, `.Business`, `.DAL`
   from the outset. Once replacements pass review and equivalent meaningful
   regression tests, consolidate into the product solution and remove superseded
   spike projects/helpers/scripts. Preserve concise architectural decisions and
   result summaries; do not leave duplicate runtime implementations indefinitely.
   Never remove unique recovery evidence or terminate live sessions as file cleanup.
8. Parent promotes only tested reviewed integration commits. No automatic merge
   to `main`, GitHub push, platform/service change or teardown of existing agents.

Prefer bounded work packets and early handoffs, well before approximately 200,000
context tokens. Retire finished agents. A waiting review should not block another
independent safe slice; unresolved native ownership/correlation must still block
unsafe reuse.

## Toolchain and evidence

.NET 11 RC1 `11.0.100-rc.1.26425.128` is installed in ignored `.tools/dotnet11/`
in the main checkout, with the official archive SHA-512 verified. Use explicit
command-scoped `DOTNET_ROOT`/`PATH`; do not change the global SDK or retarget the
.NET 10 interactive experiment silently. That experiment retains its own toolchain.
Documentation/publication and organization setup are independent of runtime work.

The old M0 implementation agent was terminated at the user's request; its source,
reports and live experimental resources are preserved. The replacement lanes do
not own those live sessions. Any live smoke must use separately authorized,
proven-owned disposable resources, not blanket cleanup.
