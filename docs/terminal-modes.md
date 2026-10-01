# Launch modes

At setup you choose how agents are launched. The choice is explicit and ATF
never switches to another mode on its own
([ADR 0004](adr/0004-explicit-launch-mode.md)).

Submit and follow-up instructions accept up to 65,536 UTF-16 characters by
default; IPC accepts 2 MiB frames. In Linux Herdr mode, a real-agent prompt
above 120 KiB UTF-8 is
rejected before acceptance because Herdr passes it as one CLI argument. Long
Windows Terminal prompts are handed to the agent through a private prompt
file when they exceed the command-line limit; this path awaits Windows
validation. Headless backends receive prompts on stdin.

| Mode | Platform | What you get |
| --- | --- | --- |
| `herdr` | Linux; macOS after `brew install herdr` | Each agent's real TUI in a tab of a Herdr session |
| `wt` | Windows | Each agent's real TUI in a Windows Terminal tab |
| `terminal` | macOS | Terminal.app by default; kitty tabs if kitty's remote-control socket answers during setup |
| `headless` | All | Agents run in the background; output in logs and the web console |

```sh
atf setup --mode herdr    # or wt, terminal, headless
```

Without `--mode`, setup asks in the terminal. If the selected provider is
missing or cannot run, setup explains why and recommends another mode; a
job whose terminal fails reports a launch failure. The saved choice applies
to new agents only; running agents keep their binding.

## What interactive means

Interactive agents are the backend's own TUI (Claude Code, Codex, Pi) in a
visible tab or pane. You can read it, type to it and use its UI. A log tail in
a terminal does not count.

At the same time the lead keeps full control through ATF: submit, follow-up in
the same conversation, result, interrupt and stop. The daemon knows which job,
native session and terminal tab belong together.

- **Prompts** go through Herdr's native agent commands
  (`agent start/prompt/get`) or, in Windows Terminal and macOS tabs, through
  the agent's launch command (with a private file for long Windows prompts);
  never through simulated keystrokes.
- **Completion** comes from the agent's native session transcript, matched by
  a correlation marker for this run. Terminal text, silence or an idle pane is
  not treated as a result.
- **Human input** in the tab is allowed. It does not change the machine job's
  result, and ATF does not claim exclusive control of the tab.
- **After a turn,** the tab stays open and idle for follow-ups. **Stop agent**
  (web console) closes it.
- **Stopping** interrupts only that agent; neighboring tabs and your own tabs
  are not touched. Closing a tab by hand is reported as an interruption, never
  as success.

`ATF_INTERACTIVE_STARTUP_TIMEOUT_SECONDS` (default 180, range 30–900) sets how
long Herdr, Windows Terminal and macOS launches wait for the agent to be ready and
confirm the prompt. A prompt that times out is never resent; the job stays
under observation and then becomes `needs_reconciliation`
([ADR 0008](adr/0008-never-replay-uncertain-prompts.md)).

Herdr 0.8.2 exposes `resume_agents_on_restore` only for an entire session.
ATF cannot turn it off for its tabs in a shared user session. Herdr's restore
may launch a bare native resume command without ATF's bypass and MCP options;
inspect restored tabs before using them. ATF will not adopt a pane whose saved
server and shell identity no longer match.

## Headless

Headless agents run as background processes with their JSON output modes
(Claude `-p --output-format stream-json`, `codex exec --json`, Pi
`--mode json`, Cursor `-p --output-format json`, Droid `exec --output-format json`)
and permissions bypassed ([ADR 0006](adr/0006-bypass-permissions.md)).
Follow-ups resume the same native session. A headless run whose daemon dies is
cleaned up only when ATF can prove it owns the process (marker plus pidfd on
Linux).

Cursor CLI and Factory Droid are headless-only in ATF. Their CLIs have a TUI,
but ATF does not yet have a native transcript binding for interactive results.
A submission for either backend in Herdr, Windows Terminal or macOS terminal
mode is rejected with a headless-only error. Their tier settings are available
in Settings: Cursor defaults to the CLI's `auto` model (its effort setting is
not passed to the CLI), while Droid defaults to `claude-opus-5` with a native
reasoning-effort level. Override tier models to match your account.

## Platform notes

| Platform | Notes |
| --- | --- |
| Linux / Herdr | Tested. Agents share the `default` session by default, with one workspace per Git repository and one tab per agent. If that session is stopped, ATF starts its server (it does not create an absent session name) and opens the tab there. Set `ATF_HERDR_SESSION` before starting the daemon or choose `herdr-session:<name>` in Settings to use another listed session. `own-session` was removed for new jobs; existing own-session jobs keep follow-up and recovery. Opt-in live test: `ATF_REAL_HERDR=1` launches only in its own `atf-test-*` session. |
| macOS / Herdr | Claude and Codex launch, follow-up, stop agent and reattach of a running turn after a daemon restart tested against Herdr 0.9.3. A pane's shell must prove it carries the launch's private bootstrap path. macOS hides the environment of Apple's own shells (`/bin/zsh`), so ATF then types one `/bin/sh -c 'printf …'` line into the new pane: it records the shell's PID next to the bootstrap path the shell exported, and only a shell carrying exactly that path can write it. A shell whose environment is readable (for example Homebrew zsh) needs no typed line. After a daemon restart an idle retained Claude or Codex pane is retained again: the pane's live native session is matched through the Darwin process table, Claude's session registry and the files Codex holds open (libproc), as `/proc` does on Linux. A Herdr server ATF starts leads its own process group (Linux uses `setsid`), because launchd kills a stopped job's whole process group (`AbandonProcessGroup` in launchd.plist(5)). |
| Windows / `wt` | Partly verified ([platform status](platform-status.md)). State lives under `%USERPROFILE%\.local\state`, not `%LOCALAPPDATA%`, because Windows Terminal tabs cannot read the MSIX-virtualized path. Interactive Codex needs the native `codex.exe`, not a `.cmd` shim. Idle agents that outlive a daemon restart are adopted again from their wrapper's PID and start time, as on macOS (unverified on Windows). |
| macOS / `terminal` | kitty tested with Claude Code, Codex and Pi ([platform status](platform-status.md)). A follow-up closes the idle tab and opens a new one that resumes the same session. Idle agents that outlive a daemon restart are adopted again, so follow-up, Stop agent and idle close still reach them. A turn still running when the daemon stops keeps its tab, and only a turn that settled records its tab as idle, so idle close and the retention cap never close a working agent; after the restart the job stays `needs_reconciliation` until `stop_job` closes that tab. A native Claude follow-up running in a surviving tab stays fenced the same way while that tab lives. `stop_job` also releases the fence of a tab whose agent is proven to have exited, but not of one whose exit cannot be verified. A tab stopped before its wrapper reported a PID cannot start the agent later: the stop claims that PID record first. A kitty that is not in front can take seconds to answer remote control; ATF waits up to 10 seconds. The chosen host is saved; if kitty later disappears, jobs fail with `backend_not_started` rather than switch hosts. Terminal.app needs the Automation permission for the app that started the daemon to control Terminal (System Settings > Privacy & Security > Automation); without it a job fails at once with `backend_not_started` and that remedy. A Terminal.app launch with the permission granted is untested, and AppleScript `do script` may open a window rather than a tab. Claude native wake is unavailable. |

Process ownership is decided by the agent's PID plus its kernel start token,
not by the terminal application. A background daemon may not be able to open
windows when no desktop session is logged in; interactive launch then fails
with an error rather than running headless.
