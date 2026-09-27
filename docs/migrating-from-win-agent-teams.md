# Migrating from win-agent-teams to ATF

ATF coordinates durable **jobs**. Optional agent names label a sequence of turns;
save the returned job IDs for follow-up and status. Use
`claude`, `codex`, or `pi` as the ATF backend name (`claude-code` in the
reference becomes `claude`). Choose headless or Herdr at setup; see the
[Linux quickstart](quickstart.md). This table reflects the current Linux MCP
surface; tool names differ where ATF uses jobs instead of process records.

| win-agent-teams MCP tool or behavior | ATF equivalent | Difference |
| --- | --- | --- |
| `spawn_agent` | `submit_job` | Returns a durable `job_id`; `cwd` and `worktree=true` are optional. Optional name; model accepts configured capability tiers. |
| `follow_up_agent` | `follow_up` | New job resumes the parent's native session, backend and worktree; use a new idempotency key. Busy/queued parents defer by default; `defer=false` refuses busy parents. |
| `follow_up_agent(replace_if_idle=...)` | `follow_up(replace_if_idle=...)` | Defaults true: reuse/relaunch a retained interactive session. False records `failed / agent_idle_but_alive` without sending a prompt to a live idle agent. Dead sessions still resume. |
| Interrupt without a prompt | `interrupt_job` | Cancels the turn with reason `interrupted`; retains native session history for later resume. |
| Revive a dead session | `revive_agent` | Same durable resume path as `follow_up`; provide job ID, instruction and idempotency key. |
| `expected_outputs` | `submit_job(expected_outputs=[...])` | Paths persist as metadata and return in `get_job`; neither system verifies file contents. |
| Interrupt a running turn and continue | `follow_up(interrupt=true)` | Cancels the current turn with reason `interrupted`, then submits the new prompt in its session. |
| `send_message` | `follow_up` downstream; `send_message(to="team-lead")` upstream | Managed children receive durable downstream turns and can send durable inbox replies to their parent. |
| `read_messages` | `read_messages` | Cursor-based managed-child and external replies. `get_job` separately returns committed turn results. |
| `kill_agent` | `stop_job` for active work; `stop_agent` for idle retained agents | Keeps durable history and stops only owned agents. |
| `list_agents` | `list_jobs` | Page through job records, not live named processes. |
| `agent_status` | `get_job` / `list_jobs` | Job status; no heartbeat or member binding row. |
| `check_agent` | `get_job`, `get_job_output`, `get_job_activity` | Status, native session, startup diagnostics and cursor-based activity. List timestamps expose inactivity; no identical heartbeat/marker heuristics. |
| `agent_watch_paths` | None needed | Native notice wakes a registered lead; no file watcher. |
| `install_lead_wake` | `register_codex_wake` for Codex; setup for Claude | Codex reads `CODEX_THREAD_ID` from its shell. Claude inbound setup and Pi extension are in the quickstart. |
| `install_member_wake` | None needed | ATF has no external-member inbox watcher. |
| `list_backends` | `list_backends` | Configured backends, executable availability, model choices, effective tiers, cached native models and launch mode. Availability checks PATH, not authentication. |
| `delivery_status` | `get_job`; retry with the same idempotency key if acceptance is uncertain | The `delivery` field retains run ID and submission/acknowledgement timestamps, including after cancellation. Acceptance alone is not delivery. |
| `deliver_pending` | None needed | The daemon dispatches accepted jobs; clients need not drain a send queue. |
| `session_info` | `session_info` | Reports current and recoverable lead sessions and effective tiers. |
| `resume_session` | `resume_session` | Adopts a previous lead session and its jobs. |
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

## Deferred turns and recovery

MCP `follow_up` and `revive_agent` default to `defer=true`. A busy or
queued parent receives a durable child job immediately; the daemon waits
until the parent ends and backend cleanup completes. Each accepted intent
is claimed once. Repeating the same idempotency key recovers that job;
changing its prompt or options returns `idempotency_conflict`.

`get_job.delivery.state` is `pending`, `not_started`, `unconfirmed`,
`acknowledged` (backend acknowledgement), or `result_observed`.
Acknowledgement does not mean the task completed successfully.

Queued turns survive daemon restarts. If the parent's turn was uncertain
at restart, its session stays fenced and `get_job` on the queued child
reports `parent_needs_reconciliation`. It is not silently replayed.
Inspect the parent and reconcile/stop its owned agent before resuming;
`stop_job` can cancel the deferred child, and `queue_ttl_s` bounds its wait.
A terminal parent without a recorded native session makes the child fail
with `parent_session_missing`; an expired native session reports
`session_expired`.

`interrupt_job` sends no follow-up. `revive_agent` uses the same backend,
working directory and native session as its parent, including stopped
headless agents. Uncertain live processes remain fenced. Herdr can retain
or reopen a TUI; there is no headless fallback. Native wake is a notice,
while `get_job` is the committed source of status and result.

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
