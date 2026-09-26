# Core stability before v0.0.1

Source audit began at `main` `0a418ec`; concurrent merges advanced it to `91b07cb` before delivery. Affected findings were rechecked against `91b07cb` (notably retained interactive tabs and the new StopAgent operation). Citations use the initial snapshot except updated Herdr lifecycle and slice 5. Linux first; Windows smoke remains unverified.
Read AGENTS.md and HANDOFF.md; historical handoff status is not current runtime evidence.
No production/test code changed, no commit, no real backend or owner Herdr session launched.

Paths below are repository-relative. Reference paths prefixed `reference/` mean
`/home/mikael/code/github/agentic-coder-teams-mcp/` (read-only).
These are source-demonstrable defects/gaps, not claims that each race was reproduced live.

## Prioritized slices

### 1. P0 — Never replay an uncertain Herdr prompt

- **Problem:** A successful/uncertain prompt can execute twice when the transcript is delayed, unavailable, or unreadable and Herdr reports idle/done.
- **Evidence:** `src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrAgentControl.cs:67` swallows timeout/stalled submission errors because delivery may already have happened. `src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrInteractiveBackend.cs:189-221` nevertheless resends after five seconds without transcript evidence. Missing evidence is not proof of non-delivery. The reference explicitly reconciles uncertain receipts instead of redelivering (`reference/src/claude_teams/delivery.py:11-34`).
- **Fix:** Delete automatic second submission. Continue bounded observation, then retain `needs_reconciliation`; retry only a proven pre-delivery failure, never a missing transcript.
- **Test:** Fake control accepts once, reports idle, transcript appears late (also malformed/absent). Prompt call count stays one; late evidence completes, missing evidence becomes uncertain. Cover timeout/stalled responses.

### 2. P0 — Require turn-bound authoritative completion

- **Problem:** Interim assistant text plus a transient idle/done status is treated as the final result, allowing premature follow-up. Transcript scanning can also overwrite this turn's result with later human-turn output. New retained-tab behavior removes automatic completion teardown, but does not fix false completion.
- **Evidence:** `src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrInteractiveBackend.cs:163-173` at `91b07cb` completes on nonempty text without checking `Completed`. `src/AgentTeamForge.Business/Features/Agents/Terminals/InteractiveTranscriptReader.cs:126-149` accumulates all later assistant text and ORs completion flags without stopping at the bound turn boundary.
- **Fix:** Use the native completion record for the correlated turn, not Herdr's visual state alone. Stop attribution at the next native user/turn boundary; do not let subsequent human input complete or alter the machine job. Preserve uncertain tabs rather than declaring success.
- **Test:** Interim commentary + idle must not complete/close; final native completion does. Add later human-turn transcript text and verify the original result remains unchanged. Cover Claude/Codex/Pi fixtures.

### 3. P0 — Restart must not destroy live interactive work

- **Problem:** Restart unconditionally closes every provably owned Herdr session, including a still-running human-visible TUI, before quarantining its job. Ownership authorizes explicit stop, not automatic recovery destruction.
- **Evidence:** `src/AgentTeamForge.Host/Hosting/DaemonCommand.cs:109-127` invokes Herdr recovery before dispatch; `src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrOwnedSessions.cs:33-39` calls the stop callback and deletes ownership records; `src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrTerminal.cs:207-214` delegates recovery to `StopOwnedSessionAsync`. This contradicts HANDOFF.md's explicit interactive crash boundary.
- **Fix:** Small first cut: leave owned TUI and ownership record intact, quarantine uncertain jobs, and block machine follow-ups until explicit stop or verified reconciliation. Do not build seamless TUI adoption for v0.0.1. Keep existing marked headless-process cleanup separate.
- **Test:** Kill only an isolated test daemon while an owned interactive job is live; restart leaves that session alive and blocked, preserves ownership, never touches an unrelated/default session, and still dispatches unrelated unattempted jobs.

### 4. P0 — Fence the native session, not only `running` job rows

- **Problem:** A queued sibling follow-up can resume the same native session after an earlier sibling becomes uncertain but still has a live TUI. Checking only the requested parent at admission is insufficient.
- **Evidence:** `src/AgentTeamForge.DAL/Features/Jobs/JobStore.cs:124-132` excludes only `k.status='running'`. `src/AgentTeamForge.Business/Features/Jobs/FollowUpJob.cs:57-66` checks only the parent's runs. `src/AgentTeamForge.Business/Features/Jobs/DispatchJob.cs:422-427` commits uncertainty on protocol failure, and Herdr disposal at `src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrInteractiveBackend.cs:254-275` at `91b07cb` retains sessions even after noncompletion (under an idle-session abstraction).
- **Fix:** Gate claim on unresolved ownership/uncertainty for the entire native session, including follow-ups whose own session ID has not yet been recorded. Release the gate only after verified exit/idle reconciliation. Conservatively block uncertain siblings rather than implement a new scheduler. Coordinate with slices 3 and 5.
- **Test:** Completed A; queue B and C against A; B reports a control/protocol error while its TUI survives. C must remain undispatched. Repeat across daemon restart and cancellation cleanup; unrelated sessions must still run.

### 5. P1 — Keep failed explicit cleanup retryable

- **Problem:** The newly merged StopAgent path improves retained-tab cleanup, but removes its only in-memory launch handle before trying the stop. A transient Herdr stop failure returns an error once; a retry reports unchanged although the session survives. Capacity eviction also forgets failed stops.
- **Evidence (91b07cb):** `src/AgentTeamForge.Business/Features/Agents/Terminals/RetainedSessions.cs:16-21` removes on TryTake; `:55-70` calls that before stop; `:40-51` also discards failed evictions. `src/AgentTeamForge.Business/Features/Jobs/StopAgent.cs:30-39` maps the first exception to `DaemonUnhealthy`, but the next missing entry to `unchanged`.
- **Fix:** Retain/reinsert failed cleanup ownership without overwriting a concurrently reused session; failed stops must remain retryable. Carry the same durable launch identity through restart as slice 3, so explicit stop works after quarantine without a bare-PID fallback. Keep job outcome separate from session lifetime.
- **Test:** Stop delegate fails once then succeeds: second explicit stop must retry the same proven launch, not return unchanged. Test failed capacity eviction, concurrent resume, and restart cleanup. Never stop a foreign/reused identity.

### 6. P1 — Reconnect a live MCP bridge after daemon death

- **Problem:** Lazy start is performed only when the bridge process starts. A surviving bridge cannot restart a crashed daemon on its next tool call; cached session/wake state does not trigger a new startup attempt.
- **Evidence:** `src/AgentTeamForge.Host/Features/Jobs/JobsMcpBridge.cs:98-105` calls `SetupCommand.StartAsync` once. Tool dispatch at `:189-205` and `:259-264` sends directly through `IpcClient`; `src/AgentTeamForge.Host/Transport/IpcClient.cs:89-101` reports failures but intentionally never reconnects/retries.
- **Fix:** Keep IPC transport nonreplaying. At the bridge boundary, on provably-unsent `daemon_unavailable`, use the existing start.lock-protected starter and retry once with the unchanged request/key. Never auto-replay `outcome_unknown`. Retain/rebind the existing durable lead/wake identity as needed, including external-only calls.
- **Test:** Keep one MCP connection open, kill its isolated daemon, call get/list and then submit: daemon restarts and lead identity persists. Concurrent bridges start one daemon. Lost response after acceptance still yields `outcome_unknown` and same-key recovery yields one job.

### 7. P1 — Read Claude interactive transcripts from the launched profile

- **Problem:** Custom Claude profile/config directories are passed to Herdr agents but ignored when finding their transcripts. Successful jobs then become unobserved/uncertain (and currently hit slice 1's duplicate-send path).
- **Evidence:** `src/AgentTeamForge.Business/Features/Agents/Terminals/LaunchEnvironment.cs:35` allows `CLAUDE_CONFIG_DIR`; `src/AgentTeamForge.Business/Features/Agents/Terminals/InteractiveTranscriptReader.cs:68-74` hardcodes `~/.claude/projects`. Headless Claude already respects the override for transcript initialization (`src/AgentTeamForge.Business/Features/Agents/Backends/ClaudeCodeBackend.cs:176-178`).
- **Fix:** Resolve the transcript root from the same effective launch environment used for the agent. Reuse that resolution for headless/interactive lookup; no new configuration mechanism.
- **Test:** Isolated `CLAUDE_CONFIG_DIR` containing the sole correlated transcript completes and resumes. Default home contains a decoy and is not consulted. Do not modify real user config.

### 8. P2 — Wire tier availability checking into production admission

- **Problem:** Tier mapping matches the reference, but its unavailable-model check exists only behind an uninjected optional delegate. Production accepts known-unavailable tier targets and only discovers the problem after launch.
- **Evidence:** `src/AgentTeamForge.Business/Features/Jobs/ModelSelection.cs:60-71` checks only `discoverModels?.Invoke`; `src/AgentTeamForge.Host/Hosting/DaemonCommand.cs:178-183` constructs both acceptors without that delegate. Compare `reference/src/claude_teams/backends/codex.py:239-254` and `reference/src/claude_teams/backends/pi.py:246-280`.
- **Fix:** Wire a bounded, cached backend model discovery into both production acceptors. If discovery returns a nonempty catalog and the tier target is missing, reject before acceptance with the backend upgrade hint. Preserve unknown-catalog behavior, tier-owned effort, and raw-slug behavior; do not silently downgrade.
- **Test:** Exercise the actual composition with fake discovery: present/missing/empty catalog, Codex vs Pi `medium-fast`, retired `high-fast`, and follow-up inherited selection. Verify missing known tier creates no job/process.

## Flows checked without a new slice

- **Submit/headless:** all three real backends pass bypass flags and resume native sessions; admission + intent are transactional (`JobStore.cs:27-111`), claim records precede effects (`:121-160`). Existing fake/unit tests are not live CLI compatibility proof.
- **Busy/idle follow-up and interrupt:** busy without `interrupt` deliberately returns `parent_not_ready` (`FollowUpJob.cs:41-47`); preserve that explicit scope for v0.0.1 instead of copying the reference's larger pending-delivery machinery. Idle resume and atomic parent cancellation/child acceptance already exist. Session-lifetime holes are slices 3–5.
- **Lazy start/start.lock:** existing gate serializes starters (`SetupCommand.cs:134-157,349-360`); do not replace it. Linux ownership cleanup uses marker + pidfd, not PID alone (`OrphanedBackendProcess.cs:110-163`).
- **Wake:** terminal rows/messages commit before scan; notices contain counts/read instructions, not results (`WakeCoordinator.cs:51-107`). Reference's notice-only model is retained (`reference/src/claude_teams/native_wake.py:204-228`). Native Windows Claude wake is explicitly unavailable (`ClaudeChannelWake.cs:13-16`), not verified support.
- **Prune:** retains unread wake jobs and ancestors of retained descendants, deletes child-first in one transaction (`src/AgentTeamForge.DAL/Features/Jobs/PruneJobs.cs:19-86`). Do not add automatic worktree deletion to this stability pass.
- **External join:** transactional deterministic-token replay until ticket expiry already exists (`src/AgentTeamForge.DAL/Features/External/ExternalMemberStore.cs:149-210`); basic join/send/read/leave need smoke coverage, not a rewrite. MCP's “one-time” wording is stale, not a stability slice.
- **Tier mapping:** Luna/Sol/Astra effort ladder and Pi-only medium-fast match reference. Missing production discovery is slice 8, not a mapping-table change.

Known tracked minors are not duplicated: WindowsTabJob.Assign sidecar race, macOS launch without setsid, ReadTeam sender-cursor advancement.

## Validation and release boundary

- Before the concurrent main merges, pinned SDK `11.0.100-rc.1.26425.128`: fresh `dotnet restore AgentTeamForge.slnx` followed by `dotnet test AgentTeamForge.slnx -c Release --no-restore` passed **393**, skipped **5** opt-in real-agent/Herdr tests, failed **0**. Build ran as part of test. Tests used private `/tmp/atf-stability-*` roots.
- Initial no-restore runs failed 23 web tests because stale restore assets omitted `Microsoft.AspNetCore` from the test runtime; fresh restore resolved all failures. This was not a newly attributed product defect.
- `dotnet format AgentTeamForge.slnx --verify-no-changes --no-restore`: passed.
- No published-binary smoke or real-agent run performed by this audit. No Linux release verification or Windows support claim follows from source review.
- After fixes, run existing `scripts/verify.sh`, then a small published-binary Linux matrix: Claude/Codex/Pi × headless/owned Herdr, submit → result → follow-up; busy refusal/interrupt; targeted stop; daemon/bridge restart; native notice → authenticated read; external join/send/read/leave; prune dry-run/apply. Use private `/tmp/atf-stability-*` state and a non-default web port.
- Windows handoff must include binary, exact versions, commands, and expected outcomes for the same core operations in explicit headless/Windows Terminal modes. Test on Windows; record unsupported native wake rather than silently calling polling equivalent.
