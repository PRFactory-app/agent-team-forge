# W7 — Scoped acceptance: minimum review contract

**Proposed freeze; independent major-plan review required before runtime changes.**
This unlocks reserved W7, not a new epic, P02 completion, or native-platform support.
Inputs: [P02-W02 and section 3](../planning/full-product/02-durable-core.md),
[cross-phase contracts](../planning/full-product/contracts.md), [architecture](../architecture.md),
[allocation](parallel-implementation-wave.md), and [contributor policy](../../AGENTS.md).

## 1. Source boundary and increment

Read-only source inspected through `.worktrees/m0-integration`; baseline is
`81a11b27fb67d2ec600ace67586c092829927f8f`, under `spikes/m0-durable-core/`.
It has configured `BoundPrincipal`, one operator credential, `job_submit`/`job_get`,
v1 fingerprint in `AcceptJob`, scoped job-key uniqueness, and schema version 1.
It does **not** have registered authority, credential generations, or retry epochs.
Latest inspected HEAD `043df59e68220b3466617e1eafd659f0f0b7713c` adds inspection/paging,
private-file hardening, and acceptance-outcome hardening. These are later changes,
not baseline evidence. Preserve their fixes when parent selects the canonical base;
particularly commit-outcome uncertainty and Business admission-fault ownership.
Canonical source/tooling are moving separately; this document changes neither.

Deliver two explicit registered teams, stable principals and logical fake-agent /
conversation targets, operator-issued one-hop delegation, scoped submit/get/list,
and safe retries/revocation. Keep existing fake execution only. No identity provider,
general RBAC, nested teams, real P03 bootstrap delivery, interrupt/stop implementation,
or new scheduler. Registration is metadata, never permission to launch a backend.
Exactly Host → Business → DAL; only daemon opens SQLite, including authority data.

## 2. Decisions needed versus proposed defaults

| Category | Decision / proposed contract | Approval owner |
| --- | --- | --- |
| Required decision | Authorize this bounded P02-W02 experiment and dependency exception; pin canonical commit, exact files and separate reviewer. | Parent |
| Required decision | Accept same-user capability threat boundary and explicit private-file credential provisioning below; no identity inferred from environment, PID, display name, or team payload. | Parent/security review |
| Required decision | Revocation denies future API access, including retries; already committed work continues within immutable accepted policy. Revocation is **not** job cancellation. | Parent/security review |
| Required decision | W6 supplies reviewed additive authority migration and shared records first; storage owner alone allocates migration ID/checksum and edits shared schema. | Parent + W6 |
| Proposed default | One active delegation per principal, to one team and one target pair; operator alone registers/revokes; no redelegation or helper authority. | Plan reviewer + parent |
| Proposed default | Keep keys/outcomes indefinitely within finite admission caps; no pruning, epoch retirement or TTL expiry in this increment. Explicit exhaustion rather than unsafe reuse. | Plan reviewer + parent |
| Proposed default | Preserve 64 KiB frame, 4,000 UTF-16-code-unit instruction, 128-character key, global 8 outstanding jobs, 60 s fake runtime; per-team outstanding cap 4. | Plan reviewer + parent |
| Proposed default | Cap durable operations at 10,000 globally / 1,000 per principal; teams 32, principals 128, targets 128, outstanding grants 32. All count checks atomic. | Plan reviewer + parent |

These numbers are checkpoint limits, not replacements for the full-product profile.
Reject invalid UTF-16 and empty instructions; otherwise preserve instruction bytes,
newlines and Unicode. No silent normalization, headless default or policy widening.

## 3. Binding, requests and permissions

Keep owner-private local transport and peer checks; peer UID alone gives no team role.
Propose IPC v2, rejecting v1 before effects once scoped mode is enabled. `job_submit`
remains an experimental name, not the final first-job/follow-up public API freeze.
Operator credential from explicit private configuration binds one durable operator;
configuration principal must match its persisted binding or startup fails closed.
No auto-operator enrollment from a request. Never serialize DAL rows to clients.

| Request (typed payload) | Required fields → response | Authority / constraints |
| --- | --- | --- |
| `authority_register` / `RegisterScopeRequest` | key, epoch, teamId, principalId, agentId, conversationId, launchMode → stable `ScopeView`, operationId | Operator only; caller-chosen opaque IDs validated, immutable and never recycled; explicit `headless` fake mode only. Creates scope/target and delegation, initially without credential. |
| `authority_grant` / `IssueGrantRequest` | key, epoch, principalId, nonceHash, credentialHash → grantId, daemonEpoch, expiresAt | Operator only; hashes of distinct client-generated 32-byte CSPRNG secrets; binds intended principal and current credential generation. No raw secret response. |
| `authority_exchange` / `ExchangeGrantRequest` | grantId, nonce, credential → `BindingView` | Restricted pre-auth exchange on peer-checked IPC; verifies both secrets, consumes grant and activates credential atomically. No job authority before success. |
| `hello` / `AuthenticateRequest` | credentialId, credential → binding | Credential lookup determines principal, team, target, credentialGeneration, delegationGeneration and daemonEpoch; request identity cannot override them. |
| `authority_revoke` / `RevokePrincipalRequest` | key, epoch, principalId → operationId, generations | Operator only; atomically disables delegation/credentials, increments generations and invalidates grants; no reactivation API yet. |
| `job_submit` / `ScopedSubmitRequest` | key, epoch, fingerprintVersion, policyVersion, teamId, agentId, conversationId, instruction, explicit launchMode, fake options → `ScopedJobResult` | Bound operator or delegated principal; every target must match registration; fixed delegation permits submit/read only. |
| `job_get`, `job_list` | jobId OR teamId + bounded cursor → existing job views/page | Operator within explicit registered team; delegate only its own principal's jobs in its bound team. Cross-scope lookup returns `not_found`; list never reveals other rows. |

Client provisioning generates and keeps secrets in explicitly selected private files;
operator passes only hashes through registration IPC and explicitly hands files to
intended client. No secrets in argv, environment, logs, fixtures or ordinary responses.
Reject symlinks/non-private files using reviewed private-file primitives; do not invent
Windows ACL evidence. Provider delivery into real managed agents remains P03 work.
Grant TTL: 60 s, single-use, bound to persisted random daemon epoch rotated under
singleton lock before listening. Restart rejects outstanding grants, not valid durable
credentials or retry epochs. Lost exchange reply: try credential authentication, never
reuse nonce; if not active, operator issues a new grant. Existing active credential
cannot be replaced by exchange; rotation is deferred, revoke/new principal is explicit.
Same-user unrestricted file/process access defeats capabilities: this is not a sandbox.

## 4. Fingerprint, retries and atomic storage

`RequestAuthority` is Business-owned, created from verified binding, not deserialized.
Business validates and computes semantics; DAL rechecks active binding generations
inside the same transaction as lookup/admission. Revocation racing acceptance has a
single commit order. Reads similarly check authority within their read transaction;
already-authorized responses cannot be recalled after revocation commits.

| Storage operation / records (proposed DAL types) | Atomic contract |
| --- | --- |
| `AuthorityStore.RegisterScope(RegisterScopeData)` | Check operator generation, mutation-key duplicate and caps; insert principal/team/target/delegation + operation outcome together. Existing identical IDs return stable registration; changed relationships conflict. |
| `AuthorityStore.IssueGrant(GrantData)` / `ConsumeGrant(GrantProof)` | Verify authority/epoch/expiry/caps; store only secret hashes. Consumption CAS, credential insertion and grant consumption commit together. Concurrent exchanges have one winner. |
| `AuthorityStore.RevokePrincipal(RevokeData)` | Check operator, duplicate first; disable authority and increment generations with retained operation outcome in one transaction. Duplicate does not increment again. |
| `JobStore.AcceptScopedOrGet(ScopedNewJob, AuthorityFence, AdmissionLimits)` | Recheck current authority, validate epoch; lookup key before capacity check; compare fingerprint; else insert operation + job + unattempted intent + acceptance event, with quotas and accepted policy snapshot in one commit. |
| `AuthorityFence`, `ScopeRecord`, `CredentialRecord`, `OperationRecord` | DAL-owned feature records, no secrets or Business dependency. Operations unique on `(principal, team, operation, epoch, key)`; stable operationId/jobId and original outcome survive completion/restart. |

New principal gets retry epoch 1, persisted and advertised in binding; unknown/old
values return `epoch_rejected`, never create state. Epoch is distinct from daemon,
credential, delegation and run generations. No epoch advance API or tombstone deletion.
Future compaction needs reviewed minimum-epoch/tombstone migration; migration must not
turn retained legacy keys into fresh work. At cap, duplicates remain retrievable.

Fingerprint version 2: SHA-256 over a fixed ordered sequence of UTF-8 fields, each
prefixed by unsigned 32-bit big-endian byte length. Order: operation, teamId, agentId,
conversationId, exact instruction, backend (`fake`), explicit launchMode, policyVersion,
permission (`submit_read_own`), maxRuntimeMs, maxResultChars, behavior, hold (`0`/`1`).
Use invariant decimal integers. Registry fixes workspace to absent; reject supplied
workspace/backend/permission overrides and unknown semantic fields. Version is stored
alongside hash; request key/epoch, connection IDs and display names are not hashed.
Policy v1 resolves behavior=`complete`, hold=false, runtime=60000, result chars=16000;
keep this resolver immutable for retries. Compare using requested/stored version, never
current defaults. Unknown fingerprint/policy version fails before admission. Authority
mutations use the same encoding with operation-specific ordered request fields above;
exclude key/epoch, include supplied hashes; retain golden byte/hash vectors per operation.

| Error | Client action / guarantee |
| --- | --- |
| `unauthenticated`, `authority_revoked` | Rebind through operator; no automatic retry or identity fallback. Invalid credential gives generic unauthenticated; revocation applies to previously bound requests. |
| `forbidden`, `not_found`, `invalid_request`, `unsupported_version` | Fix request/authority; no effects or cross-scope existence disclosure. |
| `grant_invalid` | Covers expired, consumed, wrong epoch or wrong secret; no metadata oracle; authenticate after lost exchange reply. |
| `idempotency_conflict`, `epoch_rejected`, `unsupported_fingerprint`, `unsupported_policy` | Never choose a fresh key automatically; no new work. |
| `queue_full`, `authority_capacity`, `operation_capacity` | No admission; retry same key after explicit capacity remedy, never prune old keys automatically. |
| `storage_busy` | Bounded retry, same key/epoch/semantic payload only. |
| `outcome_unknown`, `storage_unavailable`, `daemon_unhealthy` | No acceptance claim; retain key and inspect/retry after recovery. Ambiguous commit closes admission/dispatch through existing Business fault path. |

## 5. W6 handoff, fixtures and exclusive coding lanes

**W6 handoff:** [storage-maintenance contract](storage-maintenance-contract.md) read;
its maintenance M explicitly excludes authority fields. This proposal is a separate
additive authority step, ordered by parent after M (or an explicitly re-reviewed order),
not an active ID allocation or claimed writer acknowledgment. W6 owns `DAL/Migrations/`,
`DAL/Sqlite/JobDatabase.cs` and migration fixtures. Accept this schema delta before
coding: authority tables + operation journal + job policy/version references. Preserve
legacy job unique constraint: minimal increment uses only epoch 1, with no rollover.
Import existing jobs into retained operations with original IDs/hash marked legacy v1;
backfill authority only from explicitly confirmed configured binding, never guessed
from arbitrary historical rows. Mismatch refuses upgrade. Legacy retry adapter is
operator-only, resolving exact original semantics; legacy keys cannot be resubmitted
through v2 as new jobs (return `idempotency_conflict`). Do not recompute old hashes.
Fixture includes queued, attempted, terminal and uncertain jobs with events/results;
use eligible-idle variant for successful offline upgrade; active/uncertain variant must
refuse under W6 rules. Assert preserved IDs, options, hashes, evidence and retry identity.
W6 supplies backup,
interruption/refusal/restore-quarantine tests; W7 adds migrated authority/retry oracles.

Paths below are canonical `src/AgentTeamForge.*` and `tests/AgentTeamForge.Tests/`:

| Owner / release order | Exclusive files and runnable handoff |
| --- | --- |
| W6/shared-record owner, first | Migration files above; DAL `Features/Authority/AuthorityRecords.cs`, `Features/Jobs/JobRecords.cs` released as one reviewed immutable schema/type fixture snapshot. No concurrent schema writer; parent assigns exact migration ID outside this document. |
| Claude DAL+Business W7, next | DAL `Features/Authority/AuthorityStore.cs`, `Features/Jobs/JobStore.cs`; Business `Features/Authority/{AuthorityContracts,BindPrincipal,RegisterScope,DelegatePrincipal,RevokePrincipal}.cs`, `Features/Jobs/{AcceptJob,JobContracts,GetJob,ListJobs}.cs`; `Features/Authority/ScopedAcceptanceTests.cs`. Real SQLite methods, not placeholder stores. |
| Claude Host-wire W7, parallel after shared handoff | Host `Features/Authority/AuthorityEndpoint.cs`, `Transport/{IpcMessages,IpcServer,IpcClient}.cs`, `Features/Jobs/{JobsEndpoint,JobsMcpBridge,ClientCommand}.cs`; `Features/Authority/AuthorityWireTests.cs`, `Scenarios/ScopedAcceptanceScenarios.cs`. Consume frozen Business signatures; no SQL or temporary auth bypass. |
| Parent-assigned composition owner, serialized | Host `Hosting/DaemonCommand.cs`, setup/profile files, Business `SpikeProfile.cs`, shared test fixtures/project entries. Release exact diff after W6 composition changes; no parallel edits. |

Host `IpcJson` explicitly source-generates all request/response types in section 3,
`ScopeView`, `BindingView`, `ScopedJobResult` and bounded list envelope; typed nested
payloads, no reflection/object bags. Freeze Business method signatures and DAL record
fields in reviewed fixture handoff before Host starts; names here alone are not stubs.

## 6. Readiness and review gate

Red-first oracles: equal concurrent keys yield one job/intent/event; changed semantics
conflict; revoked binding cannot fetch duplicates; cross-team/forged target denies;
revocation/accept races follow commit order; concurrent nonce replay has one winner;
expiry/restart reject grants; credential survives restart; old epoch never admits;
quota races stay bounded and duplicates work at cap; changed defaults retain identity;
lost acceptance response resolves original operation; migration preserves legacy retry.
Published offline scenario: register two scopes → bind delegates → submit → disconnect
→ reconnect/get → revoke → denied retry; no native backend/model calls required.
Run format, warning-clean build, full tests and published AOT scenario on selected
canonical snapshot; review actual diff separately with Codex for Claude implementation.

**Blocked until:** parent approves decisions/profile and canonical base; W6 acknowledges
single-writer handoff and provides reviewed schema/records/fixtures; independent major
plan review approves binding/provisioning, permissions, revocation races, fingerprint
vectors/defaults, epoch/no-prune limits, migration/legacy retry and commit-error behavior.
Resolve blockers and record approval before either runtime lane starts. This document
launches no workers and grants no code, commit, main, push or live-call authorization.
