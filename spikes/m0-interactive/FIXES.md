# Fixes for CODE-REVIEW.md (post-review repair)

Implementer: Claude Opus 5.5 (Claude Code). **Not self-approved**; this needs GPT/Codex
re-review. Scope: this spike only, still .NET 10 (SDK 10.0.401). CODE-REVIEW.md is unchanged.
Raw evidence under `evidence/` remains local verification material; see the README
publication boundary.

## Gates

`scripts/gates.sh` exits 0 (`evidence/gates-fixes.log`):

- `dotnet format --verify-no-changes`: pass.
- Release build with `-warnaserror`: 0 warnings, 0 errors.
- Tests: **103/103 passed, 0 skipped**. The review baseline was 49.

**Red first.** Each new test group failed first, at compile time, because the new types did
not exist yet. Mutation checks confirmed that the key tests have teeth:

- Removing the claim state and fence checks in `DispatchClaims.TryClaim` fails 4 of the 6
  claim tests, including the two-process race.
- Restoring a text-tag fallback in Codex evidence, and treating every history error as
  "empty", fails 5 of the 8 evidence and history tests.
- The symlinked-socket test failed before `SocketOwnership` resolved links.

**Not run:**

- Native AOT publish and published-binary smoke: the ILCompiler package is not cached.
- Markdown lint.
- Windows and macOS.
- Human T05/T15 verification.

## Per-finding disposition

| # | Finding | Disposition | Tested evidence |
| --- | --- | --- | --- |
| 1 | Concurrent sends can deliver the same job twice | **Fixed.** `Delivery/DispatchClaims.TryClaim` runs inside one `StateStore.Update` lock. It moves only an unattempted `Accepted` job to `Dispatching`, gives it a fresh attempt id, and sets a per-conversation fence (`FenceJobId`/`FenceAttemptId`). All backend, binding, history and busy checks run *after* the claim. A refused check uses `ReleaseUnsent`, which is valid only for the owning attempt and only when nothing was written. The fence is freed only by a terminal outcome; uncertainty keeps it. | `DispatchClaimTests.*` (6), incl. `Two_processes_racing_for_one_conversation_produce_exactly_one_claim`: two real `AtfSpike claim-probe` processes, with the lock held while claimed. Live: two concurrent `send` processes on one Codex conversation → one `claim_refused` ("fenced"), one completed, and exactly one new turn (`evidence/fixes-live/codex-two-sender-race.jsonl`). |
| 2 | Human or foreign input can forge job completion | **Fixed for Codex; documented limitation for Claude.** Codex evidence uses only the native `clientId` = `atf1.<job>.<attempt>` (`Codex/ClientMessageId.cs`), and the text-tag fallback is gone. `JobReconciler` rejects evidence from another attempt or another turn id, and evidence for a never-attempted job. **Claude:** hooks cannot prove who typed a prompt. `ClaudeCorrelation` accepts only the first prompt after the claim whose SHA-256 equals the exact written text; everything else is foreign. Results are labelled "human origin not excluded", and Claude is documented as **not a supported safe adapter**. | `CodexThreadEvidenceTests.A_typed_tag_or_malformed_client_id_is_a_foreign_turn_not_job_evidence` (4 cases), `JobReconcilerBindingTests.*`, `ForeignTurnTests.*`, `ClaudeCorrelationTests.*` (5: tag before attempt, different text, never-attempted job, second match). Live: a prompt with a spoofed `[atf-job:f-j4]` typed into the Codex TUI via Herdr was not job evidence; the job stayed `Accepted` and unsent (`evidence/fixes-live/codex-foreign.jsonl`). |
| 3 | Teardown relies on a reusable name | **Fixed.** `session-up` persists intent, then creates a session with a random `atf-spike-<12 hex>` name and refuses any name already in Herdr's list, running or stopped. It records the server PID and start time (the single process whose argv is exactly `herdr --session NAME server`) and creates a workspace labelled with a random owner label. `teardown` stops and deletes only when the recorded session exists, the running server matches the recorded PID and start time, and the owner-label workspace is present. A stopped owned session is deleted without a stop. Earlier fixed-name sessions and schema-1 state are never adopted. | `HerdrOwnershipTests.*` (4, fake Herdr JSON, incl. a legacy fixed-name session refused). Live: the owned session was created. Live teardown was deliberately **not** run, so the session stays available as the E2E demo. |
| 4 | Codex sends through an unverified or stale binding | **Fixed.** `CodexBinding.Problem` fails closed before every send, cancel and reconcile-idle. It requires: the owned Herdr server; app-server PID and start time (now persisted); the listening socket's inode held by that app-server (`SocketOwnership`, via `/proc/net/unix` and fd links, symlinks resolved); exactly the bound conversation thread loaded; the TUI PID and start time alive under the owned Herdr server; and the pane terminal id and agent kind unchanged. A failure releases the claim with nothing sent. Launch verifies the same facts before recording the binding. | `CodexBindingTests.Any_stale_or_unproven_element_fails_closed` (10 cases), `SocketOwnershipTests.*`, `CodexHelperThreadTests.*`. Live: the first launch **correctly failed closed** because Codex 0.157.1 always symlinks the socket to `/tmp/codex-daemon-<uid>/<hash>`. The check now resolves the link, backed by a regression test. |
| 5 | Human-wins pause not implemented | **Fixed.** A turn not bound to our attempts or turn ids persists `ForeignPause` on the agent and blocks every claim. `--allow-busy` is **removed**. `reconcile-idle` lifts the pause only after the full binding check passes and an idle, readable history is confirmed; it records the foreign turn ids as acknowledged. Unclassified turns (user message not yet listed) count as busy, never foreign. | `DispatchClaimTests.Foreign_pause_blocks_claims_until_explicitly_cleared`, `ForeignTurnRaceTests.*`. Live Codex: after a Herdr-typed foreign turn *completed*, `send` → `refused_foreign_turn` plus a persisted pause; `reconcile-idle` acknowledged that turn; the next `send` completed (`codex-foreign.jsonl`). The live run also exposed a false pause on our own just-started turn, now fixed and tested. The Claude pause uses the same rules, but an Esc-interrupted Claude turn keeps the session non-idle (no Stop hook), so `reconcile-idle` refuses; see Open. |
| 6 | History errors collapse to empty evidence | **Fixed.** `CodexHistory.Classify` returns `ProvenEmpty` only for the two observed pre-first-turn errors on a never-used (`HistoryEstablished=false`), idle, preview-less thread. Everything else is `Unavailable`, which refuses a send (`refused_history_unavailable`), never resolves a wait, and never counts as idle. Unbounded history (1000+ turns) is also `Unavailable`. | `CodexHistoryTests.*`, including an error on an established thread, a thread with a preview, an active thread, an unknown code, and a timeout. |
| 7 | Unrestricted diagnostic RPC | **Fixed.** `codex-rpc` accepts only `thread/read`, `thread/turns/list`, `thread/loaded/list` and `model/list`. The `codex-poll` diagnostic was removed. | `CodexBindingTests.Diagnostic_rpc_is_limited_to_read_only_methods` (8 cases). |
| 8 | Hook, result and IPC bounds; no durable outage replay | **Fixed where feasible; limitation stated.** JSON-RPC now has a 30 s request deadline (`JsonRpcTimeoutException` = may have been delivered), a bounded DropOldest inbound queue with a drop counter, a 1 MiB outgoing cap checked before any write, and at most 64 pending requests. Other bounds: 10 s connect timeout, 64 KiB instruction cap, 16 MiB WebSocket inbound cap (unchanged). The hook reads at most 1 MiB, the event log is capped at 16 MiB, and each record is fsynced. Any failure writes `<log>.failed` and exits non-zero. The harness then refuses sends and turns an unresolved wait into `NeedsReconciliation`. **Not claimed:** zero result loss. If even the marker cannot be written (for example a full disk), only the TUI shows the hook error. | `JsonRpcPeerTests.Stalled_peer_hits_the_request_deadline_as_possibly_delivered`, `Flooding_peer_cannot_grow_the_inbound_queue_beyond_its_bound`, `Oversized_outgoing_request_is_rejected_before_any_byte_is_written`, `ClaudeHookRunnerTests.*` (write failure to a directory path, oversized input, full log). Live: hook runner OK, no failure marker (`evidence/fixes-live/claude-smoke.jsonl`). |

### Non-blocking observations

- **State durability and privacy — fixed.** `.run/` and `.run/spike2/` are 0700. State,
  the lock file, Claude settings and hook logs are 0600. State is fsynced, and so is its
  directory (libc `fsync` via `LibraryImport`). Guarantee: survives process kills;
  power-loss behaviour is intended but untested. This is not the product's SQLite design.
  Tests: `StateStorePermissionTests`; live check shows `700 .run/spike2`, `600` files.
- **Environment — fixed as an allowlist.** `LaunchEnvironment.Apply` is an allowlist:
  locale, XDG, display, `CODEX_HOME`, `CLAUDE_CONFIG_DIR` and `MISE_*`. Anything else,
  including API keys, needs `ATF_SPIKE_ENV_ALLOW`; agent-session and Herdr context can
  never be forwarded. Tests: `LaunchEnvironmentTests.*`. Live: the owned server's
  environment names contain only allowlisted names
  (`evidence/fixes-live/herdr-server-env-names.txt`). The hook still stores cwd,
  transcript path and up to 4000 characters of result; not publication-safe.
- **Cancel scope — tightened.** Codex cancel requires the full binding and the bound turn
  id. Live: `Interrupted` (`evidence/fixes-live/codex-cancel.jsonl`), but child cleanup
  for a running tool is still untested. Claude `cancel` now reports `cancel_unsupported`
  with exit code 8. `--send-esc-unverified` only types Esc and still exits 8; nothing is
  marked interrupted.
- **Approvals.** Codex server requests are still unanswered and not persisted as a job
  state; live Codex approval remains unverified.
- **Launch intent.** The agent record (phase `launching`) is persisted before any tab
  exists. Live evidence: the fail-closed launch above left agent `cx1` in `launching` and
  was not reused. Automatic cleanup or adoption of such orphans is not implemented.

### New findings from the repair's live run (Codex 0.157.1)

- `--listen unix://PATH` creates PATH as a symlink to `/tmp/codex-daemon-<uid>/<hash>`.
  Socket ownership must resolve the link.
- After the first turn, the per-agent app-server also loads an ephemeral
  `threadSource: "thread_title"` helper thread. `CodexThreads.IsInternalHelper` excludes
  only ephemeral, non-user threads; anything unreadable counts as a conversation.
- A just-started turn can be listed before its user message and `clientId` exist. It is
  now "unclassified" (busy), and turn ids returned by our `turn/start` are ours.

## Open / blocked

- **Claude targeted native cancel: blocked** (no native API). This needs a product decision.
- **Claude origin proof: unsupported.** The strict text-and-time match cannot exclude a
  human typing identical text in the window. An interrupted Claude turn leaves the session
  permanently non-idle for `reconcile-idle`.
- **T05/T15 need a human.** Steps are in REPORT.md §7. The Herdr-typed prompt is not
  human verification.
- **Still unrun:**
  - Stale-binding scenarios other than the symlink case (only fake-fact tests).
  - Live teardown of the new owned session.
  - Daemon/power-loss durability.
  - Codex approvals.
  - AOT and published-binary smoke.
  - .NET 11 (not retargeted).

## Live resources

- **Created:** one owned Herdr session (random `atf-spike-…` name, recorded in
  `.run/spike2/state.json`). It holds Codex agents `cx1` (failed-closed launch) and `cx2`
  (working) plus Claude agent `cl1`. It is left running as the E2E demo;
  `scripts/atf-spike.sh teardown` removes only it.
- **Model turns:** about 10 tiny Codex turns and 1 Claude turn.
- **Preserved:** the earlier `atf-m0-spike` session and its schema-1 state were untouched.
  No other sessions or processes were affected.
