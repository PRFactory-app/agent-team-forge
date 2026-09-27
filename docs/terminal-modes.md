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
| `herdr` | Linux (macOS after `brew install herdr`, untested) | Each agent's real TUI in a tab of a Herdr session |
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
long Herdr and Windows Terminal launches wait for the agent to be ready and
confirm the prompt. A prompt that times out is never resent; the job stays
under observation and then becomes `needs_reconciliation`
([ADR 0008](adr/0008-never-replay-uncertain-prompts.md)).

## Headless

Headless agents run as background processes with their JSON output modes
(Claude `-p --output-format stream-json`, `codex exec --json`, Pi
`--mode json`) and permissions bypassed ([ADR 0006](adr/0006-bypass-permissions.md)).
Follow-ups resume the same native session. A headless run whose daemon dies is
cleaned up only when ATF can prove it owns the process (marker plus pidfd on
Linux).

## Platform notes

| Platform | Notes |
| --- | --- |
| Linux / Herdr | Tested. ATF starts its own Herdr session by default; Settings can place agents in an existing Herdr session instead. Opt-in live test: `ATF_REAL_HERDR=1` launches only in its own `atf-test-*` session. |
| Windows / `wt` | Partly verified ([platform status](platform-status.md)). State lives under `%USERPROFILE%\.local\state`, not `%LOCALAPPDATA%`, because Windows Terminal tabs cannot read the MSIX-virtualized path. Interactive Codex needs the native `codex.exe`, not a `.cmd` shim. |
| macOS / `terminal` | Untested. Terminal.app's AppleScript `do script` may open a window rather than a tab. The chosen host is saved; if kitty later disappears, jobs fail rather than switch hosts. Claude native wake is unavailable. |

Process ownership is decided by the agent's PID plus its kernel start token,
not by the terminal application. A background daemon may not be able to open
windows when no desktop session is logged in; interactive launch then fails
with an error rather than running headless.
