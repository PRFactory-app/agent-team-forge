# Slice 3 core: human waits

This slice supplies durable wait/reservation storage, job-scoped request semantics,
connector answer advancement, and native input receipt matching. It deliberately
is not enabled in the daemon: the shared integration files remain unchanged.
`HumanWaitMigration.Sql` is unregistered. No live CLI or PRFactory UI end-to-end
qualification is claimed by these unit tests.

## Sequential integration

1. Register `HumanWaitMigration.Sql` after the existing PRFactory team tables,
   using the next migration number. Compose `HumanWaitStore`, `HumanWait` with
   the authenticated principal, and `PRFactoryInteraction` with the scoped
   `FollowUpJob.Execute` delegate and existing `ExternalTeam`.
2. In JobsMcpBridge/JobsEndpoint expose `request_human_input(question,
   idempotencyKey)` only to the authenticated current managed job. Resolve the
   server/work-item/member/turn from durable membership, never caller-supplied
   authority. Call `HumanWait.Request`; persist first, then stream question ID,
   question, originating turn and `WaitingForHuman`. Return `EndTurnInstruction`.
   Include that instruction in agent prompts: end the turn; no stdin waits,
   native approval UI, synthetic Enter, or busy-session interruption.
3. DispatchJob retains generic Completed semantics. Only native completion
   evidence may complete the originating run. `Refresh` requires a completed
   job/run, captured unfenced session and no started run before enabling resume.
   Uncertain recovery is not completion. A diagnostic timeout may report a
   blocked turn; it must not manufacture completion or kill a busy tool.
4. In PRFactoryDelivery route correlated answers to `Answer` with the exact
   server/work-item/member/question/command UUID and immutable answer text.
   Ordinary uncorrelated instructions, held lead messages and phase Q&A remain
   on their existing paths. ACK means durable reservation, not consumption.
   Report reservation errors (including iteration limit) explicitly.
5. Poll active teams about every two seconds, independently of heartbeat, with
   outage backoff. On startup and each tick enumerate `ForTeam`, call `Refresh`
   and `Advance` for pending waits, and scan resumed input receipts. Stream
   queued/waiting/resumed/applied/failed state and question closure. Retain
   rows and relevant jobs/transcripts until server acknowledgement; prune must
   not delete pending waits or their parent/resumed jobs.
6. Hold the same per-team authority/admission gate through refresh, follow-up
   acceptance and `RecordResumed`, and through cancellation and every other
   turn admission. Recheck slice 4 server disposition before side effects;
   database claimed/accepted checks alone cannot prove remote authority.
   Fence new dispatch before cancellation. Do not close a member or mark a team
   successful while `BlocksCompletion` is true. Failed/cancelled waits block
   success too; surface their error and finish via the failure/cancellation path.

## FollowUpJob and iteration admission

`Advance` uses the originating job, exact `AnswerPrompt`, and fixed
`prf-human:<command UUID without hyphens>` key, with no interrupt/defer flags.
Never generate a new retry key after response loss. FollowUpJob's existing
accepted-key lookup must remain ahead of mutable readiness checks. Its existing
same-session/backend/cwd/worktree reuse is essential; integrate slice 5's full
workspace/session options preservation there. Never replace a missing saved
session with a fresh one. Retained Herdr send is allowed only after proven
completion, using the existing backend mechanism, never arbitrary keystrokes.

Acceptance and `RecordResumed` are separate durable transactions. Retrying
Advance recovers the accepted child by key, then atomically adds its member turn
and records its job ID. Do not independently insert that member mapping in the
follow-up delegate. An early answer remains reserved until completion is proven.

Pass recipe MaxIterations to Answer. Capacity counts actual started/completed
model turns plus queued jobs and reserved managed answers as admission holds.
Every other initial/dynamic/follow-up/finalization admission must check
`CanAdmitTurn` under the same gate and retain that gate through durable member
mapping. A reserved human answer already owns its slot: do not apply a second
budget check that charges it again. External mailbox delivery is not a model
turn. Tests cover the initial turn, answer reservation, retry and subsequent cap.

## Consumption receipts and backend capabilities

After durable resume, scan only the verified resumed native session transcript;
pass its session ID, resumed job ID and native record to `ConfirmManagedInput`.
Use native user input records: Claude `user/message`, Codex
`response_item/payload`, Pi `message/message`. Full answer prompt equality is
required. Stdout, enqueue ACKs, assistant echoes and tool-result echoes are not
receipts. Applied means input observed, not successful task completion. A scanner
must read actual transcript records, not agent-supplied JSON or a stream echo.

Cursor and Droid are explicitly rejected by `SupportsBackend` and Advance until
session capture/resume AND native input receipt proofs are added. Their existing
CLI resume flags alone do not qualify them. Advertise `human-wait-v1` only for
backend/launch-mode combinations with integrated authenticated tooling, scanner,
and real published-binary session proofs; unit fixtures are not CLI qualification.

## External members and server wire

The authenticated external endpoint resolves its exact live membership and calls
`HumanWaitStore.Request` with null job ID and durable member turn. Answers use
existing `SendToMemberOnce` with command UUID and the existing authenticated
mailbox. Repeated Advance calls enqueue once. Only after an authenticated read
returns the matching answer message (or explicit acknowledgement of that exact
command) call `ConfirmExternalInput`. Bind all supplied scope to that credential;
this method is an integration hook, not an unauthenticated endpoint. Never call
it on enqueue ACK, unrelated reads or truncated results lacking the answer.
Native wake remains notice-only; never stop an external member's process.

Server integration must add question ID, waiting reason and delivery state to
existing command/stream DTOs and UI, preserve existing ownership and authorization,
and require negotiated `human-wait-v1` at claim. Do not raise WorkerVersion or
claim old servers support this protocol. Preserve phase approval/rejection flows.

## Validation

Focused tests cover end-turn waiting, durable restart, early answer, lost acceptance
reply, command race/dedupe, cancellation/fencing, deadline, scope, iteration cap,
completion blocking, mailbox dedupe/read and Claude/Codex/Pi receipt shapes.
The previously reported mailbox read failure did not reproduce on this checkout;
read error and inbox type assertions now identify that boundary explicitly.
Full gate is `DOTNET_PROCESSOR_COUNT=2 scripts/verify.sh`, including format, build,
tests, Native AOT publish and isolated published-binary smoke. Real server UI,
daemon-restart and installed backend session proofs remain integration work.
