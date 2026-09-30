# Windows Claude native wake validation, 2026-09-30

Symptom: a Claude lead on Windows had `wake_status` `registered:false,
usable:false` before and after submitting jobs; no bridge error was logged.

Cause: Claude Code 2.1.x on Windows exports `CLAUDE_CODE_MESSAGING_SOCKET` in the
session-local pipe namespace (`\\.\pipe\LOCAL\cc-msg-<hex>`).
`ClaudeChannel.PipeName` rejected any backslash after `\\.\pipe\`, so
`HostSessionWake.ForClaudeChannel` returned null and the bridge silently skipped
registration. Host ancestry (`claude.exe` found via Toolhelp), the exported
channel env and the pipe itself were correct: the pipe's server PID was
the host `claude.exe` and its owner was the same user SID.

Fix: accept exactly one `LOCAL\` prefix followed by a flat name. Other nesting,
remote hosts, empty names and `anonymous` are still rejected. The same-user
owner check, server-PID check, host-ancestry binding and notice-only semantics
are unchanged.

Runtime smoke (self-contained `PublishAot=false` publish, isolated state
directory, real Codex backend, headless launch mode): the new `atf mcp`
bridge ran under the Claude host and inherited its channel env.

- `wake_status` returned `registered:true, usable:true`, kind `claude`, on a
  `\\.\pipe\LOCAL\...` address. The installed 0.0.9 bridge in the same
  session still reported `registered:false`.
- A Codex job completed at 08:59:48 UTC. The daemon recorded a successful
  native Claude post (`wake_targets.last_success` 08:59:52 UTC) with the job
  still unread (`wake_jobs.read_at` NULL).
- The Claude session received
  `[AgentTeamForge wake] 1 completed job(s) await reading` without calling
  `get_job` or `read_messages`. It was repeated later while the job stayed
  unread. `get_job` afterwards returned `completed`, `SMOKE-OK`.

The smoke notice arrived during a live Claude turn. Delivery after the lead
has ended its turn goes through the same channel post; it is verified after
deployment. Existing Claude sessions must restart their MCP bridge (reload
the client) because the wake target is resolved once at bridge startup.
