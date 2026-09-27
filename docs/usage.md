# Usage

How to hand work to agents, follow up, stop them, and where ATF keeps its
state. Install first: [install](install.md). Platform support:
[platform status](platform-status.md).

## Set up

```sh
"$HOME/.local/bin/atf" setup               # asks for the launch mode
"$HOME/.local/bin/atf" setup --mode herdr  # or: headless, wt (Windows), terminal (macOS)
"$HOME/.local/bin/atf" doctor              # read-only check
```

Install and log in to the backend CLIs you want first: Claude Code, Codex or
Pi. Setup registers the `agentteamforge` MCP server in each installed client,
installs Pi's MCP adapter and wake extension (needs network), and sets
Claude's `crossSessionInbound` to `accept` so wake notices arrive. Reload the
clients afterwards. See [launch modes](terminal-modes.md) for what each mode
means.

- The daemon starts on first use. `atf start` starts it explicitly (safe to
  repeat); `atf stop` stops it.
- Login autostart is off by default: `atf setup --autostart` /
  `--autostart=off`.
- Setup refuses to register a temporary or worktree binary or state directory
  without `--force`. For testing, use `atf start --state-dir DIR` or
  `atf mcp --state-dir DIR`.
- The daemon runs up to 16 jobs at a time by default.

To register a client by hand (troubleshooting):

```sh
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge"
ATF="$(command -v atf)"
claude mcp add --scope user agentteamforge -- "$ATF" mcp --state-dir "$STATE"
codex mcp add agentteamforge -- "$ATF" mcp --state-dir "$STATE"
```

## The lead's loop

In a lead session, ask in plain words ("use agentteamforge to have Codex review
this branch") or call the MCP tools directly:

1. **Submit.** `submit_job(backend="claude", instruction="...", cwd="/repo",
   worktree=true, idempotency_key="task-1")`. Use a fresh idempotency key per
   submit; retrying with the same key returns the same job. Submit
   independent jobs back to back.
2. **Yield.** Save each `job_id` and end the turn. Do not run a watcher or poll
   while native wake is available.
3. **On wake,** call `list_jobs` and `get_job(job_id=...)` for the result.
   `get_job_output(job_id=...)` shows live output of a job still running.
4. **Follow up** on a finished job with
   `follow_up(job_id=..., instruction=..., idempotency_key="task-1-b")`. It
   resumes the same native agent session, backend and worktree. Add
   `interrupt=true` to replace a running turn. Without it, a follow-up to a
   busy job returns `parent_not_ready`.
5. **Stop** a queued or running job with `stop_job(job_id=...)`.

Other `submit_job` options:

- `model`: for Codex and Pi a capability tier (`cheapest`, `low`, `medium`,
  `high`, `xhigh`, `max`; Pi also `medium-fast`), mapped in
  [model tiers](web-console.md#model-tiers); for Claude `haiku`, `sonnet`,
  `opus` (default) or `fable`. Raw model slugs pass through.
- `effort`: for Claude or a raw Codex/Pi model; a tier sets its own effort.
- `timeout_s` (1–86400): cancel with reason `timeout` that long after the job
  starts. `queue_ttl_s`: cancel with reason `queue_ttl` if not started in time.
- `name`: agent name for the web console card.
- `herdr_placement`: `own-session` or `herdr-session:<name>` (Herdr mode).

MCP tools for managed jobs: `submit_job`, `get_job`, `get_job_output`,
`follow_up`, `list_jobs`, `stop_job`, `session_info`, `resume_session`,
`register_codex_wake`. Coming from win-agent-teams? See the
[migration guide](migrating-from-win-agent-teams.md).

### Several leads and restarts

Each MCP lead has its own session and sees only its own jobs, even when
several leads share a folder. `list_jobs(all_workspace=true)` shows the other
leads' jobs in that folder. After a restart, `session_info()` shows the
current session, its stable `lead_token` and recoverable sessions for the
folder; `resume_session(session_id=...)` adopts a prior session's jobs and
unread wake notices. A bridge whose parent process and folder survive a
restart reconnects to the same session automatically.

### Worktrees

`worktree=true` (CLI: `--worktree` with `--cwd` inside a git checkout) creates
`worktrees/<job-id>` on branch `atf/job-<job-id>`. Job views show the path and
branch; follow-ups reuse the checkout. Worktrees are never deleted
automatically.

## Native wake

When a job finishes, its result is stored first; then the lead's host gets a
short notice naming `list_jobs` and `get_job`. The notice carries no result.
See [ADR 0005](adr/0005-native-wake.md).

- **Claude Code** (Linux): works after setup. Not available on Windows or
  macOS; poll `get_job` there.
- **Codex:** if Codex does not pass `CODEX_THREAD_ID` to MCP, read it with a
  shell tool and call `register_codex_wake(thread_id="...")` once before
  submitting. A Codex lead in a new repository may show **Trust this folder**
  once, even in bypass mode; accept it before leaving the lead unattended.
- **Pi:** load the bundled extension with the same state directory:

  ```sh
  ATF_STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge" pi -e "$(realpath extensions/pi-wake)"
  ```

CLI calls and sessions without a detected host get no wake.

## External members

A manually started Claude Desktop or Codex Desktop session can join a lead as
an external member and exchange messages with it.

1. Give the member its own restricted MCP entry (same state directory,
   absolute `atf` path):

   ```sh
   STATE="${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge"
   ATF="$(realpath "$(command -v atf)")"
   codex mcp add --env ATF_EXTERNAL_ONLY=1 agentteamforge-external -- \
     "$ATF" mcp --state-dir "$STATE"
   ```

   That entry offers only `join_team`, `external_read`, `external_send`,
   `external_set_wake` and `leave_team`, and creates no lead session.
2. In the lead, call `create_join_ticket(name="codex-desktop")` (or use the
   web console) and paste the returned `join_prompt` into the member's
   conversation. The member calls `join_team(session_id=..., token=...)` and
   keeps the returned `member_token`. The ticket expires after ten minutes;
   a retry with the same ticket before expiry returns the same membership.
3. For Codex queue notices, the member reads `CODEX_THREAD_ID` and absolute
   `CODEX_HOME` in its session and calls
   `external_set_wake(member_token=..., codex_thread_id=..., codex_home=...)`.
   Claude Desktop members have no wake and poll `external_read`.
4. The lead sends with `send_message(to="codex-desktop", text=...)` and reads
   replies with `read_messages()`. The member reads with
   `external_read(member_token=...)` and replies with
   `external_send(member_token=..., text=...)`.
5. `leave_team(member_token=...)` leaves permanently; `close_team` in the lead
   revokes all members. Neither stops the member's process.

## CLI

`atf client submit|get|follow-up|list|stop` return JSON; `logs` prints raw
output.

```sh
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge"
atf client submit --state-dir "$STATE" --backend claude --cwd "$PWD" \
  --key qs-1 --instruction "Explain this repository"
atf client get --state-dir "$STATE" --job JOB_ID
atf client follow-up --state-dir "$STATE" --job JOB_ID --key qs-2 \
  --instruction "Summarize the tests"
atf client list --state-dir "$STATE" --limit 20     # also --status, --cursor
atf client logs JOB_ID --follow --state-dir "$STATE"
atf client stop JOB_ID --state-dir "$STATE"
```

Submit and follow-up also accept `--timeout S` and `--queue-ttl S`. Submit
accepts `--worktree`; follow-ups reuse the parent's worktree.

## Job states

| Status | Meaning |
| --- | --- |
| `queued` | Accepted and stored; not started |
| `running` | An attempt has started |
| `completed` / `failed` | Finished with the backend's authoritative result |
| `cancelled` | Stopped, timed out, expired in the queue or interrupted (see `reason_code`) |
| `needs_reconciliation` | ATF cannot prove whether the prompt ran or how it ended |

`needs_reconciliation` is never retried automatically, and follow-ups into
that agent session are blocked ([ADR 0008](adr/0008-never-replay-uncertain-prompts.md)).
Read the agent's session to decide. `stop_job` then stops the agent only if
ATF can verify it owns the process, records `cancelled` with reason `stopped`
and releases the block; otherwise it returns `owned_agent_not_verified`
without signaling any PID. After a daemon restart, in-flight jobs become
`needs_reconciliation` with reason `daemon_restart_uncertain` while their
interactive agents keep running
([ADR 0009](adr/0009-restart-quarantines-live-tuis.md)).

## State, logs and pruning

State lives in `${XDG_STATE_HOME:-~/.local/state}/agentteamforge`
(`%USERPROFILE%\.local\state\agentteamforge` on Windows); pass
`--state-dir DIR` to use another. It is owner-private and holds the profile,
credentials, launch mode, `jobs.db`, `daemon.log`, per-job logs in
`logs/<job-id>.log`, worktrees and backups. Full list:
[data model](data-model.md#files-next-to-the-database).

Finished jobs (`completed`, `failed`, `cancelled`) older than 30 days are
pruned at startup and daily, with their logs; results the lead has not read
are kept. `atf prune --dry-run` previews and `atf prune` runs it now.
Worktrees are left for manual cleanup.
