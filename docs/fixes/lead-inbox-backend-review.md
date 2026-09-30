# Review — lead inbox backend (slice 4, commit 1952f8c)

Reviewer: Claude (opposite family), read-only.
Scope: `git show 1952f8c` against `docs/fixes/web-session-layout-plan.md` §2c / slice 4 and
`docs/fixes/lead-inbox-backend.md`; frontend contract check against 1bef1fb (`wwwroot/app.js` `sendInline` `@lead` path).

## Verdict: APPROVE — 0 blocker, 0 major, 0 minor

The slice matches the plan closely. I found no real bugs.

## What I checked

### Transaction and dedupe (`ExternalMemberStore.SendToLead`, `InsertLeadMessage`)
- The dedupe key insert (`external_delivery_keys`), the `NextSenderSeq` cursor bump and the message insert all run in the same
  immediate transaction (`BeginTransaction(deferred: false)`). If the insert fails (for example, the team closed between
  `EnsureMcpTeam` and the send), `return false` disposes `tx` without committing. That rolls back both the key and the
  `external_sender_cursors.high_water` increment, so a retry with the same key does not get stuck as a false duplicate.
- A duplicate key commits the no-op and returns `true` before any seq allocation. This is the same pattern as `SendToMember`.
- A retry after the lead closes is rejected by `EnsureMcpTeam` before the duplicate short-circuit. The scenario test covers it.
- Keys share the `(team_id, command_id)` keyspace with `SendToMember`. For MCP-lead teams, `SendFromLead` passes no command id,
  and the frontend uses `crypto.randomUUID()`, so the two cannot collide in practice.

### `SendFromMember` refactor
- It behaves the same as before: `FindMember` → the same `INSERT … SELECT … FROM external_teams WHERE closed_at IS NULL` with the
  team's `wake_key` → commit only when exactly 1 row is inserted. The early `return false` still rolls back the seq allocation.

### Wake propagation
- `InsertLeadMessage` copies `external_teams.wake_key`. `LeadSessionStore` (register wake) updates `external_teams.wake_key`
  and re-keys unread `recipient='lead'` messages. `WakeStore.PendingExternal` picks up unread rows by `wake_key`. The operator
  row goes through the same path as a member's `external_send`, so the lead is woken in the same way. `EnsureMcpTeam` seeds
  `external_teams.wake_key` from `lead_sessions` when it creates the team, so a lead that never created a ticket also works.

### Session and workspace validation
- `JobsEndpoint` skips the generic `sessions.Exists` guard only for `ExternalOperatorSend`. `EnsureMcpTeam` runs the same check
  (`lead_sessions` row matching both session **and** workspace, `closed_at IS NULL`) and returns `invalid_session`. Result:
  - an unknown lead id is rejected;
  - a mismatched workspace is rejected;
  - a closed session is rejected, as is a closed team (the `closed_at IS NULL` filter in the insert).
- The web operator holds the console token, which already has authority over every lead. Posting to any lead is intended.

### Auth on the web route
- `/api/leads/{id}/messages` goes through the shared `HandleAsync` pipeline, the same as every other `/api` route: Host check,
  bearer token (`HasToken`), exact-Origin check for mutations, concurrency gate, and `CancellationToken.None` for the IPC call.
  The route id must be a canonical `D`-format GUID.

### Reserved "operator" name
- `CreateTicketForTeam` rejects `operator`. Both the MCP `CreateTicket` path and the connector/actor path go through it, and the
  test asserts it. The sender string is hardcoded server-side, so the web body cannot choose a sender.

### Input bounds
- Web layer: text 1–65536, workspace 1–4096 and fully qualified, key 1–`MaxKeyChars`. Business layer: text 1–`MaxText` (65536)
  and key ≤128. Both sides count UTF-16 length, so the bounds agree. An oversized key returns `web_bad_request` (tested).

### Frontend contract (1bef1fb `sendInline`)
- URL: `POST /api/leads/{encodeURIComponent(id)}/messages`. Matches.
- Body: `{ text, workspace, idempotency_key }`. Matches `WebLeadMessageBody` under the `SnakeCaseLower` source-gen policy.
- Responses:
  - `ok` → "Delivered to lead inbox", no `deliveryJobId`, as the plan requires;
  - `invalid_session` / `web_bad_request` → terminal "Failed: …";
  - `network` / `web_busy` / `daemon_unavailable` → retry with the same key, which the backend dedupes;
  - the key is generated once per pending attempt and reused on Retry.

## Tests run

`dotnet test tests/AgentTeamForge.Tests --filter "FullyQualifiedName~Web_operator_message|FullyQualifiedName~Features.External."`
with `C:\Projekt\git\agent-team-forge\.tools\dotnet11\dotnet.exe` on Windows: **17 passed, 0 failed, 0 skipped.**
Not run: the Linux-only scenarios and the manual native-wake check from the plan checklist (wake within about 2 s). The
implementer did not run them either. Do them on Linux before merge.

## Non-blocking note (no change required)
- `operator` is reserved case-sensitively (`SafeName` allows `Operator`), and existing members named `operator` are not migrated.
  Both are cosmetic: the lead sees the exact sender string. Only tighten this if impersonation turns out to matter.
