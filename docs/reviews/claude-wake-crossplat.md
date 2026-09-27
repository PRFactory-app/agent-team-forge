# Claude member wake and host-local cross-platform relay

2026-09-27. Branch `feat/claude-wake-crossplat`, based on `064e776`.

## Implementation

`join_team` automatically registers a Claude member when its own MCP bridge
can resolve the nearest Claude host and exported messaging channel.
`external_set_wake(member_token, kind="claude")` explicitly rebinds that channel;
no socket, credential or host PID comes from model tool arguments. The old
Codex registration remains compatible. An empty `codex_thread_id` without a
kind clears either registration. Revocation and unread catch-up use the existing
external membership and wake stores. No hooks are installed.

The durable mailbox is the existing SQLite external-message inbox. The
coordinator scans only committed unread rows and offers a body-free notice to
an ephemeral `ClaudeWakeMailbox`. The recipient's own MCP bridge claims that
offer through authenticated local IPC, writes its own Claude channel, then
reports whether the write completed. The coordinator advances only notice
progress, never read cursors. Offers are single-claim and bound to channel
address, credential and host PID; the routing gate remains held until the
receipt or timeout. A bridge failure, daemon restart or expired offer leaves
the durable message unread and eligible for retry. Late or lost receipts may
produce duplicate notices, never duplicate inbox messages.

This relay matters even on Linux: Claude Code 2.1.283 held the old direct daemon
write as an unidentified peer. A successful socket write was not delivery.
The recipient's own MCP process passed the native channel trust check without
an approval prompt.

Linux and macOS select Unix sockets. macOS host ancestry uses a bounded `/bin/ps`
snapshot; native Claude executables are recognized, while ambiguous/unrecognized
hosts degrade to manual reading. Windows selects only local named pipes, checks
the connected pipe's owner user SID and server PID before sending credentials,
and uses asynchronous .NET pipe I/O with cancellation. An incomplete operation
retains its stream/buffer and reserves its case-insensitive pipe name until it
drains; at most eight operations can be retained. No pipe ACL is modified.
The existing kind-specific `wake_targets.home` field stores the host PID for
Claude registrations, avoiding a schema migration.

The wire format remains newline-delimited auth plus notice-only user JSON.
No transport acceptance receipt exists from Claude itself; successful writes
are notice-posted evidence, not proof that a model read the inbox.

## Validation and boundaries

Read-only reference inspection: `origin/feat/native-downstream-delivery` at
`f6b9b25`, especially `native_wake.py` and `winpipe.py`. Used `git show`/`log`;
no reference checkout or changes.

Focused tests cover member validation/clear/revocation/catch-up, message commit
before notice, preservation of unread state, channel identity, relay claim and
receipt handling, cancellation, platform selection, local pipe names and owner
SID checks. Windows/macOS runtime is **untested on this Linux machine**.
Windows still needs real Claude pipe/ACL and stalled-reader cancellation tests;
macOS needs real exported-channel and native-host ancestry tests.

A Claude session must export `CLAUDE_CODE_MESSAGING_SOCKET` and
`CLAUDE_CODE_MESSAGING_TOKEN` to its own ATF MCP bridge. A Desktop integration
without that channel, or with no recognizable Claude ancestor, cannot be woken
through this mechanism: join reports the unavailable channel and manual
`external_read` fallback; explicit Claude registration fails. This slice does
not claim arbitrary Claude Desktop sessions have those exports.

The isolated Linux live run used Claude Code 2.1.283 (Sonnet), an interactive
PTY, a copied credential in a disposable HOME/config directory, a dedicated
state directory and port 18763. Hooks were explicitly disabled. The member
joined, sent READY, and ended its turn. At 10:44:18 UTC its transcript recorded
the body-free native wake; it called `external_read` and sent
`ACK: WAKE-relay-first` at 10:44:29 UTC. No prompt, polling instruction or input
was sent to the Claude TUI after the initial turn. The daemon only committed
the external message; the MCP relay posted the notice.

A second notice arrived at 10:45:31 UTC; `ACK: WAKE-relay-second` was committed
at 10:45:36 UTC in the same Claude process/session. Test Claude and daemon
processes were stopped, and the entire disposable HOME (including copied
credentials) was deleted afterwards. The first exploratory run encountered an
inherited repository Stop hook; the successful relay run used
`--setting-sources user --settings '{"disableAllHooks":true}'` explicitly.
No hooks were installed or edited.

`main` advanced during the slice and was merged cleanly at `ce655a3` before
final verification. Both disposable daemon state directories were also removed
after their processes exited. Opposite-family review remains with the lead;
no sub-agents were spawned, as requested.
