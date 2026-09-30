# Idle agent close implementation report

Implemented on `feat/idle-agent-close`. No push performed.

Implementation commits: **5b8e2542ad7c198f2f6c8922ad460462f6bc482a** (idle close) and **3af3766da4d9b65d778cc5da6052870e44606948** (live timeout/cap settings and console). Initial report commit: **e7f2f30ae17719412472a9184cc57417c447a04b**. This final report-only commit records the stable implementation hashes.

## Design and configuration

`RetainedSessions` now owns a disposable 30-second `TimeProvider` timer. A monotonic timestamp is recorded when a settled turn calls `Remember`. The sweep checks expiration and removes each eligible session under the existing lock, then invokes the existing owned-session stop callback outside the lock. `TryTake` removes an entry under that same lock before ordinary follow-up reuse. Successful closure drops retention; cleanup exceptions remain retryable through the same best-effort path as LRU eviction. The configurable retained-session cap still applies (default 16); lowering it evicts the oldest entries on the next sweep.

Settings are stored in the state directory's **`launch-mode.json`** (the actual launch settings file in this checkout, rather than `settings.json`):

- **`idle_close_minutes`**: default/absent **5**, range **0..1440**, or **-1** for off. Zero closes when the turn settles.
- **`max_retained_sessions`**: default/absent **16**, range **0..64**. Zero retains no idle sessions.

Both are editable in **web console Settings → Idle interactive agents**. `GET/PUT /api/settings/retention` follows the existing tiers route and bearer/loopback host/origin checks. HTTP PUT requires both fields; idle minutes accepts a number in 0..1440 or the string `off`. The daemon IPC handler independently validates both limits, maps off to -1, and persists through the existing atomic private-file `WriteMode` path. Unrelated launch settings are preserved. Error responses use the console's existing conventions. The small form uses number inputs, an off checkbox, and textContent for status.

CLI options: `atf setup --idle-close-minutes MINUTES|off --max-retained-sessions COUNT`; omitted values preserve existing settings. **No daemon restart is required.** All Windows, macOS and Herdr backends share one live settings source, reloaded on Remember and every sweep. A shortened timeout uses the original idle timestamp. Lowering the cap closes the oldest excess idle sessions within the next 30-second sweep; increasing it allows subsequent turns to retain more sessions. Off disables only timeout closure, not the cap or periodic check. Invalid/unreadable external edits retain the last known good limits; endpoint writes reject invalid values before persistence.

Backend timers are disposed on daemon shutdown. Active ordinary/native turns are outside the retained idle pool, so changing either limit does not stop them.

Native Claude/Codex delivery bypasses backend `Start`. Those claim paths now reserve/remove the retained session under the same lock before delivery and restart retention after native settlement. A native-turn marker prevents the prior run's late unwind from re-retaining an agent during delivery. An ordinary resume clears stale native markers. A Codex queue submission that never started restores idle retention before the existing resume fallback.

## Liveness and resume verification

LRU eviction did not write a separate stopped status to SQLite; it invoked `StopOwned`, leaving the completed job result and native session ID intact. Timeout eviction uses that exact callback. Successful owned cleanup now marks the session closed in the daemon liveness ledger. Herdr removes its owned run/ownership record; an absent in-memory control binding also reports closed, without calling StatusAsync.

Job responses expose nullable **`agent_live`** from daemon-owned in-memory state: a known retained, reserved or running/taken session is live, an owned launch closed by this daemon is false, and unknown ownership is null. False produces an **“agent closed”** label in the web console. Shared sessions are evaluated once per request. No terminal commands, sidecar reads, process-identity checks or scans over the WT job dictionary run from job_get/job_list liveness. This deliberately reports daemon knowledge, rather than probing for an external process death on every read.

Verified the resume path in code: Herdr `Start` reuses only a retained live session; when none remains it launches with the persisted `ResumeSessionId`. Windows/macOS `Start` launches with that same resume ID. Native eligibility becomes false after owned cleanup and ordinary dispatch resumes from the saved transcript. `follow_up` and `revive_agent` preserve the saved session identifier; no transcript or result deletion was introduced. This was code verification and fake-control testing, not a real interactive GUI session test on all three platforms.

## Changed files

- `docs/usage.md`
- `src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrInteractiveBackend.cs`
- `src/AgentTeamForge.Business/Features/Agents/Terminals/IInteractiveSessionStop.cs`
- `src/AgentTeamForge.Business/Features/Agents/Terminals/MacTabControl.cs`
- `src/AgentTeamForge.Business/Features/Agents/Terminals/RetainedSessions.cs`
- `src/AgentTeamForge.Business/Features/Agents/Terminals/WtInteractiveBackend.cs`
- `src/AgentTeamForge.Business/Features/Jobs/DispatchJob.cs`
- `src/AgentTeamForge.Business/Features/Jobs/JobContracts.cs`
- `src/AgentTeamForge.Business/Features/Jobs/ListJobs.cs`
- `src/AgentTeamForge.Host/Features/Jobs/JobsEndpoint.cs`
- `src/AgentTeamForge.Host/Features/Setup/SetupCommand.cs`
- `src/AgentTeamForge.Host/Features/WebConsole/wwwroot/app.js`
- `src/AgentTeamForge.Host/Hosting/CommandLine.cs`
- `src/AgentTeamForge.Host/Hosting/DaemonCommand.cs`
- `tests/AgentTeamForge.Tests/Features/Agents/Terminals/RetainedSessionsTests.cs`
- `src/AgentTeamForge.Business/Features/Agents/Terminals/InteractiveRetentionSettings.cs`
- `src/AgentTeamForge.Host/Features/Setup/InteractiveRetentionConfiguration.cs`
- `src/AgentTeamForge.Host/Transport/IpcMessages.cs`
- `src/AgentTeamForge.Host/Features/WebConsole/WebConsoleServer.cs`
- `src/AgentTeamForge.Host/Features/WebConsole/wwwroot/index.html`
- `tests/AgentTeamForge.Tests/Features/WebConsole/WebConsoleServerTests.cs`
- `docs/fixes/idle-agent-close.md` (this report)

## Validation

SDK: `C:/Projekt/git/agent-team-forge/.tools/dotnet11/dotnet.exe`.

- `dotnet build AgentTeamForge.slnx -c Release -warnaserror`: **passed, 0 warnings, 0 errors**.
- Three new deterministic tests: idle expiration via the injected timer; taking a session prevents expiration (also tests native reservation against a late Remember and resets the clock after native settlement); timeout zero does not retain. **3/3 passed**. All RetainedSessions tests pass.
- Requested terminal/Herdr selection (`FullyQualifiedName~Terminals|FullyQualifiedName~Herdr`): feature **255 passed, 66 failed, 5 skipped, 326 total**; exported main **252 passed, 66 failed, 5 skipped, 323 total**. Failed test names match exactly.
- Final run adds NativeCodexDelivery, NativeClaudeDelivery, SetupCommandTests and CommandLineTests to that selection: **344 passed, 71 failed, 5 skipped, 420 total**. All 71 failures match the union of main baseline failures; **no new failing test**. Main's additional selection alone: **90 passed, 5 failed, 95 total** (one test overlaps the terminal/Herdr selection).
- Baseline commit: `6d496da9c839fe95423a407b3ead7353dca33180`, exported inside this worktree and built/tested with the same SDK, configuration and filters. Main's working tree was not modified.
- Native AOT publish attempted and blocked by missing Visual C++ toolchain: `vswhere.exe failed to locate Visual Studio with Microsoft.VisualStudio.Component.VC.Tools.x86.x64`. No claim of successful AOT publishing.
- Managed publish (`dotnet publish ... -c Release --no-build -p:PublishAot=false`) **passed**. Published `.idle-publish-managed/atf.exe --version` with `DOTNET_ROOT` pointing to the supplied SDK **passed**, output `atf 0.0.1-dev`. CLI help includes the new option.
- `git diff --check`: **passed**.
- No Linux/macOS machine execution and no opposite-family review performed by this implementation worker; integration review remains with the parent workflow.

## Live-settings follow-up validation

The additional request made both limits editable in the console without restarting. The final Release build with `-warnaserror` passes with **0 warnings and 0 errors**. Final combined selection includes terminal/Herdr, native Claude/Codex delivery, SetupCommandTests, CommandLineTests and WebConsoleServerTests: **386 passed, 71 failed, 5 skipped, 462 total**. The failure names match the main baseline union exactly. Main's WebConsoleServerTests selection adds **41 passed, 0 failed** to the earlier baseline. All **four new tests** pass (three retention tests plus one endpoint test).

The focused endpoint test checks bearer/origin rejection, server-side missing/out-of-range/off validation, persistence and reopening the settings source, preservation of another launch setting, live oldest-first cap reduction, and zero limits. It exercises the actual HTTP route and daemon settings handler with a temporary JSON file store. Read/write delegates isolate the pre-existing Windows `StateDirectory.Open` ACL failure in the test fixture; production still uses the existing StateDirectory private-file reads and atomic WriteMode implementation. An initial test run hit that fixture failure before reaching the endpoint; after this test isolation, no new failures remain.

`node --check src/AgentTeamForge.Host/Features/WebConsole/wwwroot/app.js` passes. The final managed publish and published `atf.exe --version` smoke run pass. Published CLI checks reject cap 65 and timeout 1441 with exit code 64 before changing state. The previously recorded missing native AOT C++ toolchain remains a limitation. No real browser interaction or Linux/macOS runtime test was performed by this worker.

## Pre-existing Windows failures

These exact names failed both on exported main and in final feature verification. The terminal failures include platform-specific Unix file permission/process assumptions and Windows fake terminal behavior; setup failures include unavailable symbolic-link privileges. They were not changed or hidden.

- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrInteractiveBackendTests.CodexTrustsOnlyTheLaunchDirectoryViaConfigOverride`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrInteractiveBackendTests.Idle_interrupted_turn_settles_and_follow_up_reuses_native_session`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrInteractiveBackendTests.Restart_logs_and_keeps_unprovable_Herdr_record_without_blocking_startup`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrInteractiveBackendTests.Restart_preserves_recorded_Herdr_session_and_fences_interrupted_follow_up`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Agent_start_failure_is_no_effect_only_with_verified_cleanup(cleanupFails: False)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Agent_start_failure_is_no_effect_only_with_verified_cleanup(cleanupFails: True)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Blocked_startup_sends_no_prompt`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Close_restored_refuses_non_atf_recorded_agent_name`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Close_restored_refuses_record_from_another_session`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Command_OutputAndTimeAreBounded`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.ExistingDefaultCodexHomeIsPinnedForSharedTab`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Follow_up_admitted_after_the_record_vanished_keeps_the_job_uncancelled`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Legacy_record_derives_agent_name_from_file_name`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Owned_reconciled_job_stop_cancels_its_deferred_child_with_the_parent`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Prompt_waits_for_stable_ready_state_before_sending(kind: Claude)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Prompt_waits_for_stable_ready_state_before_sending(kind: Codex)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Prompt_waits_for_stable_ready_state_before_sending(kind: Pi)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Recognized_startup_blocker_fails_without_delivery_or_fence(kind: Claude, screen: "Choose the text style that looks best with your te"···, reason: "agent_first_run_required")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Recognized_startup_blocker_fails_without_delivery_or_fence(kind: Claude, screen: "Choose the theme\nDark mode", reason: "agent_first_run_required")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Recognized_startup_blocker_fails_without_delivery_or_fence(kind: Claude, screen: "Claude account with subscription\nAnthropic Consol"···, reason: "agent_login_required")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Recognized_startup_blocker_fails_without_delivery_or_fence(kind: Claude, screen: "Not logged in · Please run /login", reason: "agent_login_required")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Recognized_startup_blocker_fails_without_delivery_or_fence(kind: Claude, screen: "Select login method", reason: "agent_login_required")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Recognized_startup_blocker_fails_without_delivery_or_fence(kind: Claude, screen: "Try the new fullscreen renderer?", reason: "agent_first_run_required")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Recognized_startup_blocker_fails_without_delivery_or_fence(kind: Claude, screen: "Yes, I trust this folder", reason: "agent_workspace_trust_required")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Recognized_startup_blocker_fails_without_delivery_or_fence(kind: Codex, screen: "Do you trust the contents of this directory", reason: "agent_workspace_trust_required")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Recognized_startup_blocker_fails_without_delivery_or_fence(kind: Codex, screen: "Sign in with ChatGPT\nUse an API key", reason: "agent_login_required")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Restart_keeps_unrebindable_live_pane_fenced_and_fails_only_a_proven_gone_one`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Restart_reattaches_a_submitted_job_to_its_saved_live_pane`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Restart_stop_retries_durable_identity_and_refuses_a_reused_server`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Restored_pane_with_recorded_agent_name_is_closed`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Restored_sweep_never_closes_unproven_panes(name: "atf0123456789abcdef0123", pane: "w1:p2", tab: "w1:t2", restarted: False, sessionRunning: True)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Restored_sweep_never_closes_unproven_panes(name: "atf0123456789abcdef0123", pane: "w1:p2", tab: "w1:t2", restarted: True, sessionRunning: False)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Restored_sweep_never_closes_unproven_panes(name: "atf0123456789abcdef0123", pane: "w1:p2", tab: "w1:t7", restarted: True, sessionRunning: True)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Restored_sweep_never_closes_unproven_panes(name: "atf0123456789abcdef0123", pane: "w1:p7", tab: "w1:t2", restarted: True, sessionRunning: True)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Restored_sweep_never_closes_unproven_panes(name: "otherName", pane: "w1:p2", tab: "w1:t2", restarted: True, sessionRunning: True)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Setup_text_in_history_never_blocks_a_ready_resumed_or_retained_pane`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.SharedPlacement_ClosesOnlyRecordedTab`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.SharedPlacement_CreatesRepoWorkspaceOnceAndReusesItUnderConcurrentSpawns`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.SharedPlacement_RestartStopUsesDurablePaneRecord`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.SharedTabPinsRelativeCodexHomeEvenWhenServerHasAnotherEnvironment`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Stop_after_restart_closes_proven_resumed_pane_and_keeps_record`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Stop_agent_after_restart_releases_fence_without_changing_job_outcome`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Sweep_refuses_record_whose_name_is_not_its_atf_file_name(fileName: "atfaaaaaaaaaaaaaaaaaaaa", savedName: "atf0123456789abcdef0123")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Sweep_refuses_record_whose_name_is_not_its_atf_file_name(fileName: "bootstrap", savedName: "atf0123456789abcdef0123")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Sweep_refuses_record_whose_name_is_not_its_atf_file_name(fileName: "claude", savedName: "claude")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Unavailable_terminal_removes_its_bootstrap`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Uncertain_turn_without_native_session_keeps_tab_fenced_until_stop_agent`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.Unknown_screen_keeps_waiting_without_submitting`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.UnreadableWorkspaceListingNeverCreatesWorkspace`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.UnsettledPrompt_IsSubmittedOnceAndEndsUncertain(code: "agent_prompt_stalled", processTimeout: False)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.UnsettledPrompt_IsSubmittedOnceAndEndsUncertain(code: "timeout", processTimeout: False)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.UnsettledPrompt_IsSubmittedOnceAndEndsUncertain(code: null, processTimeout: True)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.HerdrTerminalTests.WorkspaceLabel_UsesMainCheckoutForWorktree`
- `AgentTeamForge.Tests.Features.Agents.Terminals.InteractiveTranscriptReaderTests.Claude_limit_reset_times_are_resolved_in_their_zone(observed: "2026-09-27T10:00:00Z", text: "You've hit your session limit · resets 12:30am (Am"···, expectedUtc: "2026-09-28T04:30:00")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.InteractiveTranscriptReaderTests.Claude_limit_reset_times_are_resolved_in_their_zone(observed: "2026-09-27T21:00:00Z", text: "You've hit your session limit · resets 3pm (Europe"···, expectedUtc: "2026-09-28T13:00:00")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.InteractiveTranscriptReaderTests.Claude_limit_reset_times_are_resolved_in_their_zone(observed: "2026-10-24T22:30:00Z", text: "Your limit will reset at 3am (Europe/Berlin)", expectedUtc: "2026-10-25T02:00:00")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.InteractiveTranscriptReaderTests.Claude_monthly_spend_limit_maps_to_rate_limit_with_utc_reset`
- `AgentTeamForge.Tests.Features.Agents.Terminals.MacTabControlTests.ManagedMacWrapperCarriesPrivateMcpConfigOnResume(kind: Codex, resume: "'resume' 'native-1'")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.MacTabControlTests.ManagedMacWrapperCarriesPrivateMcpConfigOnResume(kind: Pi, resume: "'--continue'")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.MacTabControlTests.WrapperQuotesResolvedArgumentsAndResume`
- `AgentTeamForge.Tests.Features.Agents.Terminals.WtInteractiveBackendTests.LongWindowsPromptIsHandedOverAsItsFileWithTheCorrelationMarker(kind: Pi)`
- `AgentTeamForge.Tests.Features.Agents.Terminals.WtInteractiveBackendTests.ManagedWtLaunchAndResumeCarryPrivateMcpConfig(kind: Codex, resumeFlag: "resume")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.WtInteractiveBackendTests.ManagedWtLaunchAndResumeCarryPrivateMcpConfig(kind: Pi, resumeFlag: "--continue")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.WtInteractiveBackendTests.TabLaunchAndResumeCarryResolvedSelection(kind: Pi, options: "model=gpt-6-luna;effort=max", modelFlag: "--model", model: "openai-codex/gpt-6-luna", effortFlag: "--thinking", effort: "max")`
- `AgentTeamForge.Tests.Features.Agents.Terminals.WtInteractiveBackendTests.WrapperKeepsPromptAndUsesResumeWithoutExposingItToWt`
- `AgentTeamForge.Tests.Features.Jobs.HerdrPlacementTests.PersistsPrivateDefaultAndResolvesOverride`
- `AgentTeamForge.Tests.Features.Setup.SetupCommandTests.ConcurrentBridgesStartOneDaemonThatSurvivesBridgeExit`
- `AgentTeamForge.Tests.Features.Setup.SetupCommandTests.CurrentRelease_MapsOldReleaseImageToCurrent`
- `AgentTeamForge.Tests.Features.Setup.SetupCommandTests.LoginAutostartWritesAndRemovesLinuxUnitAndMacPlistInTempHome`
- `AgentTeamForge.Tests.Features.Setup.SetupCommandTests.PartialRegistrationFailureCanBeRerun`
- `AgentTeamForge.Tests.Features.Setup.SetupCommandTests.StableBinaryFollowsCurrentReleaseLink`

## Review fixes

Implementation commit: **40a7a4a317eacb8b31ddbcbb1cef030d5cfb37a2**. The report update is committed separately. Read and addressed every finding in `docs/fixes/idle-agent-close-review.md`.

- **H1:** Native reservations are now `Dictionary<string, InteractiveLaunch?>`: the exact removed idle launch, or null for a session reserved while running. `ReleaseNativeTurn` is idempotent and restores only a captured idle launch, restarting its idle clock. Release is wired to Claude pre-write revert in JobsEndpoint, both native claim transactions when no claim is returned (including transaction exceptions), Codex non-start/revert and its ReleaseNativeAttempt else branch, unresolved/NeedsReconciliation exits, cancellation and early native-run exits, and successful native stop_job release. Successful delivery stays reserved until its transcript settles. Rollback/revert/resume tests use real SQLite transactions, an aborting insert trigger, the authenticated Claude completion endpoint and a tracking WT control; exactly one launch remains after ordinary resume and none leaks after idle stop.
- **H2:** Releasing a null reservation only clears the reservation. It never fabricates an idle settlement for a working parent. The parent's own Remember determines when it is idle. If that parent settles during native submission, its settlement is recorded but protected from sweep, cap eviction and TryTake until the reservation clears. The zero-timeout regression tests both orderings and confirms the running parent is never closed on revert.
- **M1:** WT native settlement consumes the launch captured by the reservation, with no FirstOrDefault over `_jobs`. The regression creates an old stopped WT launch and a new live launch for one session; native settlement and subsequent idle stop close the new launch, leaving no live tab.
- **M2:** Successful stop callbacks and explicit owned stops update an in-memory closed tombstone. Herdr also checks its real control's in-memory IsBound dictionary; an unbound known launch reports false rather than null. The regression idle-closes a Herdr pane, then makes the fake control throw on any status probe and confirms HasLiveSession is false; an unknown session remains null.
- **M3:** Deleted terminal/process probing from generic HasLiveSession. The retained-session ledger tracks bindings, taken/running sessions, native reservations and closed launches. JobsEndpoint evaluates each distinct backend/session once per list request; get evaluates its one session once. WT regression checks two job rows sharing a session produce one lookup and no physical probe.
- **L1:** The shutdown loop skips macOS backends alongside WT after disposing their timers. macOS idle tabs remain open on daemon stop/upgrade, matching main's behavior and Herdr's shutdown policy.

Validation with the supplied .NET 11 SDK:

- `dotnet build AgentTeamForge.slnx -c Release -warnaserror`: **passed, zero warnings/errors**.
- Requested groups (`RetainedSessions`, `WtInteractiveBackend`, `Herdr*`, native Claude/Codex delivery, `DispatchJob`, `WebConsole`, `SetupCommand`): feature **297 passed, 65 failed, 5 skipped, 367 total**; exported main **290 passed, 65 failed, 5 skipped, 360 total**. Exact failed test names match.
- Additional dispatch fault/concurrency coverage: **12 passed, 0 failed** on both feature and main.
- Combined verification: feature **309 passed, 65 pre-existing failures, 5 skipped, 379 total**; main **302 passed, 65 failed, 5 skipped, 372 total**. All **seven new tests** relative to main pass, including the **three review regressions**.
- The broader requested selector also matches `AgentTeamForge.Tests.Features.Setup.InstallScriptTests.CompletionPrintsQuotedAbsoluteSetupCommand`. This failure was absent from the earlier narrower report but is now independently reproduced on exported main. The other selected failures are already in the earlier Windows baseline list. No new feature failure was introduced.
- Managed publish and the published `atf.exe --version` smoke run pass (`atf 0.0.1-dev`). The previously documented unavailable native AOT C++ toolchain remains unchanged.
- `git diff --check` passes. Files were staged explicitly. The lead's uncommitted `.github/workflows/release.yml` change and untracked review document were not edited, staged or committed. No push performed.
