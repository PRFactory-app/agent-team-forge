# 0008. Never replay an uncertain prompt

Status: accepted

## Context

A prompt written to an agent may have been delivered even when its response
was lost, the transcript was late, or the daemon crashed. Resending it can make
the agent do the work twice (two commits, two pushes). An early Herdr backend
resent after five seconds without transcript evidence.

## Decision

- Before any process spawn, terminal launch or prompt write, a transaction
  records the attempt (`dispatch_intents.state='attempted'` plus a `runs` row
  with generation and correlation).
- An attempt without authoritative evidence of its outcome becomes
  `needs_reconciliation`. It is never retried automatically.
- Only a proven pre-delivery failure may be retried.
- Completion requires the backend's native completion record for the
  correlated turn. Later human input in the same session does not change the
  machine job's result.
- A `needs_reconciliation` job fences its whole native session
  (`jobs.session_fenced`). An idle, verified interactive pane can settle an
  unobserved interrupted turn before a follow-up is dispatched. Unrelated
  sessions keep running.
- `stop_job` signals only a verified owned process. If no marked process
  remains, it cancels the fenced job and clears its fence.

## Consequences

- Some jobs end as `needs_reconciliation` even though the agent finished; the
  operator reads the agent's session and decides.
- Clients retry with the same idempotency key, which returns the existing job,
  never a second one.
