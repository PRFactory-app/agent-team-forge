# MVP Pi backend review

**Verdict: APPROVED with one fix.** Reviewed Claude commit `861b2aa` against `d7d24ae`, the read-only win-agent-teams Pi backend, and Pi 0.87.1's local JSON event documentation. The backend's launch, stdin prompt, session resume, excluded question tools, event mapping, and process-tree termination work in the tested scope.

Pi can emit `message_end` before automatic retry or recovery finishes. The original code completed a job if stdout ended after that message without `agent_settled`. I changed EOF in that case to `pi_not_settled` and added a focused regression test. No other blocking bugs found.

Verification on this review head: root .NET 11 build passed; full tests passed (82 passed, one opt-in skipped); `ATF_REAL_PI=1` new and resumed session test passed against installed Pi; a framework-dependent Release publish succeeded and the published host reached its expected usage output with `DOTNET_ROOT` set to the repo's .NET 11 runtime. `git diff --check` passed. The daemon currently composes the fake backend, so live Pi dispatch through the daemon is outside this slice.
