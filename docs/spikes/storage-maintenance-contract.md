# W6 — Minimal storage maintenance contract

## Status and pinned inputs

Proposal for independent plan review, not runtime authorization or P02-W07 completion.
Parent may release reserved Claude W6 after decisions below; no whole-epic replan.
Read with [P02-W07](../planning/full-product/02-durable-core.md),
[allocation](parallel-implementation-wave.md) and [architecture](../architecture.md).
Reviewed source input: `81a11b27fb67d2ec600ace67586c092829927f8f` (S).
Read-only integration inspection: `043df59e68220b3466617e1eafd659f0f0b7713c`.
Its newer review labels do not replace S or establish a new accepted DB schema.
At both snapshots, `spikes/m0-durable-core/src/AgentTeamForge.DAL/{Sqlite,Migrations}/`
and the SQLite package pin are unchanged; newer JobStore inspection/acceptance fixes
are separate inputs, not permission to import the moving integration branch.

## Existing schema versus proposed additions

S has schema **1**, not the full P02 logical-record inventory: `schema_migrations`
contains only `version, applied_at`; jobs embed scoped keys/fingerprints/results;
`dispatch_intents`, `runs`, and `events` complete the five-table schema.
`Schema.Apply` checks only maximum version; it does not validate history/checksums.
`JobDatabase.Open` requires an existing file; connections set foreign keys/FULL/busy
handling. WAL is selected on creation, not verified on every existing-schema open.
Each JobStore write uses `BeginTransaction(deferred: false)`; startup quarantine
covers started runs only. Queued restored work would currently dispatch. No durable
maintenance flag, conversation/resource claims, authority registry or restore gate exists.
`AdmissionGate` drains submissions only; it is not a storage-maintenance interlock.
Existing newer-schema test checks main-file bytes only, not WAL/SHM or crash recovery.

Propose one additive migration **M**, numeric ID allocated by the shared schema owner.
Add `storage_state(singleton INTEGER PRIMARY KEY CHECK(singleton=1),
restore_quarantined INTEGER NOT NULL CHECK(restore_quarantined IN (0,1)))`, exactly
one row, initially `0`; missing/duplicate/invalid state refuses startup.
Add nullable `checksum TEXT` to `schema_migrations`; validate legacy v1 against frozen
DDL/PRAGMA structure before backfilling its pinned digest. Digest identifies exact
migration content, not just a version number; no fake historic applied checksum claim.
M sets its own checksum and version row in the same transaction as these additions.
No changes to job/intent/run/event semantics or authority fields belong to M.
Current accepted schema remains v1 until M is reviewed, implemented and accepted.
Supported upgrade: exact v1 → M only, extended solely by explicitly reviewed steps.
W7's concurrent scoped-acceptance contract writer owns proposed authority semantics.
Parent allocates ordered IDs and one migrations/JobDatabase owner; W7 hands that owner
reviewed SQL, invariants and fixture requirements, never edits the same stream in parallel.
If W7 lands first, pin its accepted schema and explicit upgrade path before W6 coding;
never assume next ID, silently skip W7 tables, or infer compatibility from max(version).

## Small API and interlock

Use root `AgentTeamForge.slnx` and the separately frozen canonical feature map.
Host `Features/Maintenance` maps an explicit offline maintenance-daemon role to
Business `Features/Maintenance`; Business calls concrete DAL feature operations.
DAL owns SQL/backup under `Features/Maintenance`, with shared Sqlite/Migrations edits
leased to one owner. No DAL ports, fourth project, service or generic migrator.
Proposed Business calls: `InspectStorage()`, `ExportBackup(destination)`,
`UpgradeStorage()`, `RestoreIsolated(source, destination)`; return schema, outcome
and bounded reason codes, never credentials, payload rows or raw exception paths.
No public MCP operation or new remote authority: operator invokes the explicit local
maintenance-daemon role, using existing owner-checked state and singleton authority.
This role is still daemon-owned DB access, not permission for CLI/bridge DB access.
Acquire the same singleton lock as normal daemon before opening storage; never stop
or steal from a live daemon. No IPC listener, backend, dispatcher or readiness signal.
Offline-only scope avoids an unproved online drain: active daemon means refusal.
Source upgrade/export additionally refuses running jobs, started runs, reconciliation
states, inconsistent job/intent/run combinations or unresolved surviving ownership.
Queued, genuinely unattempted work may remain; terminal rows alone do not prove idle.
S has no physical-idle proof contract: initially any run row blocks source upgrade/export.
Only a separately accepted recovery-owner proof may relax this; otherwise return
`maintenance_reconciliation_required`, never infer idle from PID absence or kill agents.
Normal startup must validate schema and durable restore gate before recovery, bind or
ready; quarantined storage can be inspected offline but cannot accept or dispatch.
Lock release never automatically clears quarantine; no clear/resume API in this slice.

## DAL operations and exact failure boundary

`InspectCompatibility` opens existing storage read-only, no create, no journal change;
validate complete ordered history, checksums/known v1 shape, required columns/indexes/
constraints, foreign keys and WAL mode before write access. Unknown objects/history,
newer schema, unsupported old schema and corruption refuse; no empty-state fallback.
Preserve DB/WAL/SHM on refusal. Read-only WAL opening must not create sidecars; if the
pinned provider cannot inspect safely, refuse unavailable. Never use immutable mode
on potentially live WAL data. Test sidecar behavior; do not assume Open is nonmutating.
After inspection, reopen writable under the continuously held lock, revalidate version
and structure in `BEGIN IMMEDIATE`, then enforce operation-specific preconditions.
`UpgradeKnownSchema` takes and verifies a backup first; one immediate transaction
rechecks v1, applies M, backfills digest, inserts history and validates target state,
then commits. Version, gate and DDL are atomic; no file marker stands in for DB version.
On commit error, report `storage_outcome_unknown`, keep runtime closed, reopen only
for inspection: accept exact old or exact new state, never retry blindly or decrement.
Readiness requires successful final compatibility/integrity/gate checks, not commit alone.

`CreateVerifiedBackup` uses pinned `Microsoft.Data.Sqlite.SqliteConnection.BackupDatabase`
from existing source to a fresh private destination connection (SQLite online-backup
API), not File.Copy of main DB or manual WAL checkpoint/copy. No source write transaction
spans backup; singleton/excluded writers provide stable scope. Close destination, reopen
read-only, require `integrity_check = ok`, zero `foreign_key_check` rows, compatible
history and preserved logical IDs/counts. Publish only after verification and closure.
Backup/export here means a sensitive SQLite snapshot, not sanitized diagnostics:
it contains instructions/results and any later authority data. Keep it owner-private;
no credentials/profile, logs, workspaces, artifacts or backend sessions are bundled.
Prove backup API and native asset loading with the pinned package
`Microsoft.Data.Sqlite 11.0.0-rc.1.26425.128`; no dependency/SDK installation implied.

`RestoreQuarantined` accepts only a verified compatible backup, copies via the same
SQLite API to fresh isolated staging, upgrades there if needed, then commits the
restore gate to `1` in one immediate transaction. Preserve IDs, attempts, queued
intents and events: do not manufacture completion or rewrite attempts as unattempted.
Global gate quarantines even queued snapshot work and retains all future claim rows;
backup may precede external effects. Existing startup quarantine alone is insufficient.
Verify again, close connections, publish isolated destination; never overwrite active
state, copy authentication/bootstrap credentials or automatically register a new runtime.
Destination is inspect-only; reconciliation/activation requires a later reviewed contract.
Restoration cannot undo effects or recover work recorded after backup.

## File ownership and errors

Business validates source owner/private permissions and regular-file identity; reject
symlinks/reparse points, alias/hardlink to source, existing destination and unsafe parent.
Use exclusive staging under an owner-private local parent, not shared/network storage;
retain verified handles/identity through use and recheck publication without overwrite.
Publish a closed single-file snapshot by same-filesystem no-replace rename; no manifest
is required for safety. Crash-left staging is never a runtime root or automatic input.
Cleanup may unlink only exact artifacts created by this operation with verified identity;
never recursively delete directories, source DB/WAL/SHM, user state or recovery evidence.
Uncertain identity or cleanup failure retains staging and reports a bounded blocker.
Proposed stable codes: `maintenance_busy`, `maintenance_reconciliation_required`,
`storage_schema_too_new`, `storage_schema_unsupported`, `storage_schema_mismatch`,
`storage_corrupt`, `storage_busy`, `storage_unavailable`, `storage_outcome_unknown`,
`storage_path_unsafe`, `storage_destination_exists`, `storage_restore_quarantined`.
Busy is retryable only before effects or after confirmed rollback; do not inherit a
blanket “nothing committed” claim from current StorageException enum documentation.

## Bounded work packages, test oracles and review decisions

| Package / ownership | Deliverable and decisive oracle |
| --- | --- |
| F: independent preparation, test-only W6 files | Frozen v1 fixture built from S SQL, not current Schema.Create: queued, started, uncertain and terminal jobs, keys, results, event sequence; separate eligible-idle variant. Readback proves exact IDs/relationships; no proposed authority data invented. C# scratch backup/provider probe and crash-barrier utility can proceed on assigned files without implementing new runtime contracts. |
| A: reviewed refusal + upgrade vertical slice | Host offline role → Business preconditions → DAL inspect/M. Race normal startup against maintenance: one lock winner, loser touches no endpoint/DB. Upgrade preserves every legacy value; invalid/gapped/altered/newer schema leaves DB/WAL/SHM unchanged. Owner hands off shared SQL/composition edits before parallel W7 work. |
| B: reviewed backup/export vertical slice | Same role → Business path/idle checks → DAL backup. WAL-resident committed row appears; uncommitted row does not. Integrity/FK/history pass; existing destination/symlink/alias never overwritten. No sensitive payload in status/errors. |
| C: reviewed isolated restore vertical slice | Same role → Business isolation → DAL gate. Restart restored queued/started/terminal fixture: no ready/admission/dispatch/backend receipt, claims retained; original source unchanged. Crash before gate/publication leaves only unusable staging; after publication gate remains durable. |

Inject failure **after each numbered barrier**: (1) lock/path validation, (2) read-only
inspection, (3) staging creation, (4) backup completion, (5) backup verification,
(6) upgrade transaction begin, (7) each DDL/data/history statement, (8) commit before
reply, (9) restore-gate commit, (10) final verification/close, (11) publication.
Use deterministic C# barriers plus abrupt process death, not exceptions alone. Oracles:
old-or-new complete schema, no false success, no ready/effect, preserved source and
owned-only leftovers. Exercise busy, corrupt/truncated backup, read-only/full destination,
ambiguous commit and restart; distinguish injected full-disk errors from actual disk-full.
Shared edits also require leases for `Sqlite/StorageException.cs` and Host
`Hosting/DaemonCommand.cs`; F owns only new `tests/AgentTeamForge.Tests/Features/Maintenance/`
fixtures and `Scenarios/StorageMaintenanceScenarios.cs`, no shared build/script changes.
Run format/check, Release build/tests and published .NET 11 AOT backup/restore paths;
record native SQLite version/assets. Blocking storage calls have no strict time bound;
cancellation does not prove rollback. Process kills do not prove power-loss durability.

Review decisions required **before runtime A–C**: approve offline authority/path checks,
M/gate/checksum semantics, active-work refusal, restore nonactivation and P02-W06 ordering
exception; allocate M and exact shared-file handoff; pin accepted canonical runtime commit.
Independent plan review is outstanding. Claude implementation then needs separate Codex
review, fixes/re-review and combined gates; integration is not approval. No Windows/macOS
execution or AOT compatibility is claimed. Non-goals: online drain, compaction/retention,
full W07 diagnostics/capacity work, generic migration framework, restore activation,
credential migration, installers, live backends, source relocation or broad cleanup.
