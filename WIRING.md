# Slice 4 integration wiring

This commit supplies the authority core and direct schema tests. It deliberately does not
change shared orchestration, migration registration, or advertise a capability. Those
hooks are required before this becomes active runtime protection.

- `DAL/Migrations/Schema.cs:Apply`: register `PRFactoryAuthorityMigration.Sql` as the next
  serialized migration in `Migrations` and update the schema version accordingly.
  Preserve existing team acceptance identity and claimed JSON.
- `Host/Hosting/DaemonCommand.cs` (PRFactory composition in daemon startup): create one
  long-lived `PRFactoryAuthority` per connector, not one per heartbeat tick. Pass the
  connector-bound existing `StopJob.Execute`, `StopAgent.Execute`, and
  `ExternalTeam.RevokeMember`. Dispose only after connector tasks stop. Pass an
  `executionStopped(jobId)` proof callback: return false until dispatcher active start/run
  has unwound and retained/quarantined ownership is proven stopped; unknown ownership
  must stay false. **Do not use terminal job status or StopJob success as this proof.**
- `Business/Jobs/DispatchJob.cs:CancelRunning`, `StopReconciled`, and `RunAttemptAsync`:
  expose that quiescence proof from owned run/session handles and existing process
  correlation checks. Retry termination for an owned still-live run even if its job row
  was already cancelled. A foreign/reused PID must never authorize a kill. A late backend
  start remains incomplete until existing late-start termination finishes.
- `PRFactoryWorkItems.cs:TickAsync`: call `RetryStopsAsync` before intake/advance, even
  when disconnected or credentials rejected, including at daemon startup. Do not restrict
  retry enumeration to `teams.Pending`: authority rows preserve unfinished stops after
  terminal server dispositions. Skip intake when `IntakeBlocked`.
- `PRFactoryClient.cs:GetAtfAcceptanceAsync` / `AcceptAtfAsync`: parse the server's explicit
  disposition and reason, validate work-item/lease/machine/ATF-job identity, then call
  `ObserveAsync` with `accepted`, `completed`, `cancelled`, `revoked`, or
  `reconciliation-needed`. Unknown dispositions must become reconciliation, never acceptance.
  A 404 after durable acceptance is not permission for legacy fallback.
- `PRFactoryWorkItems.cs:ConfirmAcceptanceAsync`, `Fence`, `AdvanceAsync`: use the above
  observations; replace publication-only Fence with `ObserveAsync(...,
  "reconciliation-needed", reason)`. Forward network failures to `TransportFailureAsync`;
  transient failures retain disposition/identity but require fresh server confirmation.
- `PRFactoryHeartbeat.cs:RunAsync` token rejection catch: call `TransportFailureAsync`
  with 401/403 before parking connector, and retain a local retry loop. Never delete
  acceptance state. The item ID may be Guid.Empty for a connection-wide rejection.
- `PRFactoryWorkItems.cs:AdvanceCoreAsync`, `AdvanceExternalAsync`, `FinishAsync` and
  `PRFactoryDelivery.cs:AdvanceCommandsAsync`, `UploadManagedAsync`: wrap each individual
  submit/follow-up (+ durable member mapping), invite/send, upload, publication, and
  completion side effect in `RunAsync`. A false result means blocked, not delivered.
  Do not wrap a whole workflow and assume later effects remain authorized. After server
  completion acknowledgement observe `completed` to close retained sessions too.
- `Business/Jobs/FollowUpJob.cs:Execute`, `DispatchJob.cs:RunAttemptAsync` and generic
  `JobsMcpBridge` / `JobsEndpoint` follow-up entry points: resolve PRFactory ownership via
  `PRFactoryAuthorityStore.OwnedTurns` (includes recursive descendants) and use the same
  authority admission gate. Check before actual backend launch as well as acceptance;
  queued work must not evade a fence. Keep authorization bound to connector principal.

`RunAsync` orders **starting** one effect against fencing. Its callback must start that
single effect before its first asynchronous yield; preparation belongs outside it.
An in-flight push/upload may finish after cancellation: retain its exact receipt and
recheck authority in a separate RunAsync before completion. Never claim a push was undone.
Never recursively call authority methods from the synchronous part of a RunAsync callback.
Keep asynchronous acceptance and its durable member mapping inside the returned task:
the gate tracks admitted tasks, keeps stopping pending until they settle, and rescans
owned turns afterwards (including when a task fails). On restart, recover any accepted
but unmapped submissions before retrying stops or enabling the dispatcher; in-memory
task tracking cannot recover an orphan mapping by itself. Connector-owned queued jobs
must remain blocked until their team mapping and current authority are known.

New instances have no confirmed items, so restart cannot dispatch/publish until server
confirmation. Fenced dispositions are sticky: ordinary `accepted` cannot reopen them.
401/403 blocks intake for that instance. Explicit operator/server reconciliation and
credential recovery must be a deliberate integration flow, not automatic resurrection;
this slice does not delete local work or implement a reopen API.

Tests apply the unregistered migration directly. Real-server cancellation, server-side
rejection of stale uploads, and opposite-family review remain integration/review gates;
this branch makes no claim of deployed end-to-end proof.

Validation (2026-09-27): `DOTNET_PROCESSOR_COUNT=2 scripts/verify.sh` passed:
format check, Release build (zero warnings/errors), 749 tests passed / 7 skipped,
Native AOT publish, and 41/41 published scenarios passed. Evidence:
`evidence/published-20260927T135430Z-wrlszm/published-manifest.txt` (local generated
artifact). The focused tests include fencing before stop, quiescence retry, retained
sessions, deferred descendants, foreign PID protection, external revocation, restart,
in-flight publication and a submit whose member mapping arrives after cancellation.
