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
Claude's `crossSessionInbound` to `accept` on all platforms, including Windows,
so wake notices arrive. Reload the
clients afterwards. See [launch modes](terminal-modes.md) for what each mode
means.

- The daemon starts on first use. `atf start` starts it explicitly (safe to
  repeat); `atf stop` stops it. Setup, `atf doctor` and uninstall never start
  it: the `claude mcp get` health check they run answers without contacting
  the daemon. A daemon that is already running keeps its launch settings until
  it is restarted; setup says so.
- The state directory must be 0700 and its private files (`operator.key`,
  `profile.json`, `launch-mode.json`, `jobs.db` and its `-wal`/`-shm`) 0600;
  every command refuses looser modes and prints the `chmod` that fixes them.
  `atf stop` only warns, because it signals nothing but this state's own daemon.
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
   `interrupt=true` to replace a running turn. A verified live Codex TUI
   receives a prompt through `codex queue` even while busy; it runs after the
   current turn. With the default `defer=true`, addressing any job in the
   session chain appends durably to its tail. Queued turns retain acceptance
   order across restarts; `defer=false` returns `parent_not_ready` for a busy
   parent. `get_job` shows the native submission ID when Codex acknowledges the
   queue call. Delivery is attempted once; later turns wait for native completion.
   After 45 seconds without native activity, `get_job` and the console show
   `awaiting_native_receipt` (prompt not observed) or `awaiting_turn_end`
   (completion not observed), with `last_activity_at`; diagnostics also appear
   in the job and daemon logs. A report to the lead does not end the turn.
   A native completion event can end a turn without an assistant text reply.
   A confirmed native abort ends the job as interrupted; a confirmed API error
   ends it as failed with the error reason. Both release the next queued turn.
   `send_message(job_id=..., text=..., idempotency_key=...)` uses this same
   managed follow-up path. `send_message(to=...)` remains inbox messaging.
5. **Stop** a queued or running job with `stop_job(job_id=...)`. Its queued
   descendants are cancelled. Later turns originally addressed to an earlier
   job remain queued in order, even if they followed the stopped job. Use
   `interrupt_job` to interrupt without another prompt, `stop_agent` to
   close a retained agent, and `revive_agent` to resume a dead session
   with a new instruction and idempotency key.

For a native follow-up child, `stop_job` and `interrupt_job` request a turn
interrupt and keep the pane/session. The reply returns promptly; only the
correlated Claude interrupt sentinel or Codex interrupted abort confirms
cancellation. Idle status and successful Escape delivery do not confirm it.
`stop_job` cancels deferred descendants on confirmation; `interrupt_job` keeps
them queued. Natural completion keeps its actual result and reports that the
stop arrived too late. Confirmed API failure retains the session under the
normal idle and absolute retention limits.
If newer input follows a proven target completion or terminal API failure,
ATF records the original result and resolves the stop, while keeping the session
fenced and reserved for the newer turn.

| Native control reason/outcome | Meaning |
| --- | --- |
| `native_interrupt_pending` | Request committed; no clean stop confirmed; agent may still run |
| `native_interrupt_unconfirmed` | Missing target/end evidence or uncertain key dispatch; chain stays fenced |
| `native_interrupt_misdirected` | Another input/turn conflicts with the requested target; chain stays fenced |
| `native_interrupt_unsupported` | Native interruption is unavailable on terminal-tab backends; chain stays fenced |
| `native_released` | Observation explicitly abandoned; agent may still run |

Pending or uncertain requests block new managed turns and wakes to that native
session. `stop_agent` explicitly closes its verified owned session, including
a pending native stop. As before, a logical stop can defer pane cleanup to the
sweep; the existing acknowledgement does not distinguish deferred cleanup.
Explicit release abandons observation without proving
that execution ended. Neither unsupported control nor missing confirmation
causes an automatic close or a second Escape after ambiguous dispatch.

Idle interactive agents close automatically after 5 minutes (checked every 30 seconds),
while native transcripts remain available for `follow_up` / `revive_agent` relaunch.
The web console **Settings → Idle interactive agents** edits both limits while
the daemon runs: maximum retained sessions **0..64** (default **16**) and idle
minutes **0..1440** (default **5**), or disable the timeout. A zero for either
limit retains no idle agents. Lowering a limit closes eligible idle sessions on
the next sweep (within 30 seconds); follow-ups can still resume saved sessions.

The same settings are available with `atf setup --idle-close-minutes MINUTES|off --max-retained-sessions COUNT`. The state directory's `launch-mode.json` stores
`idle_close_minutes` (absent = 5, `-1` = off) and `max_retained_sessions`
(absent = 16). Changes take effect without restarting the daemon. Failed
owned-session cleanup remains retryable.

Other `submit_job` options:

- `model`: for Codex and Pi a capability tier (`cheapest`, `low`, `medium`,
  `high`, `xhigh`, `max`; Pi also `medium-fast`), mapped in
  [model tiers](web-console.md#model-tiers); for Claude `haiku`, `sonnet`,
  `opus` (default) or `fable`, or the aliases `fast` (haiku), `balanced`
  (sonnet) and `powerful` (opus). Raw model slugs pass through. Pi takes any
  model `pi --list-models` lists, bare (`claude-opus-5-5`) or with its
  provider (`anthropic/claude-sonnet-5-5`); the daemon launches the listed
  provider/model.
- `effort`: for Claude or a raw Codex/Pi model; a tier sets its own effort.
  Claude takes `low` to `max`, Pi `off` to `max`. Codex takes the levels its
  model catalog (`codex debug models`) reports for that model, or its full
  list when the catalog is unknown. Any other value is rejected with the
  supported list.
- `timeout_s` (1–86400): cancel with reason `timeout` that long after the job
  starts. `queue_ttl_s`: cancel with reason `queue_ttl` if not started in time.
  Native follow-ups currently have no observation-lifetime `timeout_s`
  enforcement; the Codex queue-call deadline does not bound observation.
  Without final evidence, a native turn can stay non-terminal indefinitely.
- `name`: agent name for the web console card.
- `expected_outputs`: output-path metadata retained in `get_job` (not file verification).
- `safety_mode`: `sandboxed` or `fullAccess`. Omit it for the profile default
  (`safety_mode` in `profile.json`, `fullAccess` when absent). `sandboxed` replaces
  the bypass flags with the sandbox of [ADR 0013](adr/0013-sandboxed-launch-mode.md)
  for Codex and Claude on Linux: shell writes are confined to the job worktree, its
  git data, the package caches below and a private temp directory, `~/.ssh` is
  unreadable, and the agent gets an allow-listed environment (no cloud or token
  variables). Reads elsewhere are not confined. See "Sandbox settings" below for
  the network and cache rules. Where ATF cannot enforce it (Pi, Cursor and Droid,
  other platforms, any interactive launch) the job runs with full access and
  `get_job` reports `requested_safety_mode`, `effective_safety_mode` and
  `safety_downgrade_reason` (`sandbox_unsupported_backend` or `sandbox_unavailable`).
  Follow-ups and revives keep the mode, and a job that a sandboxed agent starts over
  MCP is always requested as `sandboxed`. The web console shows the effective mode.
- `herdr_placement`: `herdr-session:<name>` (Herdr mode; a stopped session is started). Omit it for the
  daemon's default. `own-session` was removed for new jobs; existing ones keep working.

MCP tools for managed jobs: `submit_job`, `get_job`, `get_job_output`,
`get_job_activity`, `follow_up`, `list_jobs`, `list_backends`, `stop_job`,
`stop_agent`, `interrupt_job`, `revive_agent`, `session_info`, `set_session_name`, `resume_session`,
`register_codex_wake`. `list_backends` reports executable availability and
model/tier choices. `get_job.delivery` exposes durable delivery evidence. Coming from win-agent-teams? See the
[migration guide](migrating-from-win-agent-teams.md).

### Sandbox settings

Each machine keeps three sandbox settings in `profile.json`. Edit them in the web
console **Settings → Sandbox**, or in the file. A saved change applies at the next
sandboxed job launch; running agents keep the settings they started with. No
daemon restart is needed. An invalid hand edit stops the daemon at start
(`profile_invalid_sandbox`). While the daemon runs, it keeps the last good
settings; at first these are the settings it validated at start, never the
defaults. The Settings page then shows the read error.

- `sandbox_network` (default `true`). On: Codex has unrestricted network, and Claude
  reaches only the domains in `sandbox_allowed_domains`. `false` turns the network
  off for both.
- `sandbox_allowed_domains`: Claude's domain list. The default lists the common git
  hosts and package registries (GitHub, GitLab, Bitbucket, Azure DevOps, NuGet,
  npm, yarn, PyPI, Go, crates.io, Maven Central). An entry is a host
  (`github.com`) or a subdomain pattern (`*.github.com`) in lower case. Claude
  refuses an allow-all entry. ATF's check is stricter than Claude's: it also
  refuses `*.*`, a wildcard over a top-level domain (`*.com`), a scheme, a path,
  a port, upper case and single-label hosts such as `localhost`.
- `sandbox_cache_paths`: the package caches that both CLIs may write. An entry is
  absolute or starts with `~/`. The Linux default is `~/.nuget/packages`,
  `~/.local/share/NuGet`, `/tmp/.dotnet` (the .NET named-mutex directory that
  restores need), `~/.npm`, `~/.cache/yarn`, `~/.cache/pip`, `~/.cargo/registry`,
  `~/.cargo/git` and `~/go/pkg/mod`. macOS uses `~/Library/Caches/Yarn` and
  `~/Library/Caches/pip`. Windows uses the `~/AppData/Local` equivalents, but the
  sandbox does not apply there yet. `~/.dotnet` is not on the list on purpose:
  write access there includes `~/.dotnet/tools`, where an agent could place an
  executable on `PATH`. Restores work without it.

Hard rule: `~/.ssh`, `~/.aws`, `~/.config/gh` and `~/.netrc` always stay denied.
ATF refuses a cache path that is one of them, is inside one, or contains one (for
example `~/.config` or `~`). ATF also refuses the file-system root, the home
directory and a path that overlaps ATF's state directory. ATF compares both
sides as written and as resolved through symlinks: a cache link into `~/.ssh` and
a cache under the target of a symlinked `~/.ssh`, `~/.config`, home or state
directory are both refused. A path that cannot be resolved is refused. The launch
checks each entry again, so a hand-edited `profile.json` cannot make a secret
writable. Both CLIs get the resolved physical path, not the configured spelling,
so a link that is retargeted after the launch starts does not move the grant. A
cache path inside the job workspace or inside another cache path is not granted
separately, because the agent could replace one of its parents with a link.

A cache path that does not exist on the machine is skipped at launch, and ATF does
not create it: Codex refuses a nonexistent path, and ATF does not add directories
for tools the machine does not use. Create the directory (for example
`mkdir -p ~/.npm`) to enable it. The Settings page lists the skipped paths.

### Several leads and restarts

Each MCP lead has its own session and sees only its own jobs, even when
several leads share a folder. `list_jobs(all_workspace=true)` shows the other
leads' jobs in that folder. After a restart, `session_info()` shows the
current session, its stable `lead_token` and recoverable sessions for the
folder; `resume_session(session_id=...)` adopts a prior session's jobs and
unread wake notices. Each recoverable session shows `owner_native_id`,
`owner_live` and `is_current` (owned by this native session; a Pi lead is
identified by its `pi:<pid>` wake target). A session with a known live bridge
or native owner cannot be resumed by another lead without `force=true`.
A bridge whose parent process and folder survive a restart reconnects to
the same session automatically.

A job request can implicitly adopt a prior session for 30 days from the job's
last activity when the workspace, native backend and configuration home match,
the caller has no jobs, and the old bridge is provably gone. The bridge records
its PID, creation token and process scope. On Linux the scope is the inode
identity from `/proc/self/ns/pid`: an invisible PID proves death only when
that namespace matches the daemon's and `/proc/self/mountinfo` confirms a proc
mount without restricted `hidepid` options. Missing or unreadable evidence,
a different namespace, or restricted proc visibility requires explicit
`resume_session(session_id=...)`. Windows and macOS retain their local process
checks with a platform scope marker; unverified identities also require explicit
resume. No heartbeat or inactivity timeout authorizes adoption. `read_messages`
never adopts another session, and the lead's own jobs remain controllable
regardless of age.

`set_session_name(name="planner")` gives the current lead a display name in the
web console and `session_info`. Names are trimmed, limited to 64 characters and
cannot contain control characters. An empty name clears it.

### Worktrees

`worktree=true` (CLI: `--worktree` with `--cwd` inside a git checkout) creates
`worktrees/<job-id>` on branch `atf/job-<job-id>`. Job views show the path and
branch; follow-ups reuse the checkout. Worktrees are never deleted
automatically.

The agent owns its worktree: it can switch, create, rebase or reset branches
there, also onto a base that does not contain the submit-time commit. Before a
follow-up, revive or resume runs (native or launched), the daemon checks only
that the path is still a linked worktree root of the job's repository: not a
separate clone, the same git common dir that submit recorded for the job, and
an object store that holds the submit-time base commit. Jobs accepted before
the common dir was recorded compare with the submitted cwd while it exists. A refused follow-up fails with one of
these reason codes, and its result text gives the failed check and the next step:

| Reason | Meaning |
|---|---|
| `worktree_missing` | The worktree directory is gone. A follow-up never creates it again. |
| `worktree_unavailable` | The path is not a linked worktree root of the job's repository, git did not answer the check in time on the third try, or git could not create the root job's worktree. |
| `sandbox_worktree_tampered` | A sandboxed worktree failed its tamper checks ([ADR 0013](adr/0013-sandboxed-launch-mode.md)). |

If git does not answer the check in time (a loaded machine), the job goes back
to the queue and the daemon tries again: three tries in total. A stop or
timeout during the check cancels the job and releases its session fence. The chain cannot
continue in a refused worktree: submit a new job and name the branch to continue
from.

## Native wake

When a job finishes, its result is stored first; then the lead's host gets a
short notice naming `list_jobs` and `get_job`. The notice carries no result.
See [ADR 0005](adr/0005-native-wake.md).

- **Claude Code:** its own MCP bridge posts the native notice; no hook is
  installed. Linux is live-tested. Windows named pipes and macOS Unix sockets
  are implemented but runtime-untested. A session without an exported native
  channel uses manual reading.
- **Codex:** if Codex does not pass `CODEX_THREAD_ID` to MCP, read it with a
  shell tool and call `register_codex_wake(thread_id="...")` once before
  submitting. A Codex lead in a new repository may show **Trust this folder**
  once, even in bypass mode; accept it before leaving the lead unattended.
  On Windows/macOS, a custom `CODEX_HOME` must also reach the MCP bridge:
  add `env_vars = ["CODEX_HOME"]` to `[mcp_servers.agentteamforge]` in
  Codex's `config.toml`, or set that server's `env.CODEX_HOME` explicitly.
  The bridge verifies the thread under its inherited home; it does not read
  another process's environment on these platforms.
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
   keeps the returned `member_token`. The ticket expires after two hours;
   a retry with the same ticket before expiry returns the same membership.
3. For Codex queue notices, the member reads `CODEX_THREAD_ID` and absolute
   `CODEX_HOME` in its session and calls
   `external_set_wake(member_token=..., codex_thread_id=..., codex_home=...)`.
   Claude members register their own host channel automatically on join when
   available; `external_set_wake(member_token=..., kind="claude")` re-registers
   it. No channel credentials are model arguments. An empty `codex_thread_id`
   without `kind` clears either backend's registration. A Desktop session
   without exported channel credentials or a recognizable Claude ancestor
   reports the manual `external_read` fallback.
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

A headless agent whose CLI reports that it is signed out (Claude, Codex, Pi,
Droid or Cursor) fails with `agent_login_required`; the job's details and
latest activity name the command that logs it in. Interactive agents report it
too: Claude's login screen or signed-out transcript, Codex's sign-in screen or
a turn its transcript ends on a 401, and Pi's own signed-out message. Pi
records nothing for a prompt it refuses, so ATF reads that message from the
Herdr pane, or (in macOS terminal mode) from Pi's `--list-models` check run
beside the tab; there a Pi that has some credential but none for the selected
model still waits out the startup limit.

An interactive turn that waits on the agent's own background task shows
`waiting: background_task`. In Herdr, if the agent stays idle on that wait for
longer than `ATF_BACKGROUND_WAIT_MINUTES` (default 45), the job fails with
`background_wait_expired` and the next queued turn runs
([terminal modes](terminal-modes.md#what-interactive-means)).

`needs_reconciliation` is never replayed automatically. A follow-up waits
until the session is verified idle or stopped. For a live idle Herdr agent (before automatic idle close),
ATF settles an acknowledged but unobserved (interrupted) turn as `failed` and resumes the next
turn in the same session. For ordinary jobs, `stop_job` cancels the fenced job, terminates only a
verified owned process, and releases the fence once no marked process remains.
For native children, the confirmed-interrupt rules above apply instead.
For Herdr, if no ATF-owned agent remains it just cancels the job; if an owned agent
exists but cannot be verified, it returns `owned_agent_not_verified` and leaves the
fence. A cancelled job's worktree is then eligible for `remove_worktree` /
`atf worktrees prune` under the usual merged/clean checks.
After daemon restart, ATF reattaches a Herdr turn only when its saved server,
pane, terminal and shell identity still match. A run whose Herdr server or
pane shell is proven gone fails with `daemon_restart_agent_gone`; one ATF
cannot verify either way stays `needs_reconciliation` until stopped.

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

Job worktrees are removed only when nothing would be lost: no live job or
interactive agent, no dirty files, no ignored files beyond build output
(`bin`, `obj`, `artifacts`, ...), and every commit already on a remote or tag.
The daily prune also requires the commits to be merged into the default
branch. Use the `remove_worktree` MCP tool (`job_id`, `force`, `dry_run`) or
`atf worktrees prune [--job ID] [--dry-run] [--force]` (force needs `--job` and
overrides only dirty/ignored files, never unpushed commits). An unknown job id
or an already removed worktree fails `not_found`, a job submitted without a
worktree fails `no_worktree`, and a removal git refuses (for example a locked
worktree) is kept as `git_refused` with git's message in its details. The branch
`atf/job-<id>` is deleted with the worktree; the job row is kept, so a later
follow-up fails `worktree_missing`.
Worktrees are left for manual cleanup.
