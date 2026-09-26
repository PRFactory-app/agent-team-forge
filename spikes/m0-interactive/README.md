# M0 interactive spike — Claude and Codex TUIs in Herdr (Linux)

C#/.NET feasibility spike for the first M0 risk: real interactive Claude Code and
Codex TUIs in Herdr, with machine follow-up, authoritative completion, targeted
cancel, and survival of the controlling process's death. Results, versions, and
unrun gates are in [REPORT.md](REPORT.md).

This is spike code, not product code. It has no daemon, SQLite, MCP bridge,
setup flow, or service installation. It is Linux + Herdr only and makes no
Windows or macOS claims.

## Layout

| Path | Contents |
| --- | --- |
| `src/AtfSpike/Herdr/` | Launch/terminal provider: `herdr` CLI driven with argument lists, environment scrubbing. |
| `src/AtfSpike/Claude/` | Claude control/result transport: inbox-socket wire, hook evidence records and folding. |
| `src/AtfSpike/Codex/` | Codex control/result transport: JSON-RPC peer, WebSocket over a Unix socket, turn evidence. |
| `src/AtfSpike/Delivery/` | Backend-neutral job tag and reconciliation rules (accepted → dispatching → acknowledged → terminal / needs reconciliation). |
| `src/AtfSpike/Os/` | Linux process identity (PID + start time). |
| `src/AtfSpike/State/` | Spike-only JSON state file with atomic replace (not the product's SQLite design). |
| `src/AtfSpike/*Commands.cs`, `Program.cs` | CLI commands and the Claude hook helper entry point. |
| `tests/AtfSpike.Tests/` | xUnit tests for the critical logic and transports. |
| `scripts/` | Minimal shell glue: launcher, gates, bridge-kill experiment. |
| `evidence/` | Local raw experiment captures; not cleared for public publication. |
| `.run/` | Runtime state, hook logs and agent workspaces (git-ignored). `.run/probe/` holds the previous probe's data. |

## Publication boundary

Documentation sanitization is not a secret audit of raw experiment artifacts.
Do not publish `evidence/`, `.run/`, terminal captures, or backend transcripts
without a separate review and sanitized export. They may contain machine paths,
session identifiers, prompts, process details, and inherited environment data.
Keep raw evidence locally for verification; publish only approved summaries and
fixtures. The spike's `.gitignore` excludes `.run/` and build outputs, **not**
`evidence/`, so it does not by itself prevent accidental publication.

## Requirements

- Linux with Herdr 0.8.2, Claude Code 2.1.283, Codex CLI 0.157.1, .NET SDK 10.0.401
  (versions tested; see the report).
- Logged-in Claude and Codex CLIs. Live commands spend real (small) model turns.

## Gates (offline, no model cost)

```bash
scripts/gates.sh   # dotnet format --verify-no-changes; Release build with -warnaserror; tests
```

## Live commands

Build once with `dotnet build AtfSpike.slnx -c Release`, then use
`scripts/atf-spike.sh <command>`. Every command prints JSON lines.

```bash
scripts/atf-spike.sh session-up                 # new Herdr session with a random, never-existing name
herdr session attach <name printed above>       # (human) view and type into the agent tabs

scripts/atf-spike.sh launch-codex cx1           # Codex app-server pane + `codex --remote` TUI pane
scripts/atf-spike.sh launch-claude cl1          # Claude TUI (Haiku) in its own tab; see limitations

scripts/atf-spike.sh send cx1 job-1 'Reply with exactly: OK. Do not run any tools.' --wait
scripts/atf-spike.sh wait cx1 job-1             # reconcile/await from a fresh process
scripts/atf-spike.sh cancel cx1 job-2           # Codex: turn/interrupt on the bound turn
scripts/atf-spike.sh reconcile-idle cx1         # explicit: lift a foreign-turn pause after verifying idle
scripts/atf-spike.sh status                     # ownership, bindings, pause/fence, observed turns, jobs

scripts/bridge-kill.sh cx1 job-3 '<long instruction>' evidence/<file>.jsonl
ATF_SPIKE_CRASH_AT=after-intent scripts/atf-spike.sh send ...   # or after-post: SIGKILL barrier

scripts/atf-spike.sh teardown                   # stops and deletes only the proven-owned session
```

Safety rules (details and evidence in [FIXES.md](FIXES.md)):

- `send` first claims the job atomically (only an unattempted accepted job, one unresolved
  attempt per conversation). Every check runs after the claim; a refused check releases it.
- Codex binds each turn by native `clientUserMessageId` (`atf1.<job>.<attempt>`); message
  text is never correlation evidence. Before any send, cancel or reconcile the full binding
  (owned Herdr server, app-server process and socket, the bound thread, TUI process and
  pane) is verified, and a failure refuses.
- Any turn not bound to one of our attempts (for example human input) persists a pause.
  There is no bypass flag; `reconcile-idle` lifts it only after verifying an idle, readable
  history. Unreadable history is never treated as idle.
- Claude: hooks cannot prove who typed a prompt; completion is a strict text-and-time
  match labelled "human origin not excluded", so Claude is not a supported safe adapter.
  `cancel` for Claude reports unsupported (exit 8); `--send-esc-unverified` only types Esc.
- A job id that was already claimed is never sent again; the same id with a different
  instruction is a conflict.
- Herdr and its panes receive an environment allowlist; add names explicitly with
  `ATF_SPIKE_ENV_ALLOW=NAME1,NAME2` (agent-session variables are never forwarded).

State lives in `.run/spike2/` (schema 2, directory 0700, files 0600, fsynced). Earlier
schema-1 state and its session are left untouched and not adopted.

Diagnostics: `codex-rpc <agent> <read-only-method> '<json>'` (`$THREAD` is substituted;
only `thread/read`, `thread/turns/list`, `thread/loaded/list`, `model/list`).
`ATF_SPIKE_TRACE=1` prints raw terminal-looking turn listings.
