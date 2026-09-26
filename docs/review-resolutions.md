# AgentTeamForge — plan-review resolutions

> Editorial note: Source references and comparison terminology were sanitized
> for public publication. Findings, dispositions, and verdicts remain historical;
> this edit is not a new review or approval.

## Status

This document records dispositions for the findings in the
[independent Claude Opus review](plan-review.md). Original findings are preserved
and the independent re-review is appended to that record. Its verdict is
**approved for M0 investigation**, with no remaining blockers. Contradictions
were fixed in place; this ledger is only an index. No entry is runtime evidence.

## Critical findings

| Finding | Disposition and evidence |
| --- | --- |
| C1 | **Resolved in design.** Acceptance atomically stores job, idempotency record, and queued/unattempted outbox. A separate run generation/correlation attempt-start transaction commits before process spawn, tab launch, or prompt delivery. Queued/no-attempt work may dispatch; an attempt without evidence is uncertain. See [plan transaction rules and crash windows](plan.md#53-transaction-rules), [PoC C07/C08](poc.md#6-automated-failure-and-contract-matrix), and [handoff](../HANDOFF.md#sqlite-and-delivery). |
| C2 | **Resolved in design.** Completion requires a run-correlated authoritative lifecycle signal or verified durable backend record. Model self-report is informational; terminal text/silence is non-authoritative. Foreign/human activity is recorded separately under a conservative human-wins pause. The restricted child-side host bridge is named. Keystroke injection is unsupported in v1 absent a later explicit ADR/user scope decision. See [interactive evidence contract](terminal-modes.md#interactive-evidence-contract), [domain and adapter contracts](plan.md#4-backend-strategy), and T15/T16/T18. |
| C3 | **Resolved in design.** Recovery never auto-kills a live interactive TUI: verify/rebind or leave live and block machine work. Kill-on-close is headless-only under approved policy. Outage completion must replay from verified backend records or, if necessary, a bounded private helper spool; otherwise support/recovery stays blocked. See [terminal ownership rules](terminal-modes.md#5-ownership-and-crash-rules), [process recovery](plan.md#64-process-recovery), T10/T17, and PoC L06. |

## Moderate findings

| Finding | Disposition and evidence |
| --- | --- |
| M1 | **Accepted.** `run_reconcile` is an authorized lead/operator, idempotent, evidence-bound operation. It may accept an observed result, abandon safely, or request a new attempt with a new key only after verified idle/termination. DB state alone cannot release physical ownership. See [plan recovery/API](plan.md#7-api-and-tool-budget) and C31. |
| M2 | **Accepted.** Event cursors belong to a durable team-role principal with one fenced active consumer generation; reconnect advances the generation and stale read/ack fails. See [message reading](plan.md#54-messages-and-reading) and C32. |
| M3 | **Accepted as the M0 bootstrap candidate.** A private, short-lived, single-use nonce file is exchanged locally for a child-scoped capability. Environment inheritance is not assumed; secrets stay out of argv/logs. Operator/lead reattach uses authenticated local delegation. Same-OS-user access is explicitly not a sandbox. See [local IPC](plan.md#33-local-ipc) and [platform risks](terminal-modes.md#6-platform-specific-risks). |
| M4 | **Accepted.** M0 tracks platform × mode × intended production launch context for `PATH`, home/profile, config, credentials, and authentication. Setup includes a doctor in the actual daemon context. See [setup](terminal-modes.md#3-setup-and-configuration), [plan lifecycle](plan.md#32-service-lifecycle), and roadmap M0. |
| M5 | **Accepted.** Turn interrupt and agent stop are separate. Interactive interrupt keeps the tab by default; hard-stop escalation requires an explicitly accepted policy. Unknown outcomes remain blocked/unconfirmed. Recovery needs explicit human intent to stop a live TUI. See [plan API](plan.md#7-api-and-tool-budget), [terminal crash rules](terminal-modes.md#5-ownership-and-crash-rules), C21/C22, and T08. |
| M6 | **Accepted.** Approval visibility is an explicit adapter capability: surface blocked state or mark unsupported; never fabricate running or bypass. L05 covers both Linux modes and T18 covers disconnected-lead interactive approval. See [backend capabilities](plan.md#44-backend-capabilities) and [PoC live matrix](poc.md#7-live-matrix). |
| M7 | **Option A selected.** The full PoC runs T04/T07/T10 per platform from a temporary production-style launch context; no full installer is required. Linux daemon runs outside Herdr and the killed client tree. Full install remains M3. See [platform risks](terminal-modes.md#6-platform-specific-risks), [PoC platform guarantees](poc.md#platform-guarantees-and-separate-poc-decisions), and roadmap M0/M3. |
| M8 | **Accepted.** L03–L06 explicitly cover both Linux modes. L10/L11 and platform gates include all relevant T01–T12 and T15–T19. Named C/T ranges were updated. See [PoC live matrix](poc.md#7-live-matrix) and [terminal cases](terminal-modes.md#8-required-terminal-test-cases). |
| M9 | **Accepted with scoped status.** README, handoff, and roadmap now identify concurrent exploratory non-.NET spike code as non-production and unverified by this documentation session. No runtime/test absence is claimed for that tree. Generated `__pycache__/` is untouched; the owner is told to ignore generated files before Git initialization. See [README status](../README.md#status) and [handoff validation](../HANDOFF.md#8-validation-status-at-handoff). |

## Optional suggestions

| Suggestion | Disposition and evidence |
| --- | --- |
| S1 | **Keep the full contract.** No scope cut was made. A missing comparison baseline may be reported while correctness spikes continue, but no comparative efficiency claim is allowed. The small fake connector seam remains. See [measurement preconditions](poc.md#81-preconditions). |
| S2 | **Accepted.** T19 verifies that retry after a lost `agent_start` response does not open a duplicate tab/process. |
| S3 | **Accepted.** macOS selection criteria now include stable per-tab identity, targeted close, environment/bootstrap, TCC/Accessibility requirements, and scriptability without Accessibility access. See [platform risks](terminal-modes.md#6-platform-specific-risks). |
| S4 | **Accepted.** The PoC has a separate host/child bridge startup and memory budget; M0 verifies MCP tool registration under trim/AOT and source generation if needed. See [measurement targets](poc.md#83-metrics-and-preliminary-budgets) and roadmap M0. |
| S5 | **Accepted.** v1 selects conservative human-wins/foreign-busy: automatic follow-ups pause until explicit reconciled idle. See [interactive evidence contract](terminal-modes.md#interactive-evidence-contract) and T15. |
| S6 | **Accepted.** Codex lead wake/catch-up is explicitly unverified and cannot inherit Claude L07 evidence. See [PoC live matrix](poc.md#7-live-matrix) and roadmap M3 support matrix. |
| S7 | **Accepted.** Canonical order is HANDOFF → AGENTS/README → terminal modes → plan → PoC → roadmap → review/resolutions. See [README reading order](../README.md#reading-order) and [handoff](../HANDOFF.md#3-read-in-this-order). |

## Independent re-review and final clarifications

Claude Opus re-reviewed the revised documents and found C1–C3 and M1–M9
resolved, with S1–S7 dispositioned. See
[the appended verdict](plan-review.md#8-re-review-of-documented-resolutions).
Approval is for M0 investigation, not implementation correctness or the M0 exit.

The reviewer also suggested five non-blocking clarifications. They were applied
as requested after that verdict:

1. `roadmap.md` places the required production-context T04/T07/T10 execution
   gate in the full PoC (M1); M0 defines/schedules the checks and may run early.
2. `plan.md` labels `foreign_busy` as an agent/session state, not job status.
3. PoC C01 limits attempts per job until authorized reconciliation, not merely
   per generation.
4. PoC C24 explicitly rejects a child capability trying to reattach as lead.
5. README and handoff state that non-.NET exploratory code cannot count as M0
   implementation under the current C#/.NET-only contributor policy.

These clarify the approved design rather than assert new runtime evidence.

## Documentation validation

Bounded standard-library checks cover all Markdown files in this planning set:
UTF-8, final newlines, no trailing whitespace/tabs/CR characters, balanced code
fences, local file and heading links, and contiguous C01–C32, L01–L12, T01–T19
case definitions. All checks passed. Standard project format/lint tooling should
be added during implementation setup; no runtime build/test/AOT claims are made.
