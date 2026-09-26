# AgentTeamForge — contributor and agent instructions

## Project vision

Build a small, maintainable, local-first execution engine for coding agent teams,
with explicit process ownership and durable coordination rather than a broad
compatibility layer for other orchestration systems.

- A separate .NET daemon owns accepted jobs and coordination. Lead agents and
  other hosts are clients; their crashes must not destroy accepted work within
  its configured budget and permission limits.
- **Managed/spawned Pi agents are required**, alongside Claude Code and Codex.
  Plan Pi launch, same-session follow-up, status/result, approvals where exposed,
  interrupt/stop, and recovery through the same application contracts. This is
  not satisfied by attaching an externally launched Pi session or by Pi acting
  only as a lead host. Verify its native control path and supported modes.
  **Windows managed Pi execution is mandatory**, including real native launch,
  follow-up/results, interruption/stop and reconnect tests. Interactive mode uses
  a visible selected terminal tab; headless requires explicit choice. Linux tests
  or Windows cross-builds cannot establish this support.
- **Native session wake is the standard notification mechanism**, based on the
  public [PR #70 design](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70).
  Use authenticated host-native wake with notice-only payloads, generation-bound
  registration, coalescing, bounded retry and unread catch-up. Commit messages
  before wake; wake success is neither delivery acknowledgment nor model action.
  Do not require a watcher, terminal keystrokes or repeated model polling on the
  normal path. Manual read/catch-up is explicit degraded recovery, not a silent
  replacement for a required native-wake capability. The reference is not proof
  of Windows/macOS/Pi support: qualify each promised host/platform separately.
- Prefer SQLite for structured durable state; use files for worktrees, large
  logs, artifacts, and backend-required data.
- Keep MCP bridges thin. Do not build a custom model loop, distributed scheduler,
  or large terminal UI. Native AOT and memory savings must be measured, not assumed.
- Local use must work without an external orchestrator. Any future connector is
  strictly opt-in; do not import server, fleet, billing, or external domain
  dependencies into the core.
- Setup must require an explicit launch-mode choice: real interactive agent TUIs
  in Herdr on Linux, visible terminal tabs on Windows or macOS, or explicitly
  selected headless execution. Never silently fall back to headless.
- Interactive AgentTeamForge-launched agents are part of the PoC. A log-tail tab
  is not an interactive agent. Attaching arbitrary existing Desktop sessions is
  a separate future capability.
- Distinguish client-crash survival, daemon reconciliation, and backend/machine
  recovery. Do not promise seamless process adoption or exactly-once side effects.
- Claim platform support only with recorded tests on that platform. Missing
  Windows/macOS access blocks those gates; it does not remove their requirements.

## Architecture and vertical slices

- **Target .NET 11 for upcoming implementation.** RC1 and candidate SDK
  `11.0.100-rc.1.26425.128` are confirmed in public release sources; see
  [the research](docs/research/net11-process-api.md). Package, installed-ref-pack,
  platform, and AOT compatibility still require tests. Existing .NET 10 spike
  evidence remains version-specific; do not silently retarget a running
  experiment, install an SDK, or claim .NET 11/AOT compatibility from it.
- Follow [docs/architecture.md](docs/architecture.md). Organize work and code by
  **feature/vertical slice**, through exactly three production projects:
  **AgentTeamForge.Host → AgentTeamForge.Business → AgentTeamForge.DAL**.
- This is not Clean Architecture. Business references DAL directly; DAL does
  not reference Business. Do not introduce Business-owned repository ports or
  extra Domain/Application/Infrastructure/Contracts projects by default.
- Host owns CLI/setup presentation, MCP/IPC, composition, and daemon lifecycle.
  Business owns feature rules, orchestration, and backend/terminal integration.
  DAL owns storage records, SQL, migrations, and atomic persistence operations.
  Host may reference DAL for composition only, never to bypass Business in an
  endpoint. Bridge/client modes must not open the job database.
- Keep requests/results, helpers, and any necessary interfaces beside their
  feature. Do not scatter slices into global type-based folders or add a
  handler/validator/mapper/repository chain for every operation. Share only
  proven cross-feature needs; add interfaces for real boundaries/substitution.
- Deliver behavior end to end with pragmatic TDD, not all DAL work followed by
  all Business work followed by Host. Tests are not additional production layers.
- Human-facing UI includes setup/diagnostics and a **small text-only operator
  web console** requested for cross-platform debugging: agent status/output,
  human follow-up and confirmed whole-agent stop. Start with a plan and static
  HTML mockup; production implementation follows design review. Reuse existing
  daemon authority/use cases, default to authenticated loopback access, and do
  not build a terminal emulator, analytics dashboard or second scheduler.
  This scoped console supersedes earlier blanket no-web-UI exclusions.
  `Host` remains the project name; do not add a fourth UI production project.
- Use the real solution name **`AgentTeamForge.slnx`** and production project
  names from the outset of the new core; do not carry `AtfSpike` or `.Spike`
  naming into the product. Retire superseded spike projects and temporary
  scripts after reviewed code and meaningful regression tests have moved into
  the real solution. Preserve concise decisions/results, not duplicate runtimes
  or raw evidence in Git. Cleanup must not kill live sessions or erase unique
  recovery evidence; process cleanup requires verified ownership and authorization.

## Planning and review workflow

- The [full-product roadmap](docs/roadmap.md) uses a waterfall baseline with
  ordered phase entry/exit gates; [product scope](docs/product-scope.md) defines
  required capabilities versus decision-gated options. Phase plans and reviews
  may be authored in parallel. Implement vertical slices with TDD inside each
  phase; do not postpone testing or security until system qualification.
- A narrow demonstration is an explicitly labelled checkpoint, not completion
  of a full phase. Change contracts/scope visibly and update dependent plans.
- A **Claude Code orchestrator (Claude Opus)** leads. It does no hands-on
  implementation or review itself; its sole goal is shortest wall-clock
  delivery: keep a ready queue of small, bounded, testable slices and roughly
  **six to eight useful parallel workers**. Do not count waiting workers as
  active, manufacture busywork, or bypass prerequisite safety decisions.
- Spawn **all** workers through the **win-agent-teams** MCP (`spawn_agent`), not
  native subagents:
  - Planning, research and contract drafting: backend `pi`, tier `max`
    (GPT-6 Astra).
  - Implementation (runtime, tests, small docs fixes): backend `claude-code`,
    model `opus`, reasoning effort `medium` (`low` for trivial tasks).
  - Code, plan and contract review, branch integration, combined gate/test runs:
    backend `codex`, tier `high` (GPT-6 Sol).
- The previous orchestrator (Astra) may remain a subordinate external member via
  a win-agent-teams join ticket, taking tasks from the Claude orchestrator.
- Use fresh, bounded workers per slice with explicit file/worktree ownership and
  separate feature branches. Hand off well before roughly 200,000 context tokens.
  Workers report to `team-lead` with `send_message`: DONE/FAILED, commit sha and
  gate results. Once the handoff is captured, call `kill_agent`.
- Maintain an integration branch per milestone. The Codex integrator merges
  reviewed slices, resolves mechanical conflicts and runs combined gates;
  integration is not review approval and it never self-approves semantic
  changes. Route semantic/runtime fixes to a fresh Claude writer, then Codex
  re-review. Never resolve a conflict by dropping safety tests or weakening a
  contract. Keep merge inputs small, snapshot-bound and backed by an epic plan.

This section is the authoritative contributor review policy. General references
elsewhere to independent review must be interpreted using these rules.

1. Plans are normally written by **Claude Opus** or **GPT-6 Astra**. These are
   preferred planning models, not a claim that either is available in every host.
2. Match planning depth to change size. **Independent plan review is required
   only for major changes**: architecture changes, broad refactors, new subsystem
   or platform contracts, or changes to persistence/recovery, security, or delivery
   guarantees. Review the plan before implementation in those cases.
3. Small, localized changes do **not** require a separate plan-review step;
   independent code review is sufficient as the review gate. Tests and other
   applicable quality checks still apply.
4. **Code review must use the opposite model family from the implementation:**
   - Claude-written code (including Claude Code) → GPT/Codex reviewer.
   - GPT-written code (including Codex) → Claude reviewer.
   Switching harnesses without switching model family does not satisfy this rule.
   Mixed-authorship changes must receive opposite-family review for each part.
5. Use a separate reviewer session. Review the actual diff, relevant context,
   and test evidence against the requirements. The author must not self-approve.
   If the required reviewer is unavailable, report the review gate as blocked;
   do not silently substitute a same-family reviewer.
6. Use pragmatic **TDD (red → green → refactor)** for business-critical behavior:
   write a focused failing test, implement the smallest correct change, then
   refactor. Prioritize job state transitions, durable acceptance, delivery and
   reconciliation guarantees, ownership, permissions, budgets, and cancellation.
   Keep the suite lean and risk-driven: do not assert individual prose/UI strings,
   incidental formatting, private implementation details, or duplicate coverage
   merely to increase test counts. Exact values are appropriate when they are
   part of a machine-readable contract or correctness/security requirement.
   Exploratory spikes may establish feasibility first; record untested risks and
   add regression tests for critical behavior before promoting code into production.
7. Address review findings and obtain re-review of fixes before merge. Resolve
   blocking findings; explicitly record any accepted non-blocking findings and
   follow-ups.
8. Run applicable format, lint/check, build, and test gates after changes. For
   runtime changes, test the published binary and relevant AOT/platform behavior,
   not only `dotnet run`. Report failed, blocked, and unrun checks honestly.

Typical flows:

- Small change: brief approach → implementation/tests → opposite-family code
  review → fixes/re-review → full applicable gates.
- Major change: plan → independent plan review → implementation/tests →
  opposite-family code review → fixes/re-review → full applicable gates.

## Repository discipline

- Implement runtime code, spike harnesses, and automated tests in **C#/.NET**.
  This also applies to the M0 feasibility spikes: do not prototype the new engine
  or its control transports in Python. Minimal shell glue for launching commands
  is acceptable; other language exceptions
  require explicit user approval.
- Read [HANDOFF.md](HANDOFF.md) and its document map before implementation.
  Planning documents are not evidence of working software or passed tests.
- All project documentation must be in English; user conversation may be Swedish.
- Initialize Git or publish a repository only with explicit authorization.
  Once initialized, use a feature branch and dedicated worktree for major changes.
- Keep implementation and documentation changes within this repository unless
  separately authorized. Contributors must not need access to another project's
  source to understand or implement these contracts.
- Keep scope and capability changes visible. Do not weaken interactive-terminal
  requirements to simplify the PoC.
- Avoid explicit return types unless necessary. Prefer inference and real type
  safety; `as any` is a last resort.
- Standard format/lint/build scripts do not exist yet. Add reproducible tooling
  during implementation setup; until then, run relevant bounded document checks
  and state their limits.
