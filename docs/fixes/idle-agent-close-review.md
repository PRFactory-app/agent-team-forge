# Code review — idle agent close + live retention settings

Reviewer: Claude (Opus 5.5), read-only. Scope: `git diff main...HEAD` on
`feat/idle-agent-close` (5b8e254, 3af3766 + docs), checked against
`docs/fixes/idle-agent-close.md`.

Verdict: **CHANGES_REQUESTED** (2 high, 3 medium, 1 low)

## Summary

The sweep and cap logic in `RetainedSessions` is sound. `_stop` always runs outside
`_gate`. A session taken by `TryTake` is out of the pool, so neither the sweep nor
the cap can close it. The timer is disposed on shutdown. Settings go through two
validation layers (web plus IPC `IsValid`), are persisted atomically through
`WriteMode`, and sit behind the existing token and Origin checks. The UI writes
server data only through `textContent`/`.value`, so it has no XSS. A
`launch-mode.json` without the new fields falls back to 16/5.

The problems are in the new **native-turn reservation** (`_nativeTurns`). A reserved
session is put back into the pool only on the success path. Every other exit
either leaks the TUI permanently or puts a session with a running turn into the
idle pool.

## Findings

### H1 — Native reservation is never released on revert, release or unresolved exits: TUIs leak and follow-ups spawn duplicate agents (regression)

- `RetainedSessions.cs:85-110` (`TakeForNativeTurn` removes the session from `_sessions` and adds it to `_nativeTurns`; `Remember` returns early while it is in `_nativeTurns`)
- Release is missing at: `JobsEndpoint.cs:74` (`RevertNativeClaudeAttempt`, Claude bridge did not start its write), `DispatchJob.cs:960` (`ReleaseNativeAttempt` else branch), `DispatchJob.cs:966/978` (`End(... NeedsReconciliation)`), `DispatchJob.cs:991` (`ReleaseNative` from stop_job), and a `BeginNative*Attempt` transaction that fails after the `idle`/`live` predicate returned true.

Failure scenario (Herdr, Claude):
1. A parent Claude job completes and its pane is retained.
2. The child's bridge takes a native offer. `TakeIdleForNativeTurn` removes the pane from `_sessions` and adds the session to `_nativeTurns`.
3. The bridge cannot start writing and reports `NativeWriteStarted=false`, so `RevertNativeClaudeAttempt` requeues the job for an ordinary resume. No code path restores the reservation.
4. Ordinary dispatch reaches `HerdrInteractiveBackend.Start` (`:52`). `RetainedTabMayBeLive` → `HasIdleSession` → `_liveSessions.IsAlive` returns false because the session is not in `_sessions`. Because of the `&&`, `TryTake` is never called, so `_nativeTurns` is never cleared either.
5. Start launches a **second** Claude TUI with `--resume <same session>` while the old pane is still alive. The old pane is in neither `_sessions` nor any other close path, so it is never idle-closed or cap-evicted.
6. When the new turn settles, `Remember` hits `_nativeTurns.Contains` and returns. The new pane is not retained either. Every later follow-up in that session repeats this and leaks one more pane.

On WT, `Start` calls `TryTake` directly. That clears `_nativeTurns`, but the old tab is not in `_sessions`, so it is not stopped. A second tab starts on the same session and the old tab leaks.

On `main` the native path did not remove the session from the pool, so the revert/resume path reused or closed the old tab.

Suggested fix: store the reserved launch, e.g. `Dictionary<string, InteractiveLaunch> _reserved` filled at reservation time. Add a `ReleaseNativeTurn(sessionId)` that puts the launch back into `_sessions` (the same as the successful `RememberNativeTurn`). Call it on every non-settle exit: Claude revert in `JobsEndpoint`, Codex release, unresolved/`End`, `ReleaseNative`, and a claim transaction that does not commit. Add a test where a native Claude revert is followed by an ordinary resume that reuses or closes the retained pane and leaves only one live launch.

### H2 — Codex native revert on a *running* parent puts a working session into the idle pool; the sweep or a 0-minute timeout closes it mid-turn

- `DispatchJob.cs:497-499` reserves with `running: parent.Status == Running`
- `RetainedSessions.cs:85-92` reserves even when the session is not in `_sessions`
- `DispatchJob.cs:959` calls `RememberNativeTurn` on revert

Failure scenario:
1. A Codex parent is running a long turn (`HasWorkingCodexSession`). A follow-up with `native_codex=1` is claimed through the `running: true` branch.
2. `codex queue` does not start (`!submission.Started`). `RevertNativeAttempt` succeeds, and `RememberNativeTurn(Codex, thread)` calls `Remember(thread, launch)` with `IdleSince = now` while the parent's turn is still working in that pane.
3. With `idle-close-minutes 0`, `Remember`'s `timeout == Zero && Count > 0` loop evicts it at once, and `StopOwned` kills the pane **under the running parent turn**. With the default 5 minutes, the sweep kills it 5 minutes later if the parent turn is still running. An ordinary follow-up can also `TryTake` and "reuse" the pane while it is still working.

Suggested fix: on revert, restore only what the reservation removed. If the session was not in `_sessions` at reservation time (the `running` case), only clear `_nativeTurns` and let the parent's own settle call `Remember`. This falls out naturally if the reservation stores "was retained: launch | null" (see H1).

### M1 — WT `RememberNativeTurn` may retain a stale launch; the live tab ends up untracked

- `WtInteractiveBackend.cs:120-124`

`_jobs` keeps one launch per job, and entries are only removed by `StopOwnedJob`. Every WT follow-up creates a new launch for the same `NativeTranscript.SessionId`, so several entries match. `FirstOrDefault` over a `ConcurrentDictionary` has no defined order.

Failure scenario: a Claude session has jobs A (old tab, already stopped on resume) and B (live tab). A native turn settles, `RememberNativeTurn` picks A's launch, and `_sessions[session] = A`. The idle close then calls `StopOwned(A)`, which does nothing, and B's tab stays open indefinitely. The next follow-up `TryTake`s A, stops nothing, and opens a new tab next to B.

Suggested fix: use the launch captured at reservation (H1). Failing that, pick the launch whose tab `IsAlive`, or the most recent one.

### M2 — Herdr never reports `agent_live: false` after an idle close, so "agent closed" never appears on Linux

- `HerdrInteractiveBackend.cs:191-196`, `HerdrAgentControl.cs:200-207, 283-284`, `DaemonCommand.cs:230-236`

`HerdrAgentControl.StopOwned` removes the `_runs` binding. The session stays in `_nativeSessions`, so `HasLiveSession` calls `StatusAsync`. That calls `Binding(launch)`, which throws `HerdrLaunchException("interactive run is not bound…")`. The DaemonCommand lambda catches it and returns `null`, and the UI shows nothing. The report says "Herdr `StopOwned` removes its owned run/ownership record and status probes report Gone", but for this probe that is not true.

Suggested fix: in `HasLiveSession`, return `false` when the launch has no owned binding. Either add a `HerdrAgentControl.IsBound(launch)` or catch the not-bound case explicitly. Consider also pruning `_nativeSessions` in the stop callback. Add a test: a Herdr session is idle-closed and `HasLiveSession` then returns `false`.

### M3 — The `agent_live` probe on every list/get call is expensive and serial (web poll every 5 s, MCP `list_jobs`)

- `JobsEndpoint.cs:449, 467`, `DaemonCommand.cs:230-236`, `HerdrInteractiveBackend.cs:191-196`, `WtInteractiveBackend.cs:128`

`WithLocations` calls the probe synchronously for every non-running job on the page. The page limit is 50, and the web console polls every 5 s.
- **Herdr:** each job whose pane is still bound costs `ServerProblem` (a process identity read), a `herdr pane get` **process spawn** and a `herdr agent get` **process spawn**. Each has a 5 s CTS. Jobs in the same follow-up chain share a session and are probed again each time. With 16 retained sessions and a few jobs per chain, one poll is roughly 50–100 sequential `herdr` process launches, all on the IPC handler. A hung herdr server stalls the job list for up to 5 s × N.
- **WT/macOS:** each job scans all `_jobs` and calls `IsAlive`. That does sidecar file reads and process identity checks for every stale launch, so the cost is O(jobs × launches) per poll.

Suggested fix: make `agent_live` a cheap in-memory answer, e.g. "is this session in `_sessions`, or currently taken/reserved/running". Compute it once per distinct session per request. Do not probe the terminal from list/get. If a live probe is kept, cache it per session for a few seconds.

### L1 — macOS idle tabs are now closed on daemon shutdown (behavior change)

- `DaemonCommand.cs:189` adds the Mac backends to `interactiveBackends`; the loop at `:396-405` skips only `WtInteractiveBackend`

This was added so that `Dispose` reaches the Mac backends. A side effect is that `MacInteractiveBackend.StopAllIdleSessions` → `RetainedSessions.StopAll` now closes every idle macOS tab on `atf stop`/upgrade. On `main` those tabs were left alone, and Herdr explicitly keeps TUIs on shutdown ("Daemon shutdown is not an explicit request to close human-visible TUIs"). This is untested on macOS.

Suggested fix: skip `MacInteractiveBackend` in the stop loop as well, and only `Dispose` it. Alternatively, dispose the timers from a separate list.

## Checked, no issue found

- Sweep vs `TryTake`/`Start`: removal happens under `_gate` and `_stop` runs outside it. A follow-up that loses the race gets `TryTake == false` and launches with the persisted `ResumeSessionId`, so resume still works. `follow_up`/`revive_agent` keep the session id.
- Timer: created per `RetainedSessions` and disposed before the shutdown stop loop. A callback running at that moment is harmless.
- Ownership: sweep/cap only stop launches this daemon put in `_sessions` through its own `StopOwned` callback.
- Settings endpoint: token plus exact-Origin check on PUT. The web layer validates 0..64 and 0..1440/"off", and IPC `Handle` re-validates with `IsValid` before persisting. A failed write keeps the last good value. Lowering the cap evicts on the next sweep.
- `launch-mode.json`: missing fields fall back to the init defaults (16/5). `ReadSettings` rejects values out of range. The CLI rejects negative numbers through `NumberStyles.None`.
- XSS: the new UI writes only via `textContent`/`value` and `element(...)`.

## Tests run

`dotnet test --filter "FullyQualifiedName~RetainedSessions|FullyQualifiedName~WebConsole|FullyQualifiedName~SetupCommand"`
gave 104 passed and 6 failed, all in `SetupCommandTests` and `InstallScriptTests`:
- `CurrentRelease_MapsOldReleaseImageToCurrent`
- `StableBinaryFollowsCurrentReleaseLink`
- `PartialRegistrationFailureCanBeRerun`
- `LoginAutostartWritesAndRemovesLinuxUnitAndMacPlistInTempHome`
- `ConcurrentBridgesStartOneDaemonThatSurvivesBridgeExit`
- `InstallScriptTests.CompletionPrintsQuotedAbsoluteSetupCommand`

The failures come from Windows symlink privileges, PATH/unit text differences and a daemon.log file lock. None of them touch the diff. The first five are in the report's Windows baseline list. `CompletionPrintsQuotedAbsoluteSetupCommand` is not in that list; it also looks environmental (install script) and unrelated to this change. All new `RetainedSessions` and WebConsole retention tests pass.

None of the new tests cover a native reservation that is released without settling (H1/H2), the stale WT launch (M1) or the Herdr `agent_live` result after a close (M2).

---

# Re-review — fix commit 40a7a4a

Reviewer: Claude (Opus 5.5), read-only. Scope: `git show 40a7a4a`, checked against the "Review fixes" section of `docs/fixes/idle-agent-close.md`.

Verdict: **CHANGES_REQUESTED** (1 medium new issue, 1 low)

## Original findings

| # | Status | Notes |
|---|---|---|
| H1 | Fixed, except for the case in N1 | The reservation is now `Dictionary<string, InteractiveLaunch?>`. It is released when a claim returns null, including a transaction exception (`ClaimNativeCodex`/`TakeNativeClaude` finally blocks), on the Claude pre-write revert (`JobsEndpoint.cs:75`), on Codex not-started/revert/else, on early native-run exits, and on `ReleaseNative` (stop_job). `TryTake` refuses a reserved session. Release is idempotent through `_reserved.Remove`. |
| H2 | Fixed | Releasing a null reservation only clears it. A running parent's own `Remember` during the reservation is recorded but excluded from sweep, cap and zero-timeout eviction until the reservation is released. A revert never makes a working parent idle. |
| M1 | Fixed | Settlement uses the launch captured at reservation, or for a running-parent reservation the tracked live launch. The `FirstOrDefault` over `_jobs` is gone. |
| M2 | Fixed | A `Closed` tombstone is set on every successful stop path (`StopLaunch`, eviction, `Stop`, `IsAlive` miss, `ForgetJobs`). On Herdr, a known launch that is no longer bound (`IsBound`) reports `false`. |
| M3 | Fixed | `HasLiveSession` is now an in-memory lookup in the ledger with no terminal or process probe. The list computes it once per (backend, session). Its states are correct: pooled → true, reserved → true, running → true once bound, closed → false, unknown or after restart → null. |
| L1 | Fixed | The shutdown loop now skips macOS as well as WT, after disposing the timers. |

Lock order: `System.Threading.Lock` is re-entrant, so `Remember` → `Track`/`Closed` inside `_gate` is safe. The SQLite write callbacks take `_gate` (write lock → `_gate`). No path holds `_gate` while waiting on the store, and `_stop` still runs outside `_gate`. I found no deadlock.

Tests: the filter `RetainedSessions|WtInteractiveBackend|WebConsole|NativeClaude|NativeCodex` gave 149 passed and 5 failed. All 5 are the Windows baseline `WtInteractiveBackendTests` failures listed in the report (Pi/Codex resume flags, long prompt, wrapper).

## New findings

### N1 — Medium: unresolved native Codex exits release the reservation while the queued turn may run in that pane

- `DispatchJob.cs:1009` (`native_submission_unresolved`), `DispatchJob.cs:1020` (`native_delivery_unresolved`), `DispatchJob.cs:1024` (the `finally` releases whenever `!awaitingSettlement`)

My original H1 suggestion asked for release on unresolved exits as well. That part of the suggestion was wrong, and this is the result.

For these two exits the queue message *was*, or may have been, handed to `codex queue`. The attempt stays `sent`, `ReconcileNativeCodex` keeps polling, and the code comment says the message can still be presented later. Releasing now puts the captured idle launch back into the pool with a new idle clock:
- With `idle-close-minutes 0`, `Remember` evicts it at once, and `StopOwned` kills the pane that is running, or about to run, the queued turn.
- With the default 5 minutes, the sweep kills it mid-turn if the turn runs longer than that.

When the turn later settles, `RememberNativeTurn` finds no reservation and does nothing. This breaks the invariant that a session is never closed under a running turn.

Fix: set `awaitingSettlement = true` (keep the reservation) before both `End(... NeedsReconciliation, "native_*_unresolved")` calls. The reservation is then released by settlement (`TrySettleNativeCodex` → `RememberNativeTurn`) or by an explicit `ReleaseNative` (stop_job), which already calls `ReleaseNativeTurn`. Release should stay only for not-started, revert, else, cancellation and pre-submit early returns. Add a test: an unresolved submission with timeout 0 must leave the pane open until the native settle.

### N2 — Low: the Claude revert releases even when the revert did not happen

- `JobsEndpoint.cs:74-75`

The `finally` calls `releaseNativeTurn` even when `RevertNativeClaudeAttempt` returns false, which happens when the attempt left `posting` concurrently. In practice that concurrent change was a settle or a stop, and both already released, so this is usually a harmless no-op. It can go wrong in one narrow race: a stop releases the attempt, a new child reserves the same session, and this late `finally` then drops the new reservation. The new native turn's pane is then back in the pool and can be idle-closed under that turn.

Fix: call `releaseNativeTurn` only when the revert returned true. Settle and stop already cover the other cases.
