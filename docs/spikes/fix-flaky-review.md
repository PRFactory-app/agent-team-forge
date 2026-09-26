# Flaky recovery test review

**Verdict: approved with small integration fixes.** Reviewed Claude commit `3f0de4a`
against `d7d24ae` and merged current main `898f05b` in the review worktree.

Moving the dispatch signal from `AcceptJob` to `IpcServer`'s post-write callback
fixes the original signal race. The callback runs in `finally`, so a failed
reply write or client disconnect still wakes dispatch after the committed
acceptance. Main's P1 inspection change adds `job_list` and converts an injected
post-commit failure into `outcome_unknown`; the merge retains both behaviors and
wakes dispatch for that committed unknown outcome.

The dispatcher's one-second safety poll could still claim between the commit
and reply, as could completion of a different job. The integration therefore
holds a claim gate from the start of `job_submit` handling through the reply
write and callback. The poll remains for transient claim failures, but cannot
cross that gate. A focused test holds the gate across a poll and verifies that
the job remains queued until release. The gate also closes on response-write
failure because the IPC request scope is disposed in all exits.

Gates with `/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`:

- `scripts/verify.sh`: format, warning-clean Release build, 113/113 tests,
  Linux x64 Native AOT publish, 25/25 published scenarios.
- A second `scripts/published-smoke.sh` run against the same published binary:
  25/25 scenarios.
- `scripts/demo.sh` against the published binary: 1/1.

Published binary: `artifacts/linux-x64-20260926T152021Z-9G77nw/atf` in the
review worktree. Scenario manifests:
`evidence/published-20260926T152032Z-7wvc6R/published-manifest.txt` and
`evidence/published-20260926T152046Z-1nrp8M/published-manifest.txt`.
