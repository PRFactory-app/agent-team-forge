# 0005. Native session wake, notice-only after commit

Status: accepted

## Context

A lead that polls for results burns turns and tokens. The reference tool's
[PR #70](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70)
showed that the host session can be woken natively: Claude Code through its
cross-session channel socket, Codex through `codex queue`, Pi through an
extension.

## Decision

- Native wake is the standard mechanism for supported hosts, not an opt-in.
- The result or message is committed to SQLite first. The wake is a short
  notice ("N jobs finished; call `list_jobs`/`get_job`") that carries no result
  content and no credentials.
- A successful wake is not acknowledgment. A job counts as read only when the
  registered lead reads it through the authenticated API.
- Notices are coalesced (two seconds), re-sent for outstanding unread work
  after five minutes, and failed posts back off exponentially up to five
  minutes.
- Wake registrations carry a generation; a newer registration fences older
  ones. Child processes do not inherit wake credentials.
- Manual `get_job`/`read_messages` is the explicit fallback where native wake
  is unavailable.

## Consequences

- Claude notices are posted by the recipient's own MCP bridge. Direct daemon
  writes can be held as an unidentified peer (observed with Claude 2.1.283).
  Setup also sets `crossSessionInbound: accept` on all platforms, including
  Windows; check/doctor verifies it. No wake hooks are installed.
- Codex may not pass `CODEX_THREAD_ID` to MCP; the lead then calls
  `register_codex_wake` once.
- Pi needs the bundled `extensions/pi-wake` extension.
- Claude channel wake selects Unix sockets on Linux/macOS and local named
  pipes on Windows. Windows/macOS runtime remains untested. Codex and Pi wake
  are cross-platform in code. Codex registration and notice queue receipts
  were verified on Windows with the explicit-registration fix; the lead
  also received the automatic notice after its active turn ended. Pi is verified on Linux.
  Off Linux, the Codex home comes from the bridge's inherited environment,
  so custom `CODEX_HOME` values must be forwarded to the MCP server.
- External Claude members register the host-local channel at join. Sessions
  without exported channel credentials or a recognizable Claude ancestor,
  including Desktop configurations without that mechanism, must use
  `external_read`. Registration never invents a working channel.
