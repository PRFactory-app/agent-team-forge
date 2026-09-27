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

- Claude wake needs `crossSessionInbound: accept` in Claude settings;
  `atf setup` sets it.
- Codex may not pass `CODEX_THREAD_ID` to MCP; the lead then calls
  `register_codex_wake` once.
- Pi needs the bundled `extensions/pi-wake` extension.
- Claude channel wake is Linux-only (`ClaudeChannelWake.cs`). Windows and
  macOS Claude leads must poll. Codex and Pi wake are cross-platform in code but
  only verified on Linux.
- External Desktop members have Codex queue wake only; a Claude Desktop member
  must poll `external_read`.
