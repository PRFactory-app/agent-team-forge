# 0003. A separate daemon owns accepted jobs; SQLite holds state

Status: accepted

## Context

In the reference tool, coordination lives inside the lead agent's MCP server.
When the lead or its bridge dies, in-flight work and its results can be lost.
Agent sessions routinely outlive the lead that started them.

## Decision

- One local daemon per user and state directory owns accepted jobs,
  dispatch, results and wake. It runs outside the lead's process tree.
- MCP bridges, the CLI and the web console are clients over authenticated
  local IPC (a Unix socket with an owner-only key file; a named pipe on
  Windows). A client crash never cancels committed work.
- SQLite (`jobs.db`, WAL, `synchronous=FULL`, foreign keys, bounded busy
  timeout) holds structured state. Files hold worktrees, logs and artifacts.
- A job is acknowledged only after its acceptance transaction commits. Retries
  with the same idempotency key return the same job.
- The bridge starts the daemon lazily on first use; a start lock prevents two
  daemons for one state directory.

## Consequences

- Leads can crash, restart or change without losing jobs; a new session reads
  results later and can adopt prior jobs with `resume_session`.
- Schema changes are forward-only migrations in `DAL/Migrations/Schema.cs`. A
  newer schema is refused rather than modified. See the
  [data model](../data-model.md).
- The daemon takes a database backup on its first start per boot and keeps two.
  There is no restore command; restore is a manual file copy.
