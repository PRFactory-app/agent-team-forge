# AgentTeamForge — contributor and agent instructions

## Working principles (read first; these override older ceremony elsewhere)

- **KISS, DRY, good enough.** The goal is a tool that runs on Linux within
  hours, not a pile of contracts first. Ship working code, then improve it.
- **Copy the reference model.** The owner's win-agent-teams MCP
  (`/home/mikael/code/github/agentic-coder-teams-mcp`) already works. Reuse its
  proven approaches — spawn/resume CLI invocations, bypass permissions, simple
  session tracking — instead of inventing new protocols.
- **Bypass permissions, minimal operator intervention.** Agents run unattended.
  No human-approval ceremony unless an action is destructive (deleting data,
  killing processes you don't own, publishing).
- **Linux first and complete.** Windows/macOS come last and are finished on
  those machines. Never block Linux work on other platforms; just don't claim
  platform support that hasn't been tested there.
- **Plans:** a short slice list is enough. No multi-round plan/contract reviews
  for normal features. Only genuinely risky changes (data loss, security holes)
  get a plan review — one round, more only if a real bug is found.
- **Code review:** one opposite-family review per slice (Claude code → Codex;
  GPT code → Claude), focused on real bugs — not prose, hypothetical edge cases
  or process. Reviewers must not block on "missing contract/owner decision":
  flag it and approve if the code works and is tested.
- **Tests:** a few focused tests on critical behavior (job state, durability,
  cancellation, permissions). No characterization or test-only slices for
  their own sake; no assertions on prose or formatting.
- **Planners and reviewers prefer deleting scope over adding gates.**

## Product direction

- A local .NET daemon owns accepted jobs and coordination; lead agents and
  hosts are clients whose crashes must not lose accepted work.
- Managed agents: Claude Code, Codex and Pi (launch, follow-up, status/result,
  stop, recovery through the same contracts).
- Notifications use native session wake as in the reference
  ([PR #70](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70)):
  commit the message first, then a notice-only wake; manual read is fallback.
- SQLite for structured state; files for worktrees, logs and artifacts.
- Launch mode is an explicit setup choice: interactive agent TUIs (Herdr on
  Linux, terminal tabs on Windows/macOS) or headless. Never silently fall back
  to headless. A log-tail tab is not an interactive agent.
- Keep MCP bridges thin. No custom model loop, distributed scheduler or large
  terminal UI. A small operator web console (status, output,
  follow-up, stop; no HTML from agents) on authenticated loopback is in scope.
- Local use needs no external orchestrator; any connector is opt-in.

## Architecture

- Target .NET 11 (`.tools/dotnet11/`, see
  [ADR 0001](docs/adr/0001-dotnet-11-native-aot.md)); measure AOT, don't assume.
- Solution `AgentTeamForge.slnx`, three production projects:
  **AgentTeamForge.Host → AgentTeamForge.Business → AgentTeamForge.DAL**
  (see [docs/architecture.md](docs/architecture.md)). Not Clean Architecture:
  Business uses DAL directly; no repository ports or extra projects.
- Host: CLI, setup, MCP/IPC, composition, daemon lifecycle, web console.
  Business: feature logic and backend/terminal integration. DAL: SQL,
  migrations, persistence. Bridge/client modes don't open the database.
- Organize by feature/vertical slice. No handler/validator/mapper chains per
  operation; add interfaces only for real substitution.
- Retire `AtfSpike`/spike code once its useful parts live in the real solution.

## Team workflow

- A Claude Code orchestrator (Opus) leads and does no hands-on work; it keeps
  up to 14 useful agents busy on small slices, spawned via win-agent-teams
  `spawn_agent`:
  - Planning/research: backend `pi`, tier `max`.
  - Implementation: backend `claude-code`, model `opus`, effort `medium`
    (`low` if trivial).
  - Review, integration, combined test runs: backend `codex`, tier `high`.
- One fresh worker per slice, own branch and worktree. Workers report
  `DONE/FAILED`, commit sha and test results to `team-lead` via
  `send_message`; then the orchestrator calls `kill_agent`.
- The Codex integrator merges reviewed slices and runs the build/tests. It may
  fix mechanical conflicts; semantic fixes go to a Claude writer. Never resolve
  a conflict by dropping tests.
- Before merge: build and tests pass; for runtime changes, run the published
  binary once. Report failures honestly.

## Repository discipline

- Runtime code and tests in C#/.NET; minimal shell glue is fine. Other
  languages need owner approval (static HTML/CSS mockups are allowed).
- Documentation in English; conversation may be Swedish.
- Stay within this repository. Don't kill processes you don't own.
- Prefer type inference over explicit return types.
- The document map is in [CONTRIBUTING.md](CONTRIBUTING.md#documentation-map);
  decisions are in [docs/adr](docs/adr/README.md). Planning docs are not
  evidence of working software.
