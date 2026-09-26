# Independent review: M0 acceptance outcome after commit

2026-09-26. Independent Codex/GPT review of Claude-authored source commit
`d714348938061b27a06f693d19c0f822ef7e6a66`, based on
`81a11b27fb67d2ec600ace67586c092829927f8f`, read from the exact
`spike/m0-acceptance-recovery` tree. Reviewed the complete source and test diff,
the SQLite transaction and IPC call paths, prior core and deadline reviews, and
`spikes/m0-durable-core/ACCEPTANCE-OUTCOME-REPORT.md`. The branch was neither
merged nor edited for this review.

**Verdict: approve the focused post-commit response fix, with 0 blocking and
1 non-blocking qualification below.** The post-commit row read is removed.
An injected failure at `accept.after-commit` now returns `outcome_unknown`,
and same-key recovery returns the existing job with one durable intent and one
backend invocation. This does not certify every possible failure of `COMMIT`
or power-loss durability. The already approved fake-core demo checkpoint is
separate and remains valid.

## Finding and boundary

1. **Non-blocking — the report overstates the classification at `COMMIT`.**
   `JobStore.cs:55-61` returns an `Accepted` record only after `tx.Commit()`
   returns successfully. `AcceptJob.cs:49-57` still maps a `StorageException`
   thrown by `COMMIT` to `storage_busy` or `storage_unavailable`; no new test
   injects a commit failure or observes the resulting transaction state. The
   before-commit checkpoint test proves rollback for that injected exception,
   not for every error during `COMMIT`. SQLite documents that `SQLITE_BUSY` at
   `COMMIT` leaves a transaction active, while other I/O or full-disk errors
   may leave transaction state that must be inspected or rolled back; see
   [SQLite transaction error handling](https://www.sqlite.org/lang_transaction.html).
   The report's blanket "not committed, per SQLite rollback semantics" should
   be narrowed. A caller should use the same key to resolve an uncertain
   commit result; do not infer that a storage error proves no acceptance in
   an untested failure mode. This is a qualification of an inherited commit
   boundary, not a regression introduced by removing the post-commit read.

## Reviewed behavior

- The `BEGIN IMMEDIATE` transaction still inserts the job, unique scoped key,
  unattempted dispatch intent, and `accepted` event together. On successful
  commit, `AcceptOrGet` now returns fields that were written, with `queued`,
  no result/reason, and zero attempts. It no longer performs a post-commit
  `GetJob` that could fail and turn known acceptance into a storage error.
- The returned record is an **acceptance-time snapshot**. `AcceptJob` signals
  dispatch before the endpoint replies, so the persisted job can already be
  `running` or `completed` when the caller sees `queued`. `job_get` provides a
  separate current-state read. This is an expected staleness window, not a
  duplicate or lost acceptance.
- `JobsEndpoint.cs:17-31` catches the named test checkpoint after a successful
  accepted result and maps its injected failure to `outcome_unknown`. It does
  not retry or mint a new key. The client/bridge paths already require an
  explicit same-key retry or `job_get`. The endpoint catch is scoped to that
  post-commit checkpoint; broad claims about arbitrary post-commit exceptions
  in other components are outside the tested behavior.
- The endpoint SQLite test checks one intent, one signal, one accepted event,
  and an `existing` response for the same key. The native process scenario
  checks the daemon stays alive and the job finishes with one attempt and one
  fake-backend invocation. A separate before-commit checkpoint stores nothing
  and gives a definite injected error. No fault is injected within `COMMIT`.

## Independent gates and limits

From the exact clean acceptance source tree, with command-scoped main-checkout
SDK `11.0.100-rc.1.26425.128`, ran:

```bash
main_checkout="$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)"
DOTNET="$main_checkout/.tools/dotnet11/dotnet" ./scripts/verify.sh
```

Inside `spikes/m0-durable-core`, restore and format verification passed;
Release `-warnaserror` build had 0 warnings and 0 errors; **65/65** tests
passed; Linux x64 Native AOT publish passed with warnings as errors; **21/21**
scenarios passed against the published native binary. The tested `atf` SHA-256
was `d102d99d3a5f71658df07b666e080bace5e9bf7de3bbbea6ec66580cb3b77390`.
`git diff --check 81a11b2..d714348` passed. This artifact hash is evidence
for this build, not a reproducibility claim.

These results cover the named injected pre- and post-commit checkpoints and
normal published fake-backend execution on Linux x64. They do not establish
WAL power-loss behavior, arbitrary disk I/O failure handling, Windows/macOS,
Pi, real agents, Herdr, services, or native wake. No combined hardening tree
was built or tested; the parent owns integration and its subsequent review.
