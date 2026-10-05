# Platform status

What has been tested where. Code that exists for a platform but has not run
there is marked untested. Linux is the reference platform
([AGENTS.md](../AGENTS.md): Linux first; Windows and macOS are finished on
those machines).

| Platform | Status |
| --- | --- |
| **Linux x64** (glibc) | Tested end to end with real Claude Code, Codex and Pi agents, in Herdr and headless modes. v0.0.2 was tagged after a full Linux run. |
| **Windows x64** | Supported as of v0.1.0. Tested on Windows 11 with real Claude Code and Codex agents in Windows Terminal tabs, including native wake. Pi, `install.ps1` upgrade/uninstall and headless Claude are not yet verified; see open items. |
| **macOS arm64** | Tested on macOS 26.7 (Apple silicon) with real Claude Code, Codex and Pi agents: headless, kitty, Terminal.app and Herdr, install, upgrade, uninstall and login autostart, from a local Native AOT release bundle. Native wake and external members are untested. |
| macOS x64 (Intel) | No release bundle. A local osx-x64 build ran under Rosetta (private state files, a fake-backend job); real agents untested. |
| Linux arm64, musl | Not supported. |

## Feature matrix

| Feature | Linux | Windows | macOS |
| --- | --- | --- | --- |
| Install, setup, doctor, uninstall | Tested (`install.sh`) | `install.ps1` in use on Windows (v0.0.9 installed, then local builds); upgrade/uninstall not yet verified end to end | Tested (`install.sh`): README one-liner, archive with quarantine, upgrade, rerun, downgrade refusal, uninstall and purge |
| Headless Claude Code / Codex | Tested | Codex tested (v0.1.0 wake smoke); Claude untested | Tested, including follow-up, stop, timeout, worktrees and restart cleanup |
| Headless Pi | Tested | Untested (Pi not installed on the test VM) | Tested (submit and follow-up) |
| Interactive agents | Herdr: tested | Windows Terminal (`wt`): Claude and Codex tested; see open items | kitty: Claude, Codex and Pi tested (submit, follow-up, Stop agent, hand-closed tab, idle close, working directory with a space and a quote; a signed-out Pi and a Codex 401 fail with `agent_login_required`). Terminal.app: Claude and Codex submit and follow-up tested, and the denied-Automation failure. Herdr: Claude and Codex launch, follow-up, Stop agent and restart reattach tested |
| Daemon restart keeps live TUIs | Tested (Herdr) | Tested (v0.0.3) | Tested (kitty): follow-up, Stop agent and idle close reach the surviving agent; a Codex turn still running at the restart outlived a 1-minute idle close, stayed fenced, and `stop_job` closed its tab |
| Native wake: Claude lead/member | Host-local relay live-tested with an external member | Tested (v0.1.0): session-local pipe accepted, automatic wake starts a new idle turn | Unix-socket transport implemented; runtime untested |
| Native wake: Codex lead | Tested | Tested (v0.1.0): explicit registration, queue receipt and automatic lead receipt | Untested |
| Native wake: Pi lead | Tested | Untested | Untested |
| External members (Codex Desktop) | Tested | Untested | Untested |
| Web console | Tested | Tested (v0.0.3) | Tested (follow-up, stop, new agent, settings, token rotation) |
| Login autostart | systemd user unit | Run key + hidden launcher; untested | LaunchAgent: load, unload, `launchctl kickstart` lazy start; tested in an isolated HOME |
| Native AOT release build | CI + local | CI (`windows-latest`) | Local build script, including the published-binary smoke run |

## Linux daemon lifetime

A lazily started Linux daemon runs in its own transient
`agentteamforge-daemon-*.service` (`KillMode=process`, `Restart=no`, `--collect`),
so closing the launching terminal or app does not kill it or its Herdr sessions.
Its environment reaches the service through a 0600 `daemon.env` in the state
directory, deleted once the daemon is ready. Every exit is logged
(`stopped reason=… code=N`, `process exit code=N`); a death with no log line is
recorded by systemd (`journalctl --user -u agentteamforge-daemon-*`) and by an
`ExecStopPost` result in `daemon.exit`, which the next start appends to its
`previous daemon N gone without stop` line. In Herdr mode a missing
`WAYLAND_DISPLAY`/`DISPLAY` is filled in from `systemctl --user show-environment`
when a session starts; without one there, the launch still fails clearly.
Claude Desktop starts its children with `RLIMIT_RTTIME` 0, which makes the kernel
SIGKILL a process that runs on the CPU for a moment without blocking; a daemon
launched from such a caller inherits it (a service gets unlimited). The daemon
raises the soft limit when it can and otherwise logs a warning at start.
Re-run `atf setup --autostart` to add `KillMode=process` to an existing login unit.

## macOS results (2026-10-01)

macOS 26.7 on Apple silicon, Claude Code 2.1, Codex 0.159, Pi 0.99, Herdr 0.9.3
and kitty. The full suite (1478 tests) and the published-binary smoke run pass
on that machine. Defects found and fixed in that pass:

- **Process control.** .NET's `Kill(entireProcessTree: true)` SIGSTOPs its
  direct child, and Darwin's `waitid` reports the stopped child, so the
  runtime's SIGCHLD thread spun and every later `Process` call hung (the test
  suite hung after about 370 tests). Owned process trees are now killed without
  stopping a direct child.
- **Orphan cleanup.** Apple's own binaries (`/bin/sh`, `/bin/zsh`, `sleep`)
  hide their environment from `kern.procargs2`, so run markers were invisible
  and agents' tool shells survived a daemon crash. Cleanup now walks the
  process tree from visible roots, and Codex gets the marker through
  `shell_environment_policy.set` even when the user's config inherits only
  core variables.
- **Symlinked paths.** `/tmp` and `/var` resolve to `/private/...`. Claude's
  transcript folder, worktree ownership, setup registrations, uninstall and
  `atf stop` compared unresolved paths and failed; they now compare resolved
  paths.
- **Terminal and Herdr modes.** AppleScript output leaked into `daemon.log`; a
  denied Automation permission hung instead of failing; the kitty probe raced;
  a restart orphaned live tabs; Herdr's lock file got a garbage mode from a
  variadic `open()` call; idle panes stayed fenced after a restart.
- **Install.** The quarantine attribute survived extraction and Gatekeeper
  blocked `atf`; the LaunchAgent was written but never loaded; uninstall
  restarted the daemon through the client health check.
- **Intel.** The private-file check only knew the arm64 `stat` layout and
  refused every state file on x86_64.

## Windows results (v0.1.5, 2026-10-05)

- **Claude native wake from an elevated host.** When Claude Code runs elevated
  (an administrator without a UAC split token), Windows makes
  BUILTIN\Administrators the owner of its `cc-msg` pipe. The bridge required
  the pipe owner to be the current user, so every Claude lead wake failed with
  `relay_failed` (seen on v0.1.4). Fixed: after the server PID check, an
  Administrators-owned pipe is accepted only when the server process runs as
  the current user. Verified on Windows 11 with a self-contained
  `PublishAot=false` build: a real Codex job completed and the elevated Claude
  lead received the automatic wake notice.

## Windows results (v0.1.0, 2026-09-30)

Validation notes: [Claude native wake](fixes/windows-claude-native-wake-validation.md),
[Codex lead wake](fixes/windows-codex-lead-wake-validation.md),
[installer staging](fixes/installer-fixed-staging.md).

- **Codex lead wake.** `register_codex_wake` was failing on Windows (Linux-only
  host walker). Fixed; explicit registration, queue receipt and automatic
  lead receipt passed, and real Codex and Claude jobs plus a Codex follow-up
  completed with automatic notices.
- **Claude native wake.** Claude Code 2.1.x exports its channel as a
  session-local pipe (`\\.\pipe\LOCAL\...`), which was rejected. Fixed; an idle
  Claude sublead got an automatic completion notice and started a new turn
  without polling.
- **Installer.** `install.ps1` now uses fixed staging paths, recovers a
  leftover `bin.previous`, and takes an install lock. The script was parsed and
  its helpers were run on Windows PowerShell 5.1; a full install of a release
  archive was not run.
- **Tabs and setup.** Tabs are named, Claude inbound is enabled by `atf setup`
  on Windows, and a UTF-8 BOM in Claude settings is accepted.
- **Tests and build.** Focused wake and bridge tests pass on Windows. Some
  other unit tests still fail there because of Unix-only fixture assumptions
  (see `docs/fixes/idle-agent-close.md`). The tested Windows builds were
  self-contained with `PublishAot=false`; AOT is built in CI only.

## Windows results (v0.0.3, 2026-09-27)

On a Windows 11 VM, the v0.0.3 end-to-end pass confirmed that the daemon
survives 800 sequential client calls (A), a bogus agent binary fails promptly
and the next job still runs (B), and an agent exiting before acknowledgement
becomes `needs_reconciliation` promptly (B). Claude's first-run case fails
promptly with `agent_first_run_required` (C). Claude and Codex completed jobs
in new paths with spaces. Web console authentication, a low-integrity client's
access-denied message, stopping a hung interactive job, and live tabs surviving
`atf stop` and daemon restart also passed.

The remaining C case exposed a transcript completion bug: an onboarded Claude
profile without credentials produced a synthetic `authentication_failed`
assistant API error, but the job remained `running`. The terminal transcript
error now maps to `failed`/`agent_login_required`; the Windows re-check is
pending. Other terminal Claude API errors map to
`failed`/`agent_api_error` with Claude's message.

The observed 10-minute run had not reached ATF's default one-hour real-agent
turn limit. The three-minute interactive startup limit applies only before
transcript acknowledgement. An accepted interactive turn without terminal
evidence reaches `needs_reconciliation` at the one-hour limit unless the job
has an explicit shorter timeout.

## Earlier Windows results (v0.0.2, 2026-09-27)

Passed: Codex and Claude in Windows Terminal tabs (new folders, paths with
spaces and mixed case, an existing `untrusted` Codex entry), no trust prompt or
bypass warning for Claude, access-denied message for a low-integrity client,
web console on loopback with bearer check, named pipe across integrity levels,
refusal of legacy `%LOCALAPPDATA%` state, slow interactive start, `stop` on a
fenced job, and `CLAUDE_CODE_GIT_BASH_PATH` pass-through.

Failed, with fixes merged since and awaiting re-validation:

- **A.** A client disconnecting before the pipe accept completed killed the
  daemon; the next lazy start then quarantined all in-flight jobs.
- **B.** An invalid agent binary did not fail the job; it waited out the
  startup bound and became `needs_reconciliation`.
- **C.** Claude's login or first-run screen was not detected in `wt`; the job
  waited and became `needs_reconciliation` instead of reporting
  `agent_login_required` / `agent_first_run_required`.

Minor: orphaned `codex.exe` processes after a daemon crash kept their folders
locked; `atf stop` printed raw `taskkill` output.

## Open items

- Windows: re-check the not-logged-in Claude case, run the headless Claude and Pi parts of the matrix (Pi is not installed on the test machine).
- Windows: validate `install.ps1` upgrade and uninstall end to end, and Windows Terminal launches with `;`, quotes, spaces, non-ASCII characters and `%VAR%` in the title and state path.
- Windows: custom-home client restart and lead resume for Codex wake; pipe ACLs, server PID proof and stalled-reader cancellation for Claude wake.
- Windows: fix the unit tests that fail there on Unix-only assumptions.
- Validate specific Claude Desktop channel exports; sessions without an exported channel or recognizable host retain manual `external_read`.
- macOS: native wake (Claude, Codex and Pi leads), external members, and a release built by CI rather than locally.
- win-agent-teams parity gaps: see the
  [migration guide](migrating-from-win-agent-teams.md).

## Claude external-member wake (2026-09-27)

Claude Code 2.1.283 (Sonnet) in an isolated Linux interactive PTY joined an ATF
team, sent READY, and ended its turn. With hooks explicitly disabled, two
native notices caused `external_read` followed by replies
`ACK: WAKE-relay-first` (10:44:29 UTC) and `ACK: WAKE-relay-second`
(10:45:36 UTC), in the same process/session without further TUI input.
The first direct-daemon experiment was held as an unidentified peer; the
successful path posts through the recipient's own MCP bridge.

The test used a dedicated state directory, port 18763, and copied credentials
in a disposable HOME. Test processes, credential copies and daemon state were
cleaned up. Windows/macOS and arbitrary Claude Desktop sessions were not tested.
The final runtime gate (`DOTNET_PROCESSOR_COUNT=2 scripts/verify.sh`) passed
with 709 tests, 7 opt-in live tests skipped, and 40/40 published Linux AOT
scenarios. Later main changes were documentation and test comments only.
