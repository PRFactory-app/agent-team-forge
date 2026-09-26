# Native wake port

Source: owner's read-only `agentic-coder-teams-mcp` at commit
`c2868be1ca5c84bf6dc0b0b222be7d935671e7ef`.

- `native_wake.py`: nearest-host Claude channel resolution, two-line authenticated
  Unix socket post, Codex thread verification and `codex queue`, two-second
  notice coalescing, five-minute renotify, and exponential backoff capped at
  five minutes. The corresponding C# code is under `Business/Features/Wake/`
  and `Host/Features/Wake/`.
- `pi-extensions/win-agent-teams-wake`: copied into `extensions/pi-wake/`.
  Its Pi lifecycle and `sendMessage(... triggerTurn: true, deliverAs: "steer")`
  path are retained. The entry point reads AgentTeamForge's notice-only JSONL
  spool instead of the reference inbox CLI. Set `ATF_STATE_DIR` on the Pi host
  to the same private directory passed to `atf mcp` and load this extension.
- `lead_wake.py`, `member_wake.py`, and `hooks.py` informed the rule that a wake
  only prompts the lead to read; it does not carry result content or acknowledge
  delivery. This slice does not install Claude Stop hooks. The native channel
  is the active Claude wake path.

`atf mcp` registers the nearest host session with the daemon. Registration has
an increasing generation. Accepted jobs bind to that target inside the same
SQLite transaction; terminal rows are scanned only after commit. `job_get`
from the registered bridge marks a terminal job read. A successful native post
only records that a notice was attempted successfully. Re-registration resets
notice progress and catches up unread jobs. The daemon retries failed posts
with bounded backoff. Claude channel discovery and posting are Linux-only,
matching the reference's `/proc` and Unix socket requirement. Codex queue
posting uses the reported `CODEX_THREAD_ID` and `CODEX_HOME` and is not
Linux-specific. Pi uses the extension on the actual Pi host.

The current implementation stores the Claude channel token in the private
SQLite database and expects the state directory's existing owner-only file
permissions. There is no real-session qualification in unit tests; adapter
seams use fakes and the published Linux binary gate covers the integrated
runtime path.

## Live Linux host requirements

Codex 0.157.1 gives `CODEX_THREAD_ID` to shell tools but did not pass it to
the MCP server in a live interactive session. Before submitting jobs, read
`CODEX_THREAD_ID` from a shell tool and call `register_codex_wake(thread_id)`.
The bridge reads `CODEX_HOME` (or `HOME`) from the Codex host process, verifies
the thread there, and binds later submissions to it. This follows the
reference's explicit Codex member
registration. The old environment-based registration still works where Codex
does pass the variable through.

Claude Code 2.1.283 accepted the socket post but held it behind a cross-session
approval prompt when the host ran in bypass mode with default inbound settings.
Launch that lead with a per-session settings file containing
`{"crossSessionInbound":"accept"}`. No global Claude setting is needed. With
that setting the notice arrived in the idle conversation and the lead read
the completed job. Pi 0.87.1 woke through `extensions/pi-wake` after its held
fake job completed and read the result.

Live held-job evidence (2026-09-26, Linux): Codex
`job_01a0de69a72b7dc9b96c349b4a52b09b`, Pi
`job_01a0de605adf7340906c38564a1c97f9`, and Claude
`job_01a0de625bd47299a9ff3c22ffb60b50` each showed the notice in the
idle host conversation, then fetched `fake-result` through MCP. The final
Codex run used the AOT binary after the trusted-home fix. `scripts/verify.sh`
passed with 160 tests (3 opt-in live backend tests skipped) and 26 published
scenarios.
