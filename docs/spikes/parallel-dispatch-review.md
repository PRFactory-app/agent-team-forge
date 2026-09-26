# Parallel dispatch review

Reviewed `8a1936f` against `37b0e02`. **Approve with one fix** on this branch.

The one-slot fault test exposed a claim after halt: `SemaphoreSlim.WaitAsync` can grant a released slot while the halt token is being cancelled. The second job was committed as running, so restart would quarantine work that had not begun. The dispatcher now checks cancellation and halt while holding the same lock that serializes durable claims with `Halt`. A claim already in progress may finish before halt; no new claim begins after halt wins the lock.

The SQLite claim uses `BEGIN IMMEDIATE`, so competing store instances serialize selection and attempt-start. The added test claims 16 jobs simultaneously and checks one run per job. The session predicate checks both the running job's recorded session and its parent's session, covering siblings and chains; the extended backend test holds a sibling turn while a chained follow-up waits. Shutdown leaves started attempts for recovery. The 2 MiB IPC and backend buffers are bounded by the 16 connection and configured job caps; I found no unbounded accumulation in this change.

With `DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`, `scripts/verify.sh` passed: format, Release build with zero warnings, 86/86 tests, AOT publish, and 23/23 published binary scenarios. The concurrency and fault test groups passed ten consecutive runs after the fix.
