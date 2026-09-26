# Linux quickstart

Headless launch is available; Herdr is saved but `atf start` still refuses it.

## Build or publish

From the repository root, resolve the shared pinned SDK in `.tools/dotnet11`:

~~~bash
COMMON="$(git rev-parse --path-format=absolute --git-common-dir)"
DOTNET="$(realpath "$COMMON/../.tools/dotnet11/dotnet")"
export DOTNET_ROOT="$(dirname "$DOTNET")"

"$DOTNET" build AgentTeamForge.slnx -c Release

# Publish a Linux x64 Native AOT apphost for the steps below.
"$DOTNET" publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -o artifacts/quickstart
export PATH="$PWD/artifacts/quickstart:$PATH"
~~~

The pinned SDK is `11.0.100-rc.1.26425.128`. Native AOT publishing needs the
Linux compiler and linker prerequisites.

## Set up and run

Setup requires `--mode`; `--state-dir DIR` is optional. A fresh headless setup
creates a real-agent profile and registers the MCP server in both Claude Code
and Codex. Install those two CLIs before using `--apply`; install and log in to
the backend CLI you plan to submit jobs to:

~~~bash
atf setup --mode headless --apply
atf start
~~~

By default, state is under `$XDG_STATE_HOME/agentteamforge`, or
`~/.local/state/agentteamforge` when `XDG_STATE_HOME` is unset. Use the same
override for all commands if you choose another location:

~~~bash
STATE="$HOME/.local/state/agentteamforge"
atf setup --mode headless --state-dir "$STATE" --apply
atf start --state-dir "$STATE"
~~~

`atf start` prints the daemon PID and is safe to repeat. The daemon runs up to
8 jobs concurrently by default; follow-ups on the same native session stay
serialized. `atf stop` checks the selected state directory and prints a
`kill -TERM PID` command; run the printed command to stop the daemon. It does
not send the signal itself. With the default state location, run `atf stop`;
add `--state-dir "$STATE"` for an override.

## Register Claude Code and Codex manually

`setup --apply` registers both clients. To review or run the commands yourself,
use the same state path and the absolute path to the published apphost:

~~~bash
STATE="$HOME/.local/state/agentteamforge"
ATF="$(command -v atf)"
claude mcp add --scope user agentteamforge -- "$ATF" mcp --state-dir "$STATE"
codex mcp add agentteamforge -- "$ATF" mcp --state-dir "$STATE"
~~~

## Submit and inspect jobs

The MCP bridge exposes `submit_job`, `get_job`, `follow_up`, and
`list_jobs`. Supply a fresh `idempotency_key` for each submit or follow-up;
save the returned `job_id` for later reads. A follow-up resumes a finished
job's native backend session.

~~~text
submit_job(backend="claude", instruction="Explain this repository", idempotency_key="qs-1")
get_job(job_id="<job_id>")
follow_up(job_id="<completed_job_id>", instruction="Summarize the tests", idempotency_key="qs-2")
list_jobs(limit=20)
~~~

The equivalent CLI verbs are `submit`, `get`, `follow-up`, and `list`.
Responses are JSON; copy the `job_id` returned by submit into the later calls:

~~~bash
STATE="$HOME/.local/state/agentteamforge"
atf client submit --state-dir "$STATE" --backend claude --cwd "$PWD" \
  --key qs-1 --instruction "Explain this repository"
atf client get --state-dir "$STATE" --job JOB_ID
atf client follow-up --state-dir "$STATE" --job JOB_ID --key qs-2 \
  --instruction "Summarize the tests"
atf client list --state-dir "$STATE" --limit 20
~~~

CLI submit accepts `--backend claude|codex|pi|fake`, `--instruction`,
`--key`, and optional `--cwd`. Get and follow-up use `--job`; follow-up also
needs `--key` and `--instruction`. List accepts optional `--status`,
`--limit` (1–50, default 20), and `--cursor`.

## Native wake

When detectable, the MCP bridge registers its Claude Code, Codex, or Pi host
session and sends a notice-only wake after completion. Read results with
`get_job` or `atf client get`; CLI calls and generic MCP clients get no wake.
Pi also needs the bundled extension and the same state directory:

~~~bash
ATF_STATE_DIR="$HOME/.local/state/agentteamforge" pi -e "$(realpath extensions/pi-wake)"
~~~

## State, database, and logs

The owner-private state directory contains the profile, operator credential,
launch-mode setting, daemon lock and socket. SQLite job state is
`<state-dir>/jobs.db`. `atf start` appends daemon output to
`<state-dir>/daemon.log`.

**Coming:** Herdr agent launch and the MCP `stop_job` tool. `atf setup
--mode herdr` saves the choice, but `atf start` refuses that mode. Stopping a
job individually is not available; `atf stop` concerns the whole daemon.
