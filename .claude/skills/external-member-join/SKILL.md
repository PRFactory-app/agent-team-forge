---
name: external-member-join
description: Join an ATF lead's team from a manually started interactive session (Claude Desktop, Codex Desktop) as an external member using join_team, external_read, external_send, external_set_wake and leave_team. Use when you were given an ATF join ticket or join prompt.
---

# Join a team as an external member (member side)

You were started manually and given a join prompt by an ATF lead. Mirror of
[`external-member-invite`](../external-member-invite/SKILL.md).

## Rules

- Use only `join_team`, `external_read`, `external_send`, `external_set_wake`
  and `leave_team` on the `agentteamforge` server (ideally an entry with
  `ATF_EXTERNAL_ONLY=1`). Do not use lead tools such as `send_message` or
  `read_messages`.
- **Save the `member_token`** from `join_team`. It is your credential for every
  call and grants access only to your joined lead's inbox.

## Steps

1. **Join** with the literal values from the prompt:
   `join_team(session_id="<session-id>", token="<ticket-token>")`.
   The ticket is one-time and expires after ten minutes; retrying the same
   ticket within that window returns the same membership and token. Expired or
   bad tickets return `invalid_or_expired_token`: ask the lead for a new one.
2. **Announce readiness:** `external_send(member_token=..., text="READY")`.
3. **Register wake (optional, best effort):**
   - Claude host: registered automatically on join when available, or call
     `external_set_wake(member_token=..., kind="claude")`.
   - Codex: read `$CODEX_THREAD_ID` and `${CODEX_HOME:-$HOME/.codex}` in a shell,
     then `external_set_wake(member_token=..., kind="codex", codex_thread_id=..., codex_home=...)`.
     Pass an empty `codex_thread_id` without `kind` to clear.
   A wake is only a doorbell; always read the inbox.
4. **Drain and reply:** `external_read(member_token=...)` (optional `from_agent`,
   `since_seq`, `full`, `limit`, `max_chars`); keep reading while more is
   pending, then `external_send(member_token=..., text=...)` with your result.
   Without wake, call `external_read` again before ending a long wait.
5. **Leave** when permanently done: `leave_team(member_token=...)`. Repeating it
   returns `already_left=true`. After leaving, the ticket cannot reopen membership.

## After an MCP restart

Your `member_token` still works; resume with `external_read`/`external_send`.
