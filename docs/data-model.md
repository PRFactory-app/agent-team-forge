# Data model

The daemon keeps structured state in one SQLite database, `jobs.db`, in the
state directory. Only the daemon opens it. The schema is defined by the
forward-only migrations in
[`src/AgentTeamForge.DAL/Migrations/Schema.cs`](../src/AgentTeamForge.DAL/Migrations/Schema.cs)
(current version **15**). This page describes the resulting tables; the code is
authoritative.

## Connection and migrations

- Every connection sets `foreign_keys=ON`, `synchronous=FULL` and a bounded
  `busy_timeout`; the database runs in WAL mode (`DAL/Sqlite/JobDatabase.cs`).
- Normal opening never creates a database. `atf init`/`setup` creates it.
- On open, the stored version is read from `schema_migrations`. A newer
  version than the build knows is refused without touching the file. An older
  version is migrated to the current one in a single transaction.
- Every daemon start runs `PRAGMA quick_check` first (`JobDatabase.Verify`) and
  refuses a damaged database before backing it up or reporting ready.
- The daemon copies `jobs.db` to `backups/jobs-*.db` on its first start per
  boot and keeps the two newest copies (`Host/Hosting/StartupBackup.cs`). A copy
  that fails `quick_check` is discarded, so it never rotates out a good backup.
- `jobs.db` and its `-wal`/`-shm` files are owner-only (0600) like the other
  private state files; every role refuses them otherwise, naming the `chmod` fix.

## Jobs and runs

```text
jobs 1───1 dispatch_intents
jobs 1───* runs
jobs 1───* events
jobs *───1 jobs (parent_job_id: follow-up chain)
```

### `jobs`

One row per accepted submit or follow-up.

| Column | Meaning |
| --- | --- |
| `job_id` | `job_<uuid v7>` |
| `principal`, `team` | Identity bound by the daemon profile, not by the caller |
| `target_agent` | Caller-supplied agent name |
| `operation` | `job_submit` or `job_follow_up` |
| `idempotency_key`, `fingerprint` | Caller key and hash of the semantic payload. `UNIQUE(principal, team, operation, idempotency_key)`: same key and payload return the same job; a different payload is a conflict |
| `instruction`, `options` | Prompt and JSON options (model, effort, …) |
| `backend` | `claude`, `codex`, `pi` or `fake` |
| `cwd` | Working directory |
| `parent_job_id`, `session_id` | Follow-up parent and the backend's native session ID |
| `worktree_path`, `worktree_branch`, `worktree_base` | Per-job git worktree, if requested |
| `timeout_s`, `queue_deadline` | Run timeout and queued expiry |
| `lead_session_id` | Owning lead session |
| `status` | `queued`, `running`, `completed`, `failed`, `cancelled`, `needs_reconciliation` |
| `reason_code`, `result_text` | Terminal reason and result |
| `session_fenced` | 1 while the native session may still be live and uncertain; blocks dispatch of follow-ups into that session |
| `accepted_at`, `updated_at` | Timestamps (ISO 8601) |

### `dispatch_intents`

One row per job, created in the acceptance transaction. `state` is
`unattempted` until the dispatcher claims the job, then `attempted`. An
unattempted intent is always safe to dispatch; an attempted one is never
dispatched again.

### `runs`

One row per attempt. `generation` increases per job
(`UNIQUE(job_id, generation)`); `correlation` is a unique marker passed to the
agent and used to find its native transcript. `state` is `started`,
`completed`, `failed`, `cancelled` or `needs_reconciliation`. `backend_pid`,
`acked`, `ready_at`, `submitted_at` and `acknowledged_at` record backend
evidence and startup progress for diagnostics only; they never change job
state on their own.

### `events`

Append-only job history (`accepted`, `attempt_started`, `cancelled`,
`needs_reconciliation`, terminal kinds), ordered by `seq`.

## Transaction rules

These are the atomic operations in `DAL/Features/Jobs/JobStore.cs`:

1. **Accept** (`AcceptOrGet`): in one transaction, look up the idempotency key;
   check the active-job limit and the follow-up parent; insert the job, an
   `unattempted` intent, an `accepted` event and, if the lead has a wake
   target, a `wake_jobs` row. The client is answered only after commit.
2. **Claim** (`BeginNextAttempt`): pick the oldest unattempted queued job whose
   native session is not running or fenced; flip its intent to `attempted`,
   insert a `started` run with a new generation and correlation, set the job
   `running`. This commits before any process, tab or prompt is created.
3. **Complete / fail** : update the run only if it is still `started` and is
   the latest generation, then update the job only if it is still `running`,
   and append an event. A stale generation cannot overwrite a newer run.
   `needs_reconciliation` also sets `session_fenced`.
4. **Recover** (`QuarantineUncertainAttempts`, at daemon start): every
   `started` run becomes `needs_reconciliation` with reason
   `daemon_restart_uncertain`, and its job is fenced. Nothing is re-queued. The
   daemon answers requests only after the dispatcher has failed each such job
   with no verified live run or reattached it, so no client reads a quarantine
   that recovery is about to resolve.
5. **Cancel / expire**: queued jobs are cancelled by marking their intent
   `attempted` so they are never claimed. A running job's cancellation is
   committed before ATF asks the dispatcher to stop the owned backend run;
   the session stays fenced until that run is safely stopped or verified idle.

## Lead sessions and wake

| Table | Purpose |
| --- | --- |
| `lead_sessions` | One row per MCP lead session: `workspace` (folder), `binding_key` (host process binding used to reconnect automatically), `lead_token`, current `wake_key`, `closed_at` |
| `wake_targets` | Native wake target per `target_key`: `kind` (`claude`, `codex`, `pi`), `address`, `secret`, `home`, increasing `generation`, notification progress (`notified_seq`, `external_notified_seq`, last success times) and `active` |
| `wake_jobs` | Jobs routed to a wake target; `read_at` is set when the lead reads the result. Unread rows keep a job from being pruned |

For Claude registrations, the kind-specific `home` field stores the owning
host PID. The relay's transient notice offers are in memory; the existing
SQLite unread messages/jobs are the durable source for retry after failure.

## External members

Desktop sessions that join a lead with a one-time ticket
([usage](usage.md#external-members)).

| Table | Purpose |
| --- | --- |
| `external_teams` | A team owned by a lead session or by an in-daemon owner key (PRFactory). `owner_key` unique; `closed_at` when closed |
| `external_members` | Member name per team (`UNIQUE(team_id, name)`); hashed ticket with expiry and use time; hashed member token; `active`, `wake_key`, `left_at` |
| `external_messages` | Messages between lead and members, with global `seq`, per-sender `sender_seq`, `read_at` and `wake_key` |
| `external_sender_cursors` | Per recipient and sender: highest sequence and read cursor, so cursors survive message retention |
| `external_delivery_keys` | Command IDs already delivered to a team, for deduplication |

Tickets and member tokens are stored as hashes only.

## PRFactory connector

State for claimed PRFactory work items ([PRFactory connector](prfactory-connector.md)).

| Table | Purpose |
| --- | --- |
| `prfactory_teams` | One row per `(server, work_item_id)`: claimed payload, state, upload flag, `machine_id`, lead `atf_job_id`, `acceptance_state` |
| `prfactory_members` | Stable job per `(server, work_item_id, member, turn)`, so a repeated claim or command never submits twice |
| `prfactory_external` | External recipe members: mapped external team, current ticket and expiry, upload flag, reply cursor, `closed` |
| `prfactory_command_receipts` | Server commands already handled, with the outcome |

## Pruning

`PruneJobs` removes `completed`, `failed` and `cancelled` jobs older than the
cutoff (30 days by default; daily in the daemon and via `atf prune`) in one
transaction, children before parents. A job with an unread wake row, or with a
retained descendant, is kept. Matching log files are deleted; worktrees are
left for manual cleanup.

## Files next to the database

| Path | Contents |
| --- | --- |
| `profile.json` | Bound principal, limits and enabled backends |
| `operator.key` | Daemon IPC credential (owner-only) |
| `web-console.key` | Web console bearer token |
| `launch-mode.json` | Selected launch mode, terminal host and web port |
| `tier-map.json` | Tier overrides from the web console |
| `herdr-placement.json` | Herdr placement setting from the web console |
| `prfactory.json` | PRFactory connector settings, when connected |
| `daemon.sock`, `daemon.lock` | IPC socket (named pipe on Windows) and single-instance lock |
| `daemon.log`, `logs/<job-id>.log` | Daemon and per-job output |
| `worktrees/<job-id>` | Per-job git worktrees |
| `backups/` | Startup database backups |
