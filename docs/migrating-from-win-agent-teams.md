# Migrating from win-agent-teams to ATF

ATF coordinates durable **jobs**, not named members in a lead's team. Use
`claude`, `codex`, or `pi` as the ATF backend name (`claude-code` in the
reference becomes `claude`). Choose headless or Herdr at setup; see the
[Linux quickstart](quickstart.md). This table reflects the current Linux MCP
surface. **Landing** means work is in flight, not an available tool.

| win-agent-teams MCP tool or behavior | ATF equivalent | Difference |
| --- | --- | --- |
| `spawn_agent` | `submit_job` | Returns a durable `job_id`; `cwd` and `worktree=true` are optional. No named child or tier selector. |
| `follow_up_agent` | `follow_up` | New job resumes a finished parent's native session, backend and worktree; use a new idempotency key. |
| `follow_up_agent(replace_if_idle=...)` | None | No process replacement switch. Interrupt and revive controls are **landing**. |
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
| `create_join_ticket` | None/not needed | No external-member join ticket; multi-lead support is **landing**. |
| `join_team` | None/not needed | A local MCP client connects to the daemon, not a named team. |
| `external_send` | None/not needed | No external-member inbox. |
| `external_read` | None/not needed | No external-member inbox. |
| `external_set_wake` (opt-in) | `register_codex_wake` for a Codex lead | Registers the lead's native notice target, not an external member. |
| `leave_team` | None/not needed | No membership to leave. |

`interrupt` (pause without cancellation) and `revive` are **landing**. Today's
`stop_job` cancels a job; do not treat it as either operation. Native wake is
a notice, while `get_job` is the committed source of status and result.

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
   idempotency_key="task-1-follow")`; for a job to cancel, call `stop_job`.
   Yield again for the follow-up wake, then read its `get_job` result.

The dogfood run followed this loop with parallel Claude and Codex worktree
jobs: two wakes arrived without a watcher, Codex was stopped while running,
and a Claude follow-up resumed the same native session.
