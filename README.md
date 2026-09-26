# AgentTeamForge

**A durable execution engine for coding agent teams — local first, connected when you choose.**

AgentTeamForge aims to let a lead agent start, control, and monitor teams of
Claude Code, Codex and Pi agents. A separate .NET process owns the work and its
state. MCP clients can connect, disconnect, and reconnect without their lifetimes
controlling the subagents' lifetimes. Any external orchestrator integration
must be explicitly opt-in.

## Status

This is a **planning and M0 exploration directory**, not a working product.
The first interactive-control spike is being developed under `spikes/` in
C#/.NET. Earlier non-.NET exploratory code does not qualify as M0 implementation;
its observations require verification. This architecture/documentation update
makes no claim that the concurrent spike's runtime tests passed.

The selected architecture is feature-first **Host → Business → DAL**, with
three production projects, not Clean Architecture. The next durable-core spike
is planned, not implemented by this documentation work. No production service,
working product CLI or external orchestrator integration is claimed. Local Git
is initialized on `main`; GitHub publication awaits the selected organization's
creation. The full-product waterfall plan now covers
P01–P08 from requirements/design through public release; these are planned
phases, not completed capabilities.

The initial publication scope is documentation and ignore rules only. Local
spike source remains outside that first commit pending review; sanitized spike
reports describe experiments, not supported product capabilities. Raw evidence,
session state, credentials and runtime databases must never be staged by default.

Product name: **AgentTeamForge**. Repository name: **`agent-team-forge`**.
The CLI name will be decided later; `atf` is a working name in these documents,
not an available command yet.

## Reading order

| Document | Contents |
| --- | --- |
| [Handoff](HANDOFF.md) | Current status, constraints, validation boundaries, and starting context. |
| [Contributor instructions](AGENTS.md) | Authoritative project and review policy. |
| README (this file) | Short project orientation and status. |
| [Setup choices and terminal requirements](docs/terminal-modes.md) | Mandatory choice of interactive or headless execution; Herdr, Windows, and macOS. |
| [Architecture](docs/architecture.md) | Feature-first vertical slices, Host → Business → DAL, process boundaries, and design rationale. |
| [Architecture and implementation plan](docs/plan.md) | Goals, current state, design, data model, delivery guarantees, security, and risks. |
| [PoC specification](docs/poc.md) | Exactly what the prototype must and must not demonstrate, experiments, and acceptance criteria. |
| [Full product scope](docs/product-scope.md) | Feature inventory, supported-product acceptance, and option boundaries. |
| [Waterfall roadmap](docs/roadmap.md) | Eight ordered phases, phase plans, dependencies, gates, early demo and full release. |
| [Phase planning brief](docs/planning/full-product/README.md) | Shared requirements and phase-document ownership. |
| [Next M0 spike plan](docs/spikes/m0-durable-core-plan.md) | Bounded fake-backend MCP/IPC/SQLite/AOT slice, TDD and crash gates; implementation awaits plan review and authorization. |
| [Independent plan review](docs/plan-review.md) | Original Claude Opus findings and re-review for the earlier M0 plan; not approval of later architecture/spike documents. |
| [Review resolutions](docs/review-resolutions.md) | Per-finding dispositions, corrections, and document-validation boundaries. |

## Direction at a glance

- Feature-first vertical slices in three C#/.NET projects: **Host → Business →
  DAL**. No mandatory Clean Architecture ports or extra layers.
- First-class spawned Claude Code, Codex and Pi, including native Windows Pi
  lifecycle tests; not an attach-only or Linux-only feature.
- Native session wake is standard, following the public
  [PR #70 mechanism](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70).
  Durable inbox/read-ack remains authoritative; wake only signals unread work.
  Missing Windows/macOS/Pi transport evidence remains a gate, not implied support.
- One daemon per user and machine, rather than one .NET process per agent.
- Thin MCP bridges; orchestration logic and durable state live in the daemon.
- **Mandatory setup choice:** interactive agents in Herdr on Linux, visible
  terminal tabs on Windows or the equivalent on macOS; alternatively, an
  explicitly selected headless mode. No silent switch to headless on terminal
  failure.
- Interactive means a real agent TUI with human interaction, not a log tail.
  Interactive agents launched by AgentTeamForge are part of the PoC.
- Separate adapters for terminal launch and backend control. Codex app-server
  and Claude stream-json are headless candidates, not automatic TUI transports.
- Attaching to arbitrary, already open Desktop sessions remains a separate
  future feature.
- SQLite for jobs, messages, and the delivery journal. Files for large logs,
  worktrees, artifacts, and files required by the backends themselves.
- **.NET 11** is the selected target. [RC1 process-API research](docs/research/net11-process-api.md)
  confirms the release and candidate SDK; package, platform, and Native AOT
  tests remain. Existing .NET 10 experiments do not establish .NET 11 support.
- No automatic cloud connections, mandatory external orchestrator integration,
  or silent upload of prompts or code.

## Three different guarantees

1. **Client crash:** already accepted jobs can continue, and results can be
   retrieved after reconnection, within explicit budget and permission limits.
2. **Daemon crash:** state is restored and runs are reconciled. Seamless
   continuation of every agent process is not promised.
3. **Agent/machine crash:** the job is interrupted or requires recovery.
   Backend session resume does not guarantee exactly-once execution.

These are design goals. They count as verified only after the corresponding
roadmap gates pass with recorded evidence.

## Next steps

Baseline and review the [complete product scope](docs/product-scope.md) and
[ordered phase plans](docs/roadmap.md). Continue the separately authorized
interactive-spike safety repairs and re-review before promoting its adapters.
The [durable-core spike](docs/spikes/m0-durable-core-plan.md) and
[real Linux demo](docs/spikes/e2e-demo-plan.md) are early checkpoints in P02/P03,
not substitutes for the full product. The first spike's plan-review exemption
does not extend to later persistence/lifetime design changes.

## Development rules for a future implementation

- Local Git and `main` are initialized. Create a feature branch and dedicated
  worktree for each major change; public repository creation/push remains an
  explicitly authorized action.
- Follow [AGENTS.md](AGENTS.md) for project vision and contributor rules.
- Plans normally come from Claude Opus or GPT-6 Astra. Independent plan review
  is required only for major changes; small changes need code review, not a
  separate plan review.
- Implementation → red/green/refactor → opposite-family code review
  (Claude code reviewed by GPT/Codex; GPT code reviewed by Claude) → full gates.
- Test the published binary, not only `dotnet run`.
- Keep platform-specific process rules and backend versions visible in tests.
- Proposed gates: `dotnet format --verify-no-changes`, a build with warnings as
  errors, the full test suite, and AOT publish/smoke. Exact commands will be
  added to the relevant solution when implemented; these proposed product gates
  are not claims about concurrent spike tooling.
- Add reproducible Markdown linting and link checks when build tooling is set up.
