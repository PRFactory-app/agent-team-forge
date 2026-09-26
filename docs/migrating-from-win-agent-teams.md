# Migrating from win-agent-teams to ATF

ATF coordinates durable **jobs**, not named members in a lead's team. Use
`claude`, `codex`, or `pi` as the ATF backend name (`claude-code` in the
reference becomes `claude`). Choose headless or Herdr at setup; see the
[Linux quickstart](quickstart.md). This table reflects the current Linux MCP
surface. **Landing** means work is in flight, not an available tool.

| win-agent-teams MCP tool or behavior | ATF equivalent | Difference |
| --- | --- | --- |
| `spawn_agent` | `submit_job` | Returns a durable `job_id`; `cwd` and `worktree=true` are optional. No named child or tier selector. |
| `follow_up_agent` | `follow_up` | New job resumes the parent's native session, backend and worktree; use a new idempotency key. |
| `follow_up_agent(replace_if_idle=...)` | None | No process replacement switch; `follow_up(interrupt=true)` can replace a running turn. |
| Interrupt a running turn and continue | `follow_up(interrupt=true)` | Cancels the current turn with reason `interrupted`, then submits the new prompt in its session. |
| `send_message` | `follow_up` for a managed job | No free-form lead/worker inbox. A job's result and native wake carry the reply. |
| `read_messages` | `get_job`, `get_job_output` | Read committed result/status or live log; no inbox cursor. |
| `kill_agent` | `stop_job` | Cancels a queued/running job; keeps its durable history. |
| `list_agents` | `list_jobs` | Page through job records, not live named processes. |
| `agent_status` | `get_job` / `list_jobs` | Job status; no heartbeat or member binding row. |
| `check_agent` | `get_job`, `get_job_output` | Status/result and progress log, not a transcript-state probe. |
| `agent_watch_paths` | None needed | Native notice wakes a registered lead; no file watcher. |
| `install_lead_wake` | `register_codex_wake` for Codex; setup for Claude | Codex reads `CODEX_THREAD_ID` from its shell. Claude inbound setup and Pi extension are in the quickstart. |
| `install_member_wake` | None needed | ATF has no external-member inbox watcher. |
| `list_backends` | None | Choose `claude`, `codex`, or `pi` from configured mode; no discovery MCP tool yet. |
| `delivery_status` | `get_job`; retry with the same idempotency key if acceptance is uncertain | No separate delivery receipt tool. |
| `deliver_pending` | None needed | The daemon dispatches accepted jobs; clients need not drain a send queue. |
| `session_info` | `list_jobs` after reconnect | Daemon state survives the lead; multi-lead session info is **landing**. |
| `resume_session` | None yet | Multi-lead/resume-session control is **landing**; existing jobs remain readable after reconnect. |
| `create_join_ticket` | `create_join_ticket` | Issues a one-time ticket for this lead session, valid for ten minutes. Give the returned `join_prompt` to the manually started member. |
| `join_team` | `join_team` | Exchanges the ticket for a member bearer token. A retry with the same ticket within its ten-minute TTL recovers the same membership and token; after expiry or leave it cannot reopen membership. Bad or expired tickets return `invalid_or_expired_token`. |
| `external_send` | `external_send` | Persists a member message in the lead inbox. The lead uses `read_messages` to read it. |
| `external_read` | `external_read` | Reads only that member's inbox with `member_token` (bare hex or `wam1:<session-id>:<hex>`). Supports `from_agent`, sender-scoped `since_seq`, `full`, `limit`, and `max_chars` like the reference. |
| `external_set_wake` (opt-in) | `external_set_wake` | Registers or clears a Codex queue notice target for the member; pass `codex_thread_id` and `codex_home`. Polling remains available. |
| `leave_team` | `leave_team` | Revokes the token without stopping the member's process. A repeated leave succeeds with `already_left=true`. `close_team` closes the lead session and revokes all its members. |
| `send_message` to external member | `send_message` | Lead sends to a joined member by its ticket name; `to` defaults to `team-lead` as in the reference. Delivery is durable and member wake is best effort. |
| `read_messages` for external replies | `read_messages` | Returns `{messages, cursors, seq, unread_count, has_more}`. `seq` is the 1-based sender sequence per message. An unfiltered read returns a per-sender `cursors` map; `from_agent` returns a scalar `seq`. `since_seq` requires `from_agent`; `limit=0` is a non-consuming watermark. `full=true` ignores `limit`; `max_chars` adds `truncated` and `full_len` per message. |

For a manually started member, configure a separate ATF MCP entry with `ATF_EXTERNAL_ONLY=1`. That entry exposes only `join_team`, `external_send`, `external_read`, `external_set_wake`, and `leave_team`, and does not create a lead session. Its bearer token grants access only to its joined lead's inbox. The regular lead entry exposes `send_message`, `read_messages`, `create_join_ticket`, and `close_team`.

An in-daemon connector can own a team without an MCP lead session. `ExternalTeam.CreateActorTeam(ownerKey)` recovers a stable team ID, then `CreateTicketForTeam`, `SendToMember`, `ReadTeam`, `BindTeamWake`, and `CloseTeam` operate on that ID directly. The PRFactory adapter still needs to map a work item to an owner key, deliver server `SendMessage` commands to `SendToMember`, and upload member replies from `ReadTeam` to the agent stream with a persisted cursor. Those bindings are outside this slice.

Standalone pause/interrupt without a follow-up and `revive` are **landing**.
`stop_job` cancels a job without submitting a new prompt. Native wake is a
notice, while `get_job` is the committed source of status and result.

## Orchestrator loop for skills

1. Start ATF and register its MCP bridge. In a Codex lead, read
   `CODEX_THREAD_ID` with a shell tool and call
   `register_codex_wake(thread_id="<that-id>")` before submitting.
2. Call `submit_job(backend="claude", instruction="...", cwd="/repo",
   worktree=true, idempotency_key="task-1")`. Submit independent jobs with
   distinct keys back to back.
3. Save each `job_id`, **yield the turn**, and wait for native wake. Do not run a
   watcher or poll `get_job` when wake is available.
4. On wake, call `list_jobs` and `get_job(job_id="...")`. Use
   `get_job_output(job_id="...")` for live progress if another job still runs.
5. For a completed job, call `follow_up(job_id="...", instruction="...",
   idempotency_key="task-1-follow")`. To replace a running turn, add
   `interrupt=true`; to cancel without replacement, call `stop_job`. Yield
   again for the follow-up wake, then read its `get_job` result.

The dogfood run followed this loop with parallel Claude and Codex worktree
jobs: two wakes arrived without a watcher, Codex was stopped while running,
and a Claude follow-up resumed the same native session.
