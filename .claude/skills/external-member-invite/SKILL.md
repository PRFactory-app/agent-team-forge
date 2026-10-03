---
name: external-member-invite
description: Invite a manually started interactive session (e.g. a browser-capable Claude Desktop or Codex Desktop QA) into your ATF lead session as an external member and exchange messages with it. Lead side; the member runs external-member-join. Use only when the teammate needs something a submitted job cannot have (real browser, logged-in app, human at the keyboard); otherwise use agent-orchestration.
---

# Invite an external member (lead side)

Tools are on the `agentteamforge` MCP server. The member runs the
[`external-member-join`](../external-member-join/SKILL.md) skill.

## 1. Mint a ticket

```
create_join_ticket(name="<member-name>", note="<one-line role brief>")
```

The ticket is **one-time and valid for ten minutes**. Hand it over promptly.
The result contains a paste-ready `join_prompt`.

## 2. Hand over the join prompt

Give `join_prompt` to the interactive session. The member should use a separate
ATF MCP entry with `ATF_EXTERNAL_ONLY=1` (exposes only `join_team`,
`external_send`, `external_read`, `external_set_wake`, `leave_team`).

## 3. Exchange messages

- **Send work:** `send_message(to="<member-name>", text="...")`. Delivery is
  durable; member wake is best effort. Never assume it was read: rely on the reply.
- **Read replies:** `read_messages` (delta, per-sender cursors; use
  `from_agent="<member-name>"` and keep reading while `has_more`). Replies wake a
  lead with registered native wake (see agent-orchestration); otherwise read
  manually.
- Confirm a round trip by content: send a distinctive marker and check the echo.
- Verify a member's deliverable independently, as with any job.

## 4. Teardown

The member calls `leave_team(member_token)` when done (revokes its token, does
not stop its process). `close_team` closes your lead session and revokes all
member tokens.

## Worked example

[`desktop-visual-qa`](../desktop-visual-qa/SKILL.md) runs a full browser QA
session on this pattern: a Codex or Claude Desktop tester, a ready gate before
the ticket is minted, GO/findings over the bus, and fixes routed to ATF jobs.
