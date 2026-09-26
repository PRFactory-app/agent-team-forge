# Linux quickstart

Linux x64 on tested glibc distributions:

~~~sh
curl -fsSL https://github.com/PRFactory-app/agent-team-forge/releases/latest/download/install.sh | sh
"$HOME/.local/bin/atf" setup
~~~

Install and log in to any backend CLIs you want to use: Claude Code, Codex,
or Pi. First setup asks for an explicit launch mode in a terminal. Herdr gives
visible interactive agent windows when available; headless runs agents in the
background. For automation, use `"$HOME/.local/bin/atf" setup --mode headless`.
Reload installed client sessions after setup, then run
`"$HOME/.local/bin/atf" doctor` to check registration. The daemon starts on
first use. Login autostart is off by default.

The installer leaves shell startup files alone. The absolute setup command works
without `~/.local/bin` in PATH; add `export PATH="$HOME/.local/bin:$PATH"`
to `~/.bashrc` (Bash) or `~/.zshrc` (Zsh) only if you want to type `atf`.
Setup refuses to register a temporary or
worktree binary or state path without `--force`. For testing, use
`atf start --state-dir DIR` or `atf mcp --state-dir DIR`.

See [install](install.md) for upgrades and uninstall. Linux arm64 and musl are
unsupported; macOS arm64 and Windows x64 are tester-only until native validation.

## Platform notes

Windows Terminal (`wt`) mode and its console retry, owned-tab cleanup, Windows
hooks, Pi wake extension, shim launch, and private-file ACL checks are **ported,
untested on Windows**. Windows validation remains on a Windows machine.

macOS arm64: **prepared, untested**. Install the `osx-arm64` release bundle with
`install.sh`, then run `atf setup --mode terminal`.
Terminal.app is the default host and needs no extra install. Its AppleScript
`do script` launch may open a window rather than a tab; the tester should check
the placement. If kitty is
running with a `KITTY_LISTEN_ON=unix:...` remote-control socket and responds
to `kitty @ --to "$KITTY_LISTEN_ON" ls` during setup, setup selects kitty tabs.
The selected host is saved for daemon restarts; if kitty later becomes
unavailable, jobs report a launch failure rather than switching hosts.
`atf setup --mode herdr` is also available after `brew install herdr`;
`atf setup --mode headless` selects background agents. Claude native
wake is unavailable on macOS; poll `get_job`
to check for results. Please report the daemon log and `atf doctor` output from
the volunteer run.

## Set up and run

Setup applies by default; `--apply` remains an alias. `--check` and `atf doctor`
are read-only. It registers whichever Claude, Codex, and Pi clients are installed
and reports skipped or failed clients with a rerun instruction. Claude's
`crossSessionInbound` is set to `accept`, preserving other settings. Pi setup
installs the MCP adapter and bundled wake extension; adapter installation needs
network access.

For visible interactive agents, run `atf setup --mode herdr` with Herdr available.
To enable login startup after setup, run `atf setup --autostart`. To
remove it, run `atf setup --autostart=off`; `atf doctor` reports whether
it is installed. `atf start` remains available when you want to start the
daemon explicitly.

State defaults to `$XDG_STATE_HOME/agentteamforge` or
`~/.local/state/agentteamforge` if unset; pass one `--state-dir DIR` to override:

~~~bash
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge"
atf setup --mode headless --state-dir "$STATE"
~~~

`atf start` prints its PID and is safe to repeat. It runs up to 8 jobs
concurrently by default. `atf stop` safely sends SIGTERM to this state's daemon;
add `--state-dir "$STATE"` when using an override.

## Web console

The daemon serves a small text console on `127.0.0.1:8765` in every launch
mode. Print its link with `atf web`, or use `atf web --open` to open it in a
browser. With a custom state directory, add `--state-dir "$STATE"`.

The link carries the console token in a `#token=...` fragment. The page keeps
it in that tab's session storage and removes it from the address bar. The token
is stored separately from the daemon's IPC credential in the owner-private
`web-console.key` state file. Run `atf web --rotate-token` to revoke old links
immediately and print a new one.

In interactive launch modes, a finished turn can leave its owned agent tab idle
for follow-up. Use **Stop agent** in the console to close that tab; **Stop job**
cancels a queued or running job. The console labels an absent result separately
from an empty result.

Click a lead or agent card to expand its inline composer, result, and live
activity transcript. The card's one-line preview shows the latest activity.
Expand **Raw logs** inside the card when you need the full output stream.
Click it again or press Escape to collapse it. Press Enter to send, or
Shift+Enter for a newline. Check **Interrupt** to interrupt a running turn.
The lead-session card lets you choose which of its member agents to message;
the lead session itself is an MCP binding, not a managed agent. Message delivery
and job activity appear in the cards.

Use **New agent** above the overview to submit a prompt to a configured Claude
Code, Codex, or Pi backend. Enter an existing absolute working directory and,
optionally, a model, effort, and lead session. The daemon validates the directory
and option values before accepting the job. Expand a lead card to generate a
ten-minute join ticket for a Claude Desktop or Codex Desktop external member;
copy the displayed `join_team` instructions into that member's conversation.

To change the port, run `atf setup --mode headless --web-port 8766` (or use your
configured launch mode), then restart the daemon. If the port is occupied, the
daemon logs that the web console is unavailable and continues serving jobs.

## Register Claude Code and Codex manually (troubleshooting)

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

The owner-private state directory holds profile, IPC credential, web console
token, mode, socket, lock,
`jobs.db`, daemon output in `daemon.log`, job output in `logs/<job-id>.log`, and
worktrees under `worktrees/<job-id>`; worktrees remain for manual cleanup.

Read job output with MCP `get_job_output` or `atf client logs JOB_ID --follow --state-dir "$STATE"`.

With the daemon running, `atf prune --dry-run` previews; `atf prune` removes
eligible completed, failed, or cancelled jobs older than 30 days. Automatic pruning runs at startup and daily, deleting matching logs but leaving worktrees.

`stop_job` / `atf client stop JOB_ID` cancels an individual queued or running
job. `atf stop` stops the whole daemon.

## Developer build or publish

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
