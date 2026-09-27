# Platform status

What has been tested where. Code that exists for a platform but has not run
there is marked untested. Linux is the reference platform
([AGENTS.md](../AGENTS.md): Linux first; Windows and macOS are finished on
those machines).

| Platform | Status |
| --- | --- |
| **Linux x64** (glibc) | Tested end to end with real Claude Code, Codex and Pi agents, in Herdr and headless modes. v0.0.2 was tagged after a full Linux run. |
| **Windows x64** | Partially verified on a Windows 11 VM through v0.0.3. Interactive Claude and Codex work; the not-logged-in Claude fix in this branch awaits a Windows re-check. |
| **macOS arm64** | Release built; untested. Testers welcome. |
| Linux arm64, musl | Not supported. |

## Feature matrix

| Feature | Linux | Windows | macOS |
| --- | --- | --- | --- |
| Install, setup, doctor, uninstall | Tested (`install.sh`) | `install.ps1` added; installer flow untested on Windows | Untested (`install.sh`) |
| Headless Claude Code / Codex | Tested | Untested | Untested |
| Headless Pi | Tested | Untested (Pi not installed on the test VM) | Untested |
| Interactive agents | Herdr: tested | Windows Terminal (`wt`): Claude and Codex tested; see open items | Terminal.app / kitty: untested |
| Daemon restart keeps live TUIs | Tested (Herdr) | Tested (v0.0.3) | Untested |
| Native wake: Claude lead/member | Host-local relay live-tested with an external member | Named-pipe transport implemented; runtime untested | Unix-socket transport implemented; runtime untested |
| Native wake: Codex lead | Tested | Untested in ATF | Untested |
| Native wake: Pi lead | Tested | Untested | Untested |
| External members (Codex Desktop) | Tested | Untested | Untested |
| Web console | Tested | Tested (v0.0.3) | Untested |
| Login autostart | systemd user unit | Run key + hidden launcher; untested | LaunchAgent; untested |
| Native AOT release build | CI + local | CI (`windows-latest`) | Local build script |

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
assistant API error, but the job remained `running`. This branch maps the
terminal transcript error to `failed`/`agent_login_required`; the Windows
re-check is pending. Other terminal Claude API errors map to
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

- Re-check the not-logged-in Claude case on Windows and run the remaining matrix, including Pi.
- Validate Claude native wake on Windows and macOS, including pipe ACLs, server PID proof, stalled-reader cancellation and macOS host ancestry.
- Validate specific Claude Desktop channel exports; sessions without an exported channel or recognizable host retain manual `external_read`.
- Validate `install.ps1` and upgrade/uninstall on Windows.
- First macOS run by a volunteer, including Terminal.app tab placement.
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
