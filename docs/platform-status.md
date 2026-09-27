# Platform status

What has been tested where. Code that exists for a platform but has not run
there is marked untested. Linux is the reference platform
([AGENTS.md](../AGENTS.md): Linux first; Windows and macOS are finished on
those machines).

| Platform | Status |
| --- | --- |
| **Linux x64** (glibc) | Tested end to end with real Claude Code, Codex and Pi agents, in Herdr and headless modes. v0.0.2 was tagged after a full Linux run. |
| **Windows x64** | Partially verified on a Windows 11 VM (v0.0.1 and v0.0.2). Fixes for the v0.0.2 failures are merged but not yet re-validated on Windows. |
| **macOS arm64** | Release built; untested. Testers welcome. |
| Linux arm64, musl | Not supported. |

## Feature matrix

| Feature | Linux | Windows | macOS |
| --- | --- | --- | --- |
| Install, setup, doctor, uninstall | Tested (`install.sh`) | `install.ps1` added; installer flow untested on Windows | Untested (`install.sh`) |
| Headless Claude Code / Codex | Tested | Untested | Untested |
| Headless Pi | Tested | Untested (Pi not installed on the test VM) | Untested |
| Interactive agents | Herdr: tested | Windows Terminal (`wt`): Claude and Codex tested; see open items | Terminal.app / kitty: untested |
| Daemon restart keeps live TUIs | Tested (Herdr) | Implemented; awaiting re-validation | Untested |
| Native wake: Claude lead | Tested | Not available (Linux-only channel) | Not available |
| Native wake: Codex lead | Tested | Untested in ATF | Untested |
| Native wake: Pi lead | Tested | Untested | Untested |
| External members (Codex Desktop) | Tested | Untested | Untested |
| Web console | Tested | Tested (v0.0.2) | Untested |
| Login autostart | systemd user unit | Run key + hidden launcher; untested | LaunchAgent; untested |
| Native AOT release build | CI + local | CI (`windows-latest`) | Local build script |

## Windows results (v0.0.2, 2026-09-27)

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

- Re-run the Windows matrix on the current release, including Pi.
- Claude native wake on Windows and macOS (currently poll `get_job`).
- Wake for a Claude Desktop external member (currently poll `external_read`).
- Validate `install.ps1` and upgrade/uninstall on Windows.
- First macOS run by a volunteer, including Terminal.app tab placement.
- win-agent-teams parity gaps: see the
  [migration guide](migrating-from-win-agent-teams.md).
