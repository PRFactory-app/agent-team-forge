# Flaky recovery test review

**Verdict: approved with small integration fixes.** Reviewed Claude commit `3f0de4a`
against `d7d24ae` and integrated main's job inspection, Codex backend, and
MVP job routing changes through `a03a35e` in the review worktree.

Moving the dispatch signal from `AcceptJob` to `IpcServer`'s post-write callback
fixes the original signal race. The callback runs in `finally`, so a failed
reply write or client disconnect still wakes dispatch after the committed
acceptance. Main's P1 inspection change adds `job_list` and converts an injected
post-commit failure into `outcome_unknown`; the merge retains both behaviors and
wakes dispatch for that committed unknown outcome. MVP follow-up acceptance uses
the same post-reply signal and claim guard.

The dispatcher's one-second safety poll could still claim between the commit
and reply, as could completion of a different job. The integration therefore
holds a claim gate from the start of `job_submit` handling through the reply
write and callback. The poll remains for transient claim failures, but cannot
cross that gate. A focused test holds the gate across a poll and verifies that
the job remains queued until release. The gate also closes on response-write
failure because the IPC request scope is disposed in all exits.

An additional full run exposed a race in the concurrently merged Codex backend
test: `/proc/<pid>/stat` can disappear between its existence check and read.
The assertion now treats that disappearance as the child having exited.

Gates with `/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`:

- `scripts/verify.sh`: format, warning-clean Release build, 140 passed, 3
  opt-in real-agent tests skipped, Linux x64 Native AOT publish, 26/26
  published scenarios.
- A second `scripts/published-smoke.sh` run against the same published binary:
  26/26 scenarios.
- `scripts/demo.sh` against the published binary: 1/1.

Published binary: `artifacts/linux-x64-20260926T152609Z-Mrovqq/atf` in the
review worktree. Scenario manifests:
`evidence/published-20260926T152619Z-yOMhdS/published-manifest.txt` and
`evidence/published-20260926T152630Z-aXU4kf/published-manifest.txt`.
