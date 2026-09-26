# AgentTeamForge

**A durable execution engine for coding agent teams — local first, connected when you choose.**

AgentTeamForge aims to let a lead agent start, control, and monitor teams of
Claude Code, Codex and Pi agents. A separate .NET process owns the work and its
state. MCP clients can connect, disconnect, and reconnect without their lifetimes
controlling the subagents' lifetimes. Any external orchestrator integration
must be explicitly opt-in.

## Status

The repository root now contains a **Linux fake-core checkpoint**, not a
finished agent-team product. `AgentTeamForge.slnx` contains the three production
projects **Host → Business → DAL**, plus tests. The checkpoint exercises a
.NET 11 daemon, SQLite persistence, private IPC, a thin MCP bridge and a fake
child process; it does not launch Claude Code, Codex or Pi.

The reviewed checkpoint promoted to `main` (`d7d24ae`) passed 76 tests, 22
published Native AOT scenarios and one published-binary demo on Linux x64; one
earlier run had an intermittent 21/22 scenario failure under investigation. See the
[source-bound gate record](docs/spikes/canonical-wave-integration.md). No full roadmap phase, real-agent
E2E, Windows/macOS support or production service is claimed.

Product name: **AgentTeamForge**. Repository name: **`agent-team-forge`**.
`atf` is the checkpoint executable name, not an installed system command.
GitHub publication and licensing remain owner decisions. Keep raw evidence,
session state, credentials and runtime databases out of commits.

## Quick start (Linux)

Build `atf` with the pinned SDK, then select a launch mode explicitly:

```bash
DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet
export DOTNET_ROOT="$(dirname "$DOTNET")"
"$DOTNET" build src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -c Release
ATF="$(pwd)/src/AgentTeamForge.Host/bin/Release/net11.0/atf"
"$ATF" setup --mode headless --apply
"$ATF" start
```

Use `--mode herdr` to record an interactive Herdr choice. Omit `--apply` to print
the Claude Code and Codex MCP registration commands for review. `setup` creates
private state under `$XDG_STATE_HOME/agentteamforge`, or
`~/.local/state/agentteamforge`; `--state-dir DIR` overrides it. `start` prints
the daemon PID and is safe to repeat. `atf stop` prints the `kill -TERM` command
for the running daemon. This checkpoint still dispatches the fake backend; the
selected mode is persisted for subsequent real-agent implementation.

Run `atf prune --dry-run` to count expired jobs, then `atf prune` to remove them.
`--older-than 30d` is the default; both commands accept `--state-dir DIR` and
require the daemon to be running. The daemon also prunes once at startup and
every 24 hours. Set `auto_prune` to `false` or `prune_older_than_days` to a
positive number in the private `profile.json` to change that schedule. Pruning
removes completed and failed jobs, their stored output/events/runs, and matching
files in `logs/<job-id>.log`. Active jobs and parents of active follow-ups remain.

## Run the bounded checkpoint

From the repository root, with the pinned .NET SDK and Linux native build
prerequisites already available:

```bash
./scripts/demo.sh    # Build the JIT apphost and run one fake-core scenario
./scripts/verify.sh  # Restore, format check, Release build, tests, AOT and smoke

# Optional: run the demo against the exact native binary printed by verify.sh
ATF_DEMO_BIN="/path/printed/by/verify/atf" ./scripts/demo.sh
```

Scripts discover `.tools/dotnet11/dotnet` through Git's common directory,
including from linked worktrees. The pinned SDK is
`11.0.100-rc.1.26425.128`. Override with `DOTNET=/path/to/pinned/dotnet` when
needed; scripts do not install an SDK or alter global PATH. AOT requires the
platform's native compiler/linker prerequisites. Package restore may need
network access. A fresh checkout must provision these prerequisites first.

The demo covers MCP submission, bridge death, fresh-client result retrieval,
same-key replay and daemon-restart recovery using a fake backend. It does not
contact models, install a service or control Herdr. Native artifacts use a
unique directory per verification run; use the printed path, not a guessed
fixed output directory. Only Linux x64 has recorded checkpoint evidence.

## MVP: real agents from Claude Code (Linux)

`atf init --state-dir DIR` now defaults to real agent backends (`--backends fake`
or `--test-profile` keeps fake only); `atf daemon --state-dir DIR` runs them.
Register the MCP bridge with `claude mcp add atf -- /path/to/atf mcp --state-dir DIR`
and use `submit_job(backend, instruction, cwd?, idempotency_key)`, `get_job`,
`follow_up(job_id, instruction, idempotency_key)` (resumes the job's native
session on the same backend/cwd) and `list_jobs`. A running parent returns
`parent_not_ready` by default. Set `interrupt: true` in MCP or `--interrupt` on
`atf client follow-up` to cancel its current turn with reason `interrupted` and
run the new prompt in the same session. For a finished parent, the flag acts like
a normal follow-up. In headless mode the current process ends before resume; in
Herdr mode Escape interrupts the live TUI turn and the next prompt goes to its
existing tab. CLI equivalents:
`atf client submit|get|follow-up|list`. Add `worktree: true` to `submit_job` or
`--worktree` to `atf client submit` with a git `cwd` to run in a separate checkout
under the daemon state directory. The job view and list show its path and branch;
follow-ups reuse it. Worktrees remain for manual inspection and cleanup.
`stop_job(job_id)` (`atf client stop ID`) cancels a queued or running job and kills
its process tree; the job ends `cancelled` and its session can still be followed up.
`timeout_s` on `submit_job`/`follow_up` (`--timeout S`) cancels a running job with
reason `timeout`; `queue_ttl_s` (`--queue-ttl S`) cancels one that has not started
in time (reason `queue_ttl`). Both are off by default.
`scripts/demo-real.sh [claude|codex|pi]` is the
opt-in end-to-end check (spends tokens); `fake` is a plumbing dry run. Jobs run one at
a time and agents run headless with bypassed permissions.

## Reading order

| Document | Contents |
| --- | --- |
| [Handoff](HANDOFF.md) | Current status, constraints, validation boundaries, and starting context. |
| [Linux MVP project status](docs/project-status.html) | Current mainline progress, ordered lanes, dependencies and blockers. |
| [Contributor instructions](AGENTS.md) | Authoritative project and review policy. |
| README (this file) | Short project orientation and status. |
| [Setup choices and terminal requirements](docs/terminal-modes.md) | Mandatory choice of interactive or headless execution; Herdr, Windows, and macOS. |
| [Architecture](docs/architecture.md) | Feature-first vertical slices, Host → Business → DAL, process boundaries, and design rationale. |
| [Architecture and implementation plan](docs/plan.md) | Goals, current state, design, data model, delivery guarantees, security, and risks. |
| [PoC specification](docs/poc.md) | Exactly what the prototype must and must not demonstrate, experiments, and acceptance criteria. |
| [Full product scope](docs/product-scope.md) | Feature inventory, supported-product acceptance, and option boundaries. |
| [Waterfall roadmap](docs/roadmap.md) | Eight ordered phases, phase plans, dependencies, gates, early demo and full release. |
| [Phase planning brief](docs/planning/full-product/README.md) | Shared requirements and phase-document ownership. |
| [Operator console plan](docs/ui/operator-console-plan.md) / [HTML mockup](docs/ui/operator-console-mockup.html) | Planned local text-only agent status, human follow-up and confirmed stop; static mockup, not a working runtime UI. |
| [Durable-core spike plan](docs/spikes/m0-durable-core-plan.md) | Original bounded fake-backend scope and crash gates; see the integration record for implemented checkpoint evidence. |
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
  confirms the release and SDK. The bounded Linux x64 checkpoint has .NET 11
  Native AOT evidence; other platforms and real backends still need qualification.
  Existing .NET 10 experiments do not establish .NET 11 support.
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

Fix the intermittent published-scenario failure and continue reviewed slices
through milestone integration branches. See [implementation status](docs/implementation-status.md)
for the bounded snapshot and [the roadmap](docs/roadmap.md) for full-product gates.
Separate lanes cover inspection/hardening, native-control qualification and the
operator console. The console remains a reviewed design and static mockup at
this checkpoint, not a runtime capability. Real-agent E2E remains a separate gate.

## Development rules

- Local Git and `main` are initialized. Create a feature branch and dedicated
  worktree for each major change; public repository creation/push remains an
  explicitly authorized action.
- Follow [AGENTS.md](AGENTS.md) for project vision and contributor rules.
- Plans normally come from GPT-6 Astra (Pi tier max); Claude Opus writes code;
  Codex (tier high) reviews and integrates. Independent plan review
  is required only for major changes; small changes need code review, not a
  separate plan review.
- Implementation → red/green/refactor → opposite-family code review
  (Claude code reviewed by GPT/Codex; GPT code reviewed by Claude) → full gates.
- Test the published binary, not only `dotnet run`.
- Keep platform-specific process rules and backend versions visible in tests.
- Run `./scripts/verify.sh` for format, warnings-as-errors build, tests and
  published AOT smoke gates. Use the printed binary path for the AOT demo.
- Reproducible Markdown linting and link checks remain a tooling follow-up.
