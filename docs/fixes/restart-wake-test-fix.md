# Restart wake test fix

Root cause: test/platform issue, not a product wake migration bug. On Windows, `/workspace/shared` is rooted but not fully qualified (it has no drive). `JobsEndpoint.ValidWorkspace` uses `Path.IsPathFullyQualified`, so the SessionResume request returns `invalid_request`. The assertion at original line 121 fails before wake rebinding is reached. JobSubmit and direct LeadSessionStore.Start calls do not use that endpoint validation, which explains why earlier steps pass.

Git evidence: `git blame` attributes both the test's Unix workspace literal and the endpoint's fully qualified workspace requirement to fceb13ca25626a547dd3ab6dc5436b266d74e0eb (feat: scope MCP jobs to recoverable lead sessions, 2026-09-26). The bug therefore predates PR #32, merged as 6134801. PR #32's implementation 4f11dcfa1a41906966f66bba47d91f03adcf7fa1 changes native wake discovery/registration and transport, not this test or validation. d1704704c521e09b559f0c2926ad98368f316e45 changes Claude pipe-name acceptance and tests, also unrelated. The original test was explicitly rebuilt and reproduced on this branch at line 121.

Fix: derive one fully qualified workspace from the fixture database directory using Path.GetFullPath(Path.GetDirectoryName(f.DatabasePath)!), and use it for both old and fresh lead sessions. Include resumed.Error in the existing success assertion for better diagnostics. All assertions remain: distinct lead identities, recoverability, successful resume, retained job visibility, wake_jobs target migration, successful job completion, and pending wake delivery to the new target. No production behavior or pipe addresses changed. Wake registrations here are DAL records; this test does not open a native wake pipe.

Branch: atf/job-job_01a0f24d13037f078bf2289bac8529d0
Commit: ace75c11df815f94164253b5119cc34764c570fd
Commit title: Fix restart wake test workspace on Windows
Worktree is clean. No push performed. This report is outside the worktree and is not committed.

Validation on Windows with C:\Projekt\git\agent-team-forge\.tools\dotnet11\dotnet.exe (.NET 11 preview SDK):

- Original test, rebuilt with --filter "FullyQualifiedName~Restarted_lead_resumes_jobs": total 1, passed 0, failed 1, skipped 0; expected reproduction at original line 121.
- Initial corrected test with the same filter: total 1, passed 1, failed 0, skipped 0.
- Final corrected implementation (including Path.GetFullPath), --no-restore --filter "FullyQualifiedName~Features.Wake|FullyQualifiedName~Features.Sessions": total 62, passed 62, failed 0, skipped 0. Includes the restart regression test and all Wake/Sessions feature tests. Duration 17 seconds.
- Build completed successfully as part of dotnet test; git diff --check passed.

This is a test-only change; no runtime publishing/smoke was required. Linux/macOS were not executed in this Windows worktree; the fixture-derived fully qualified path uses platform-native .NET path handling.