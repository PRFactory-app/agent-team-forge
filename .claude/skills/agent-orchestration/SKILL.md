---
name: agent-orchestration
description: Drive worker agents (Claude Code, Codex, Pi) from a lead session through the AgentTeamForge (ATF) MCP server `agentteamforge`. Covers the submit → yield for wake → read result → follow up → stop loop, restart recovery and output verification. Use whenever a workflow needs another agent to review, implement, research or test in a separate process.
---

# Agent orchestration (ATF MCP)

You are the **lead**. Workers are durable **jobs** owned by the local ATF daemon,
not processes you supervise. A lead crash never loses accepted work; save each
`job_id` and read the committed result with `get_job`. Tools below are on the
`agentteamforge` MCP server (`mcp__agentteamforge__<tool>`).

Backend names are `claude`, `codex` and `pi` (also `cursor`, `droid` in
headless mode). Launch mode (Herdr TUI or headless) is a daemon setup choice.

## Loop

```
list_backends                      # pre-flight: backend configured + on PATH
register_codex_wake(thread_id)     # Codex lead only, once, before submitting
submit_job(...)                    # save job_id; submit independent jobs back to back
── yield the turn; native wake notifies you ──
list_jobs / get_job(job_id)        # committed status + result
read_messages                      # DONE/FAILED lines sent by workers
── verify the deliverable ──
follow_up(job_id, instruction, idempotency_key)   # next turn, same native session
stop_job(job_id) / stop_agent(job_id)             # cancel active turn / close idle agent
```

## 1. Pre-flight and wake

- `list_backends` reports configured backends, executable availability (PATH,
  not auth), model choices, effective tiers and launch mode. If the backend is
  missing, stop and tell the user; never fall back to doing the work yourself.
- **Codex lead:** read `CODEX_THREAD_ID` with a shell tool and call
  `register_codex_wake(thread_id="<id>")`. **Claude lead:** wake is set up by
  ATF setup (see `docs/usage.md#native-wake`). Check with `wake_status`.

## 2. Submit

`submit_job` requires `backend`, `instruction`, `idempotency_key`. Useful optional
fields: `name` (label for the agent/web console card), `cwd` (absolute),
`worktree=true` (private git worktree from cwd's HEAD), `model`, `effort`,
`expected_outputs` (metadata only, not verified), `timeout_s`, `queue_ttl_s`,
`herdr_placement`.

- `model`: Claude takes `haiku|sonnet|opus|fable`; Codex/Pi take capability tiers
  `cheapest|low|medium|high|xhigh|max` (tiers own their effort; `effort` is for
  Claude or raw model slugs). Effective tier table: `session_info`.
- Retrying with the same `idempotency_key` recovers the same job; changing the
  prompt under the same key returns `idempotency_conflict`.
- End every instruction with an absolute output path and a reporting protocol:

  ```
  WRITE YOUR FULL OUTPUT TO <ABSOLUTE-OUTPUT-PATH>

  REPORTING PROTOCOL (mandatory):
  When the file is written, call the agentteamforge MCP tool send_message
  (to defaults to "team-lead") with one line:  DONE: <one-line result>
  If you cannot complete:  FAILED: <reason>
  ```

## 3. Wait

Yield the turn after submitting. Native wake is a notice; `get_job` is the
source of truth. Do not run file watchers or spin on `get_job`. For live
progress of a still-running job use `get_job_output(job_id, offset)` (continue
from `next_offset`) or `get_job_activity(job_id, after_cursor, limit)`.

## 4. Read and verify

- `get_job(job_id)`: status (`queued|running|completed|failed|needs_reconciliation|cancelled`),
  result, native `session_id`, `delivery` state. Delivery `acknowledged` is not
  success.
- `read_messages`: delta-by-default, per-sender cursors; `limit=0` peeks without
  consuming; `full=true` returns everything.
- Read your inbox only with `read_messages`. Message `seq` and `cursors` are per
  sender. Without `from_agent`, `since_seq`/`next_seq` use the daemon-wide
  database position (gaps are normal); with `from_agent`, they use that
  sender's sequence. Never query `external_messages` directly: raw SQL without
  `team_id` shows other leads' traffic.
- "Done" is not proof. Check the output file exists, is non-trivial and starts
  with expected content. For code changes, inspect `git diff` in the job's
  worktree for the intended change.

## 5. Follow up, interrupt, revive

- `follow_up(job_id, instruction, idempotency_key)` starts a new job in the
  parent's native session, backend and worktree. Use a new key per turn and
  repeat the reporting protocol. Busy parents defer durably by default
  (`defer=false` refuses); `interrupt=true` cancels the running turn first;
  `replace_if_idle=false` refuses an idle live interactive agent.
- `interrupt_job(job_id)` cancels a turn without sending a new prompt.
- `revive_agent(job_id, instruction, idempotency_key)` resumes a dead or finished
  agent from its recorded session.
- Lead to managed child can also be `send_message(job_id=..., text=..., idempotency_key=...)`.

## 6. Stop

`stop_job` cancels queued/running work; `stop_agent` closes an owned idle
interactive agent. Both keep durable history. Never kill processes you don't own.

## 7. Restart recovery

After your own restart, empty `list_jobs` does not mean no work: call
`session_info`, then `resume_session(session_id)` for the prior lead session. A
queued child reporting `parent_needs_reconciliation` is fenced: inspect the
parent and stop or reconcile it before continuing.

## Anti-patterns

- Raw `claude`/`codex` CLI from a subagent instead of `submit_job`.
- Polling `get_job` in a loop when wake is registered.
- A fresh `submit_job` for each iteration instead of `follow_up` (loses context).
- Instructions without the reporting protocol or with a relative output path.
- Trusting DONE without checking the deliverable.
