# Windows Codex lead wake validation, 2026-09-30

Claude Code Opus, medium effort, produced the accompanying plan. Codex
implemented the platform host resolver, explicit lead validation, inherited
home resolution and Windows cross-root rollout rejection. Source-scanning
tests were replaced with behavioral tests and a real MCP process smoke.

Machine: Windows x64. Codex CLI: 0.159.2. Starting ATF release: 0.0.9.
Before the fix, `register_codex_wake` returned `invalid_request` and
`wake_status` reported `registered:false, usable:false`.

The development apphost and a self-contained managed Windows publish both
passed an isolated-state MCP smoke using the actual lead thread:

- Explicit registration returned `registered`, generation 1.
- `wake_status` returned `registered:true, usable:true`.
- An unknown GUID and an uppercase thread ID were rejected.
- A fake-backend job completed and the daemon recorded a successful native
  Codex queue receipt (`wake_targets.last_success`, 07:25:48 UTC for the
  development apphost). This exercises completion-to-lead queue delivery,
  rather than follow-up delivery into a worker.

After the active implementation turn ended, the lead received the automatic
`[AgentTeamForge wake] 1 completed job(s) await reading` notice. This confirms
actual lead receipt for the isolated fake-job smoke, beyond the queue receipt.
The notice has no job ID: the MCP job list in the regular state directory
contains the separate planning/review jobs, while the isolated smoke job's
result was verified through its own bridge before shutdown.
Fresh real Claude/Codex jobs after client reload, custom-home client restart,
lead resume and macOS runtime checks also remain pending.

All 59 focused wake and MCP bridge tests pass on Windows, with
`TreatWarningsAsErrors=true` and no skipped tests.

The local SDK is the pinned .NET 11 RC. Native AOT publishing could not be
verified because the machine has no C++ compiler toolchain. The tested
self-contained publish uses `PublishAot=false`; release CI must verify AOT.

Existing wake tests needed portable temporary paths, directory creation,
Unix socket spelling and an absolute workspace before running on Windows.
No production trust check was weakened to accommodate those tests.
