# Linux quickstart

Windows Terminal (`wt`) mode: **ported, untested on Windows**. Windows validation remains on a Windows machine.

## Build or publish

From the repository root, resolve the shared pinned SDK in `.tools/dotnet11`:

~~~bash
DOTNET="$(realpath "$(git rev-parse --path-format=absolute --git-common-dir)/../.tools/dotnet11/dotnet")"
export DOTNET_ROOT="$(dirname "$DOTNET")"

"$DOTNET" build AgentTeamForge.slnx -c Release

# Publish a Linux x64 Native AOT apphost for the steps below.
"$DOTNET" publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -o artifacts/quickstart
export PATH="$PWD/artifacts/quickstart:$PATH"
~~~

The pinned SDK is `11.0.100-rc.1.26425.128`; Native AOT needs Linux compiler/linker prerequisites.

## Set up and run

Setup requires `--mode`; a fresh headless setup creates a real-agent profile.
`--apply` registers both MCP clients and sets `crossSessionInbound` to `accept`
in Claude's `~/.claude/settings.json`, preserving existing settings. Install
both client CLIs first; log in to each backend CLI you plan to use:

~~~bash
atf setup --mode headless --apply
atf start
~~~

For visible interactive agents, run `atf setup --mode herdr --apply` with Herdr installed, then `atf start`.

State defaults to `$XDG_STATE_HOME/agentteamforge` or
`~/.local/state/agentteamforge` if unset; pass one `--state-dir DIR` to override:

~~~bash
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge"
atf setup --mode headless --state-dir "$STATE" --apply
atf start --state-dir "$STATE"
~~~

`atf start` prints its PID and is safe to repeat. It runs up to 8 jobs
concurrently by default. `atf stop` safely sends SIGTERM to this state's daemon;
add `--state-dir "$STATE"` when using an override.

## Register Claude Code and Codex manually

To register manually, use the same state path and absolute published apphost:

~~~bash
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge"
ATF="$(command -v atf)"
claude mcp add --scope user agentteamforge -- "$ATF" mcp --state-dir "$STATE"
codex mcp add agentteamforge -- "$ATF" mcp --state-dir "$STATE"
~~~

## Join a Claude lead from Codex Desktop

The Claude Code lead uses its normal `agentteamforge` MCP entry. Add a second,
restricted MCP entry for the manually started Codex Desktop session, using the
same ATF daemon state directory and an absolute `atf` apphost path:

~~~bash
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge"
ATF="$(realpath "$(command -v atf)")"
codex mcp add --env ATF_EXTERNAL_ONLY=1 agentteamforge-external -- \
  "$ATF" mcp --state-dir "$STATE"
~~~

Restart or reload Codex Desktop so it sees `agentteamforge-external`. That entry
offers only `join_team`, `external_read`, `external_send`, `external_set_wake`,
and `leave_team`; it does not create a lead session. In the Claude lead, call
`create_join_ticket(name="codex-desktop")` and paste its `join_prompt` into the
Codex Desktop conversation. Codex calls `join_team(session_id=..., token=...)`
once and saves the returned `member_token`. The ticket expires after ten minutes
and cannot be reused.

For Codex queue notices, read the current Desktop conversation's
`CODEX_THREAD_ID` and absolute `CODEX_HOME` in that session, then call
`external_set_wake(member_token=..., codex_thread_id=..., codex_home=...)`.
The lead sends work with `send_message(to="codex-desktop", text="...")`.
Codex receives a notice to call `external_read(member_token=...)`, then replies
with `external_send(member_token=..., text="...")`. The lead calls
`read_messages()` for the reply. The queue notice is best effort, so
`external_read` remains the fallback. `follow_up_agent` in win-agent-teams
does not resume an external member; `send_message` is its pull-inbox path.
Call `leave_team(member_token=...)` only when leaving permanently.

## Submit and inspect jobs

MCP tools include `submit_job`, `get_job`, `get_job_output`, `follow_up`, `list_jobs`, `stop_job`, `session_info`, `resume_session`, and `register_codex_wake`.
Use a fresh `idempotency_key` for each submit/follow-up and retain its `job_id`;
follow-up resumes the finished job's native backend session.

~~~text
submit_job(backend="claude", instruction="Explain this repository", idempotency_key="qs-1")
get_job(job_id="<job_id>")
follow_up(job_id="<completed_job_id>", instruction="Summarize the tests", idempotency_key="qs-2")
list_jobs(limit=20)
stop_job(job_id="<running_job_id>")
~~~

Each MCP lead has its own session and sees only its own jobs by default, even when
several leads share a folder. Use `list_jobs(all_workspace=true)` to inspect
other leads' jobs in that folder. After a restart, call `session_info()` to see
the current session, its stable `lead_token`, and recoverable sessions for the
folder. Call `resume_session(session_id="<prior_session_id>")` to adopt the prior
jobs and move unread wake notices to the new bridge. A bridge whose parent and
folder binding survives a restart reconnects to the same session automatically.

CLI: `submit|get|follow-up|list|stop` return JSON; `logs` prints raw output. Reuse the returned `job_id` for later calls:

~~~bash
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge"
atf client submit --state-dir "$STATE" --backend claude --cwd "$PWD" \
  --key qs-1 --instruction "Explain this repository"
atf client get --state-dir "$STATE" --job JOB_ID
atf client follow-up --state-dir "$STATE" --job JOB_ID --key qs-2 \
  --instruction "Summarize the tests"
atf client list --state-dir "$STATE" --limit 20
atf client stop JOB_ID --state-dir "$STATE"
~~~

For the real-agent profile, CLI submit accepts `--backend claude|codex|pi`, `--instruction`,
`--key`, and optional `--cwd`. Get and follow-up use `--job`; follow-up also
needs `--key` and `--instruction`. List accepts optional `--status`,
`--limit` (1–50, default 20), and `--cursor`.

For isolation, set `worktree: true` on MCP `submit_job`, or add `--worktree`
to CLI submit with `--cwd` inside a Git checkout. The job view and list show
the worktree path and branch; follow-ups reuse that checkout.

To bound a job's run time, pass `timeout_s` (1–86400) to MCP `submit_job` or
`follow_up`, or `--timeout S` to CLI submit/follow-up; the job is cancelled
with reason `timeout` that many seconds after it starts running. `queue_ttl_s` /
`--queue-ttl S` cancels (reason `queue_ttl`) a job that has not started in time.

## Native wake

A Codex lead opened in a new repository may show **Trust this folder** once,
even in permission bypass mode; accept it before leaving the lead unattended.

Claude Code, Codex, and Pi MCP hosts may receive a notice-only wake after
completion; the notice names `list_jobs` and `get_job`, which hold the result. If Codex doesn't pass `CODEX_THREAD_ID`
to MCP, read it with a shell tool and call `register_codex_wake(thread_id="<thread-id>")` before submit.
CLI calls and generic sessions without a detected host get no wake. Pi needs
the bundled extension and same state directory:

~~~bash
ATF_STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge" pi -e "$(realpath extensions/pi-wake)"
~~~

## State, database, and logs

The owner-private state directory holds profile, credential, mode, socket, lock,
`jobs.db`, daemon output in `daemon.log`, job output in `logs/<job-id>.log`, and
worktrees under `worktrees/<job-id>`; worktrees remain for manual cleanup.

Read job output with MCP `get_job_output` or `atf client logs JOB_ID --follow --state-dir "$STATE"`.

With the daemon running, `atf prune --dry-run` previews; `atf prune` removes
eligible completed, failed, or cancelled jobs older than 30 days. Automatic pruning runs at startup and daily, deleting matching logs but leaving worktrees.

`stop_job` / `atf client stop JOB_ID` cancels an individual queued or running
job. `atf stop` stops the whole daemon.
