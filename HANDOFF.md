# AgentTeamForge — handoff for a fresh session

## 1. Start here

Work from the root of your `agent-team-forge` checkout. All paths in this
handoff are repository-relative unless explicitly marked otherwise.

This is a **planning and M0 exploration directory**, not a working product.
The user authorized the first interactive-control spike in C#/.NET, using Claude
Opus at medium effort, with no plan review for that spike. An initial Python
implementation attempt was stopped and redirected to C#/.NET. Earlier non-.NET
snippets do not qualify as M0 implementation. The concurrent spike's runtime
results are not validated by this architecture/documentation update.

The **complete waterfall product plan** has one GPT-6 Astra/medium-authored
plan per phase. Current priority is continuous bounded implementation toward a
runnable E2E checkpoint: roughly six parallel lanes, Claude writing code and
Codex GPT-6 Sol high verifying/reviewing/integrating separate worktrees. See
[the execution allocation](docs/spikes/m0-integration-plan.md). A later user
addition requests a small text-only operator web console; Pi Astra/medium owns
its plan and static HTML mockup first, not production UI implementation. The near-term real Linux demo remains a
checkpoint, not the full scope. Interactive-spike safety repairs are separately
in progress after independent GPT review; no adapter safety approval is implied.
The full-product phase plans do not by themselves authorize implementation or
Git initialization/publication. No production service or working product CLI is
claimed. `atf` remains a provisional CLI name, not an installed command.

The user subsequently authorized Git initialization and a public GitHub repository.
Local Git now uses `main`; initial staging is documentation plus ignore rules,
not unreviewed spike source or raw evidence. GitHub creation/push is waiting for
the owner to create the selected organization. Do not publish under the earlier
personal-account destination without a new decision. No license has been selected. `.tools/dotnet11/` contains the separately
user-authorized SDK `11.0.100-rc.1.26425.128` with official SHA-512 verified;
command-scoped `DOTNET_ROOT`/`PATH` are required, global SDK configuration is
unchanged. The old M0 writer was killed at the user's request; its live test
sessions and source remain preserved. Fresh feature workers own the repairs.

All project documentation must be in **English**. The conversation with the user
may remain in Swedish. The user specifically requested a **Sol subagent** for the
translation of the initial Swedish planning documents.

## 2. User decisions — preserve these

1. Product name: **AgentTeamForge**. Directory/repository slug: `agent-team-forge`.
2. New standalone project, not an in-place rewrite of another orchestration system.
3. Prefer **.NET** because the user has extensive .NET experience and no Python
   background. **Native AOT** is a goal to test, not a proven memory saving.
4. A separate, durable local daemon owns jobs and coordination. A mother agent
   and other agent hosts connect as clients; their crashes should not destroy
   already accepted work.
5. Prefer **SQLite** for structured state. Files remain appropriate for worktrees,
   large logs, artifacts, and backend-required transcripts/configuration.
6. **External orchestrator integration must be opt-in.** Local use must work
   without it.
7. **Installation must ask how agents are launched:**
   - Interactive **Herdr on Linux**.
   - A **visible terminal tab on Windows**.
   - An equivalent **visible terminal tab on macOS**.
   - Headless is an explicit alternative, not a silent default/fallback.
8. Interactive means the **actual agent TUI**, usable by a human, while still
   controllable/observable through AgentTeamForge. A log-tail tab does not count.
9. Preserve the distinction between first-class AgentTeamForge-launched interactive
   agents and future attachment to arbitrary already-running Desktop sessions.
10. Preserve the detailed plan, roadmap, explicit PoC scope, and handoff.
11. Use feature-first vertical slices in three production projects:
    **AgentTeamForge.Host → AgentTeamForge.Business → AgentTeamForge.DAL**.
    Business references DAL directly; do not impose Clean Architecture or
    Business-owned repository ports. Host includes setup presentation, CLI,
    MCP/IPC, and daemon composition, so it is not named UI.
12. Use pragmatic TDD for business-critical behavior, not a large suite asserting
    prose or implementation details. Code reviews use the opposite model family.
    Plans normally come from Claude Opus or GPT-6 Astra; independent plan reviews
    are for major changes. See `AGENTS.md` for binding rules.
13. Use the P01–P08 waterfall roadmap for the complete product. Planning/reviews
    may be parallel; phase implementation consumes accepted predecessor
    contracts. Scope and options are explicit in `docs/product-scope.md`.
14. Spawn Pi planning/review agents through `subagent_spawn`; Claude Code
    coordination may continue through its configured agent-team tools.
15. Managed/spawned Pi is first-class alongside Claude Code/Codex in P03, not
    deferred to P06 or satisfied by attached/lead-only Pi. Native Windows Pi
    spawn/follow-up/results/interrupt/stop/reconnect must pass in selected
    interactive and explicit headless modes; Linux/cross-builds are not proof.
16. Native session wake per public [PR #70](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70)
    is the standard mechanism, not opt-in or watcher-driven normal operation.
    Messages commit independently of wake; recipients use authenticated read/ack.
    Manual catch-up is explicit degraded recovery, not a silent native-wake waiver.
    Reference Windows-Claude/macOS/Pi transport gaps need actual feasibility and
    platform evidence; the PR's Linux results do not establish them here.

**Superseded earlier advice:** “managed headless first; interactive terminals
later” is no longer the plan. The user explicitly added the installation/terminal
requirement while the documents were being prepared. Do not reintroduce that
scope cut in an attempt to simplify the PoC.

Windows Terminal is a proposed first Windows provider. The user did not choose
Terminal.app versus iTerm2 on macOS; select a supported provider through a spike
and record the decision. Do not claim either is already implemented or verified.

## 3. Read in this order

1. [Handoff](HANDOFF.md) — this status, constraints, and validation boundary.
2. [Contributor instructions](AGENTS.md), then [README](README.md) — policy and
   orientation.
3. [Terminal modes](docs/terminal-modes.md) — mandatory user requirement and
   interactive contract.
4. [Architecture](docs/architecture.md) — selected feature-first three-project
   structure; then [implementation plan](docs/plan.md) for storage, guarantees,
   and security.
5. [PoC specification](docs/poc.md) — scope, failure matrix, live tests and go/no-go.
6. [Product scope](docs/product-scope.md), [waterfall roadmap](docs/roadmap.md),
   and [phase plans](docs/planning/full-product/README.md) — complete feature
   inventory, P01–P08 dependencies and gates. The [next spike plan](docs/spikes/m0-durable-core-plan.md)
   and [demo plan](docs/spikes/e2e-demo-plan.md) are bounded early checkpoints.
7. [Independent plan review](docs/plan-review.md), then
   [review resolutions](docs/review-resolutions.md) — original findings and
   their in-place dispositions, not reviews of the later architecture/spike docs.

Claude Opus completed the independent review and re-review. The appended
re-review approves the plan **for M0 investigation**, with no remaining blocking
findings. Original findings are preserved; resolutions and final editorial
clarifications are recorded separately. This is a design assessment, not runtime
approval. M0's actual results still require their own exit-gate review. The new
architecture and durable-core spike plan have not inherited that earlier
approval; review the persistence/lifetime plan before implementing the next spike.

## 4. Why this project exists

Agent-team coordination needs durable ownership outside the lead agent's
lifetime. A small local daemon can separate accepted work, message delivery,
backend control, and recovery without implementing its own model loop.
SQLite provides atomic structured state rather than cross-file transactions.

The goal is a lighter, maintainable runtime with measurable behavior, not a
compatibility port. Thin MCP clients and an optional external connector share
the local API; neither may become the mandatory owner of local jobs. Native
wake, interactive control, and AOT still need independent feasibility evidence.

## 5. Evidence and contributor context

The repository's design contracts are self-contained. Read the document map
above and the public official sources in [the plan](docs/plan.md#14-sources).
No external project's source or local worktree is a prerequisite.

Planning documents and historical reviews are not runtime evidence. New
experiments must record exact backend/SDK versions, platform, launch context,
commands, and sanitized outcomes. Existing .NET 10 spike evidence applies only
to that version. Upcoming work targets .NET 11. [RC1 research](docs/research/net11-process-api.md)
confirms the public release and candidate SDK `11.0.100-rc.1.26425.128`; actual
package, platform, installed-ref-pack, and AOT compatibility remain untested.

## 6. Technical boundaries worth remembering

### Crash guarantees

- Client/mother-agent crash: accepted jobs keep running within their policies.
- Daemon crash: durable state survives; reconcile runners and backend receipts.
  Seamless continuation of inherited stdin/stdout is not promised.
- Backend/machine crash: session resume may be possible, but external side effects
  cannot generally be guaranteed exactly once.

A live PID alone proves neither ownership nor safe adoption. Generation fencing
protects database updates, not concurrent writes by an orphaned agent process.

### Backend and terminal control

Separate four axes: backend, interactive/headless mode, terminal provider, and
control/result transport. Codex app-server and Claude `stream-json` are headless
candidates. Do not assume they transparently control an existing TUI process.

Native wake may depend on the notifying process belonging to the host's own
process tree. Verify whether a host-local bridge is required; do not assume a
detached daemon has that authority. Generic MCP notifications do not guarantee
a new model turn.

For interactive sessions, opening a tab is only the first test. Verify stable
session identity, actual human input, mother-agent follow-up, authoritative
completion, targeted turn interrupt/agent stop, and survival of the
mother-agent bridge's death.

### SQLite and delivery

Acceptance atomically persists the job, idempotency record, and a queued,
unattempted outbox row. Before any process spawn, terminal launch, or prompt
write, a separate transaction persists the run generation, correlation ID, and
attempt-start marker. Queued work with no attempt is safe to dispatch; an
attempt with no authoritative evidence is uncertain. Keep accepted,
backend-acknowledged, completed, and needs-reconciliation distinct. Never
blindly resend a potentially delivered prompt after a lost response. Read/ack
cursors must not skip unread events; client delivery and model self-report are
not proof of completed model action.

Interactive completion requires a run-correlated authoritative lifecycle signal
or verified durable backend session record. Human/foreign activity is distinct
from daemon jobs and pauses automatic follow-ups until explicit reconciled idle.
On daemon recovery, never automatically kill a live interactive TUI: verify and
rebind it, or leave it live and block further machine work pending operator
reconciliation. Completion during an outage must be replayable, preferably
from backend durable records and only otherwise from a bounded private helper
spool. Kill-on-close policies apply only to explicitly approved headless modes.

### Scope discipline

Do not import external server/fleet/billing/domain dependencies. Start with a
small runtime and a future connector boundary. Do not build a custom model loop,
large terminal UI, distributed scheduler, or full legacy compatibility layer.

## 7. What the next session should do

First, read the documents and any review dispositions. Then respond to the new
user request rather than treating this handoff as unconditional permission to
implement everything.

If authorized to begin M0:

1. Confirm available OS test environments, CLI/terminal versions, and model-test
   budget. Do not claim Windows/macOS GUI support from a Linux-only test run.
2. Resolve the highest-risk requirement first: real Claude/Codex TUIs in Herdr
   with safe native machine control, run-correlated authoritative result
   evidence, foreign/human activity handling, and outage replay. Keystroke
   injection is not a supported v1 delivery path.
3. Record the backend × mode × terminal capability matrix and macOS provider
   decision. Also record platform × mode × production-launch-context
   `PATH`, home/profile, configuration, and authentication behavior; identify
   whether a desktop-session launcher is required.
4. Test a minimal AOT/MCP/IPC/SQLite slice with a fake backend and a client-kill
   experiment, including the queued-acceptance versus attempt-start crash
   boundary and MCP registration under trim/AOT. No full product or real
   external orchestrator connector yet.
5. Save evidence, explicit blocked gates, and ADRs; revise the plan only through
   visible decisions. Missing platform access does not erase its requirements.

The Linux reference PoC can be assessed separately. The full PoC also requires
the Windows and macOS spikes; an unavailable test environment blocks that
platform's gate rather than waiving it.

Local Git is initialized with the user's authorization. Before feature
implementation, use a feature branch and dedicated worktree. Do not modify
unrelated repositories. Public creation/push waits for the selected organization;
review the exact staged tree and publication boundary before pushing.

## 8. Validation status at handoff

This handoff update is documentation only. It makes no claim about concurrently
added exploratory spike code, and no backend smoke, .NET build, runtime test,
AOT publish, service installation, or crash experiment was run by this
documentation session. Only the bounded document format, internal-link, and
test-ID consistency checks recorded in the final task report apply here. Root
ignore rules now exclude harness state, raw evidence, credentials, runtime data
and generated outputs. Ignore rules alone do not clear files for publication.

The independent Claude Opus review and successful re-review are at
`docs/plan-review.md`. Dispositions and the five minor follow-up clarifications
are in `docs/review-resolutions.md`. Approval is limited to starting M0
investigation; no implementation or live capability has been approved.

Bounded checks passed for Markdown UTF-8/newlines/whitespace, balanced code
fences, local files and heading links, and contiguous C01–C32, L01–L12, and
T01–T19 definitions. These are documentation checks, not a substitute for the
future repository's format/lint/build/runtime gates.
