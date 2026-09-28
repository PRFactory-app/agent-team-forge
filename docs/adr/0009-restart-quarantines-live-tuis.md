# 0009. Restart quarantines live TUIs instead of killing them

Status: accepted

## Context

When the daemon restarts, interactive agents from the previous daemon may
still be running in Herdr or terminal tabs, possibly with a human typing in
them. An early implementation closed every owned session on restart, which
destroyed live work.

## Decision

- Daemon shutdown or restart leaves owned interactive sessions and their
  ownership records untouched.
- On startup, every started attempt without a committed outcome first becomes
  `needs_reconciliation` with reason `daemon_restart_uncertain`. A Herdr
  attempt with matching saved server, pane, terminal and shell identity returns
  to `running` for observation of its original prompt. An unattached attempt
  fails with `daemon_restart_agent_gone`; its prompt is never replayed.
- Unattempted queued jobs are dispatched normally.
- Explicit **Stop agent** (web console or API) uses the saved ownership proof
  (for Herdr: server PID and start time, session name and owner label) to
  close only that agent, then releases the fence.
  Failed cleanup keeps the proof so the stop can be retried.
- Missing, corrupt or foreign ownership records are never adopted, and a bare
  PID never authorizes a kill.
- Headless runners are separate: a provably owned orphan (marker plus pidfd on
  Linux) may be cleaned up.

## Consequences

- A surviving pane with verified identity resumes observation of its original
  turn. An unattached job fails cleanly without replaying its prompt.
- Verified on Linux with a real Herdr session and a killed daemon (2026-09-26):
  the session survived, the job was fenced, follow-up returned
  `parent_not_ready`, an unrelated job completed, and Stop agent closed the
  session. Windows Terminal received the same behavior later; it awaits
  re-validation on Windows.
