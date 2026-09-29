# Architecture

How AgentTeamForge (ATF) is put together and how its parts fit. Decisions and
their reasons are in the [ADRs](adr/README.md); tables are in the
[data model](data-model.md).

## Overview

ATF is one executable, `atf`, started in different roles:

```text
lead agent (Claude Code / Codex / Pi)
   │ stdio MCP
   ▼
[atf mcp]  ──────┐
[atf client] ────┤  authenticated local IPC
[web console] ───┤  (served by the daemon, loopback HTTP)
                 ▼
            [atf daemon] ── SQLite jobs.db, logs, worktrees
                 │
                 ├── headless agent processes
                 ├── interactive agent TUIs (Herdr / Windows Terminal / macOS tabs)
                 ├── native wake notices to lead hosts
                 └── optional PRFactory connector (outbound HTTPS)
```

- **Daemon** (`atf daemon`): the only process that opens the database. It
  accepts jobs, dispatches them to backends, stores results, sends wake
  notices, serves the web console and runs the optional PRFactory connector.
  One per state directory, guarded by `daemon.lock`. It runs outside the
  lead's process tree ([ADR 0003](adr/0003-daemon-owns-jobs-sqlite.md)).
- **MCP bridge** (`atf mcp`): a thin stdio MCP server per lead session. It
  registers the lead session and its wake target, then forwards tool calls
  over IPC. It owns no jobs; killing it cannot stop work. It starts the daemon
  lazily if needed. MCP stdout carries protocol only; diagnostics go to stderr.
- **CLI** (`atf client …`, `setup`, `doctor`, `prune`, `web`, `prfactory`):
  short-lived clients over the same IPC.
- **Agents** are external programs: Claude Code, Codex, Pi, Cursor CLI and Factory Droid. ATF has no model
  loop of its own.

## Projects

```text
AgentTeamForge.Host → AgentTeamForge.Business → AgentTeamForge.DAL
```

Organized by feature, not by layer ([ADR 0002](adr/0002-three-projects-vertical-slices.md)).

| Project | Owns | Key folders |
| --- | --- | --- |
| Host | Entry points and composition: CLI, setup/doctor/install, MCP bridge, IPC server/client, daemon lifecycle, web console, PRFactory connector | `Program.cs`, `Hosting/`, `Transport/`, `Features/{Jobs,Setup,Wake,WebConsole,PRFactory}` |
| Business | Behavior: accept, dispatch, follow-up, stop, recovery, model selection, backends, terminals, wake, external teams | `Features/Jobs`, `Features/Agents/{Backends,Terminals}`, `Features/Wake`, `Features/External`, `Features/Recovery` |
| DAL | SQLite connection settings, migrations, feature stores with explicit SQL | `Sqlite/`, `Migrations/Schema.cs`, `Features/{Jobs,Sessions,Wake,External}` |

Host parses input, authenticates the transport and maps results; it does not
decide job transitions or run SQL. Business decides; DAL owns each atomic
transaction. There are no repository ports or mediator layers.

## IPC and identity

- Linux and macOS: Unix socket `daemon.sock` in the owner-only state
  directory. Windows: a named pipe restricted to the current user.
- Clients authenticate with the owner-only `operator.key`. The daemon binds
  the caller to the profile's principal and team; role or team names sent in
  requests are not trusted.
- Credentials never appear in argv or logs. Child agents do not inherit the
  daemon credential or wake credentials; a managed child gets a scoped context
  that only lets it message its lead (`send_message(to="team-lead")`).
- A lost response returns `outcome_unknown`. The transport never replays a
  request; the caller retries with the same idempotency key.

## Job lifecycle

1. **Accept.** The job, its idempotency record, an unattempted dispatch intent
   and (if the lead has a wake target) a wake row are committed in one
   transaction. Only then does the client get the `job_id`. The active-job
   limit, follow-up parent and model/tier are checked here.
2. **Claim.** The dispatcher picks the oldest unattempted job whose native
   session is free, records a new run (generation + correlation) and marks the
   job `running` — before any process, tab or prompt exists.
3. **Run.** The backend launches or resumes the agent outside any database
   transaction. Startup progress and backend evidence are recorded for
   diagnostics.
4. **Complete.** The backend's authoritative result (headless JSON output or
   the correlated native transcript) is stored with a generation check: a
   stale run cannot overwrite a newer one.
5. **Notify.** After commit, the wake coordinator sends the lead a notice.

These checkpoints establish different facts: acceptance proves the assignment
is durable, an attempt marker proves only that an external effect *may* have
begun, and a backend acknowledgement proves only what that backend protocol
acknowledges. Neither a wake notice nor an acknowledgement proves the model
finished the turn. Only the correlated native completion record makes a job
`completed`; a crash between attempt start and that evidence leaves an
uncertain job for reconciliation.

Follow-ups are new jobs that resume the parent's native session in the same
backend and worktree. Deferred follow-ups may be accepted while the parent
is queued or running. A verified live Linux Codex TUI receives a follow-up of
at most 16 KiB through `codex queue`, including while its turn is busy. The
submission ID is stored on the job, and the frozen thread's native user record
settles the delivery fence. The job still waits for the turn's completion.
An unresolved native attempt blocks all further follow-ups to
that thread and agent name, even after a daemon restart. `stop_job` on that
job releases the fence; the queued message may still run. If `codex queue`
never started, the turn falls back to resume. Dead or unverifiable sessions, larger
prompts and Pi use the usual resume path. No wake hooks are used.
`interrupt=true` cancels the running parent turn and accepts the new job
atomically. Queued follow-ups remain durable across restart; a fenced
parent is reported as `parent_needs_reconciliation`.

## Crash guarantees

| What dies | What happens |
| --- | --- |
| Lead or MCP bridge | Jobs keep running. A new bridge reads results; `resume_session` adopts the old session's jobs and unread notices. |
| Daemon | Durable state survives. On restart, started attempts are quarantined first. A Herdr run with verified saved process and pane identity resumes observation as `running`; a run whose agent is proven gone fails cleanly, and an unverifiable one stays fenced. Queued jobs dispatch normally. A live bridge restarts the daemon on its next call ([ADR 0009](adr/0009-restart-quarantines-live-tuis.md)). |
| Agent or machine | The session may be resumable, but external side effects are not guaranteed exactly once. |

Rules that hold everywhere ([ADR 0008](adr/0008-never-replay-uncertain-prompts.md)):

- An uncertain prompt is never resent. Only a proven pre-delivery failure is
  retried.
- A PID alone never proves ownership. Linux uses a marker plus pidfd for
  headless processes; interactive sessions use saved terminal ownership records
  (for Herdr: server PID and start time, session name, owner label).
- Database fencing stops stale status writes, not an old process writing
  files. A fenced session gets no new machine work until it is stopped or
  its exit is verified.

## Backends

Each backend implements `IJobBackend` (`Business/Features/Agents/Backends`) and
runs with permissions bypassed ([ADR 0006](adr/0006-bypass-permissions.md)).

| Backend | Headless | Interactive | Resume |
| --- | --- | --- | --- |
| Claude Code | `claude -p --output-format stream-json` | TUI in the selected terminal | `--resume <session>` |
| Codex | `codex exec --json` | TUI; trusts the checkout for that invocation only | `exec resume` / `resume` |
| Pi | `pi -p --mode json` | TUI | `--continue` in the original session directory |
| Cursor CLI | `cursor-agent -p --output-format json --force` | Headless only | `--resume <session-id>` |
| Factory Droid | `droid exec --output-format json --skip-permissions-unsafe` | Headless only | `--session-id <session-id>` |
| fake | Test backend for scenarios and demos | — | — |

Notes from backend research:

- Headless protocols (Claude stream-json, Codex app-server/exec, Pi RPC)
  cannot control an already running TUI. Interactive mode therefore uses the
  terminal provider plus the agent's native transcript for evidence.
- Codex's native `codex queue` admits input only when the thread is idle; ATF
  uses it for wake notices, not for steering a busy human turn.
- Model catalogs are discovered from the backend CLIs and cached; an
  unavailable tier target is rejected at admission with an upgrade hint.
- Cursor and Droid's JSON result supplies the native session ID and final text.
  ATF refuses follow-up if no native session was bound. Cursor supports MCP
  through `.cursor/mcp.json` and Droid through its project/user MCP config, but
  neither has a verified per-run MCP config flag. ATF does not write those
  configuration files, so its managed-child MCP routing is unavailable for
  these two backends. Setup and doctor detect their binaries without registering
  MCP with them.

## Terminals

`Business/Features/Agents/Terminals` holds one adapter per provider:
`HerdrInteractiveBackend`/`HerdrTerminal` (Linux), `WtInteractiveBackend`/
`WtTabControl` (Windows Terminal) and `MacTabControl` (Terminal.app, kitty).
Four axes are kept separate: backend, launch mode, terminal provider and the
control/result path. The user-facing rules are in
[launch modes](terminal-modes.md).

## Native wake

`Business/Features/Wake` ([ADR 0005](adr/0005-native-wake.md)):

- The bridge registers the nearest host session as a wake target with an
  increasing generation. Accepted jobs bind to it inside the acceptance
  transaction.
- `WakeCoordinator` scans committed terminal jobs and unread external
  messages, coalesces them and posts a notice through the host's native path:
  Claude host-local channel (`ClaudeChannelWake`), `codex queue`
  (`CodexQueueWake`) or the Pi extension spool (`PiExtensionWake`, read by
  `extensions/pi-wake`).
- For Claude, the daemon offers a notice through `ClaudeWakeMailbox`; the
  recipient's own MCP bridge claims it over authenticated IPC, writes its own
  channel and reports the write outcome. Offers are single-claim and bound to
  channel credentials and host PID. A ten-second timeout or daemon restart
  leaves the durable inbox unread for retry; duplicate notices are possible.
  Linux/macOS select Unix sockets. Windows verifies the connected local pipe's
  owner user SID and server PID before sending, uses cancellable asynchronous
  I/O, and reserves incomplete writes until they drain (at most eight).
- Reading the job through the registered bridge marks it read. Failed posts
  back off; outstanding unread work is re-notified.

## External members

`Business/Features/External/ExternalTeam.cs` lets a manually started Desktop
session join a lead with a one-time ticket, then exchange durable messages
through `send_message`/`read_messages` (lead) and
`external_send`/`external_read` (member). Tickets and tokens are stored as
hashes. Member wake supports Codex queue and Claude host-local channel notices. The same team API serves the
PRFactory connector with an owner key instead of a lead session. Usage:
[external members](usage.md#external-members).

## Web console and PRFactory

- The [web console](web-console.md) is static HTML/CSS/JS served by the daemon
  on loopback, calling the daemon's IPC operations
  ([ADR 0010](adr/0010-minimal-loopback-web-console.md)).
- The [PRFactory connector](prfactory-connector.md) polls PRFactory over HTTPS
  and turns claimed work items into ordinary jobs
  ([ADR 0011](adr/0011-prfactory-outbound-polling.md)).

## Testing

Tests live in `tests/AgentTeamForge.Tests` and focus on critical behavior:
job state transitions, real temporary SQLite for atomicity and idempotency,
cancellation, permissions, and process scenarios (bridge death, daemon crash
at injected points, published AOT binary). Opt-in live tests run real agents or
Herdr only when explicitly enabled. See [CONTRIBUTING](../CONTRIBUTING.md).
