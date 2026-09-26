# Session lifetime stability verification

Slices 3–5 of `plans/core-stability.md`, Linux x64, 2026-09-26.

## Behavior

- Daemon shutdown/restart preserves owned Herdr TUIs and their ownership files.
  Restart fences the associated jobs; no TUI adoption or automatic prompt resume.
- Admission and transactional claims fence the entire backend/native session,
  including a sibling that has not recorded its own native session ID.
  Cancellation keeps its fence until stop/interrupt succeeds. Unrelated sessions
  remain dispatchable.
- Explicit Stop agent uses the saved job association, Herdr server PID/start time,
  session name and owner label. Cleanup failure retains the same proof for retry.
  Stop commits fence release before removing durable proof, without changing the
  job's terminal outcome. Concurrent explicit stops are serialized.
- Verified interactive exit releases its job's fence. Terminal headless peers
  may reconcile using the existing owned-process marker check. Missing ownership
  proof never authorizes a PID-only stop or automatic fence release.

## Automated verification

Command from the dedicated worktree:

```sh
DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet scripts/verify.sh
```

Pinned SDK: `11.0.100-rc.1.26425.128`.
Format, warning-as-error build, 440 passing source tests (5 opt-in live tests
skipped), Native AOT publish, and 33/33 published scenarios passed.

Focused tests cover unrecorded uncertain siblings, cancellation/restart fencing,
unrelated claims, verified headless exit, durable stop after restart, failed stop
and eviction retry, concurrent replacement, reused-server refusal, and verified
interactive cleanup. Claude opposite-family review approved the changes after
fixing headless sibling reconciliation and positive-proof exit reconciliation.

## Real Herdr restart check

Herdr `0.8.2`, a published Native AOT binary, a fake Claude executable that signals
startup and sleeps, a private HOME/XDG/Codex/Claude profile, and a private ATF state
directory. No backend credentials or owner configuration were used.
Final binary: `artifacts/linux-x64-20260926T191551Z-ZiHAtT/atf`, SHA-256
`7b506b93cb4b4dbe6e1749f2164014fb9423508bbddbc42666d8a6585dc3ecaa`. The harness
used `atf init`, a private `launch-mode.json` (`herdr`, loopback port 39473),
`atf daemon`, and `atf client submit/get/follow-up`. Stop agent used the authenticated
loopback `POST /api/jobs/{id}/stop-agent` endpoint.

Final-binary passing run: `/tmp/atf-session-life-live-YTm7y9`.
Owned session: `atf-2c758b9a3246`.
Job: `job_01a0df2611797c7ca2f49991cb4d0bfe`.

1. Started the fake agent inside the ATF-created Herdr session.
2. Sent SIGKILL only to that isolated daemon (PID 3079491).
3. Started its replacement (PID 3079752). The Herdr session remained running and
   the ownership file was byte-for-byte unchanged.
4. The job became `needs_reconciliation` / `daemon_restart_uncertain`, with
   `session_fenced=1`; follow-up returned `parent_not_ready`.
5. An unrelated fake-backend job completed.
6. Stop agent returned `agent_stopped`, closed the owned session and removed its
   ownership file. The original job remained `needs_reconciliation`.
7. Stopped the replacement daemon. Both test daemon PIDs and the owned session
   were gone. The owner's default Herdr session and installed daemon were untouched.

An earlier harness attempt killed its shell wrapper instead of the daemon; the
second daemon correctly reported `daemon_already_running`. That attempt was not
counted as restart evidence. The harness was corrected to capture the actual
`env`/daemon process PID. Its failed launch cleaned up its own session.

## Deliberate limits

Ownership records created before the new job association, missing records,
corrupt records and foreign/reused identities are not adopted. Uncertain jobs
without usable ownership proof stay fenced, and explicit stop reports
`backend_unavailable`; restoring verified ownership/reconciliation is manual.
One unreadable ownership file conservatively prevents Herdr explicit cleanup.
This slice adds no general reconciliation command and makes no Windows/macOS
or real model execution claim.
