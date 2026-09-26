# Implementation checkpoint — 2026-09-26

This is a coordination snapshot, not release approval. Runtime work is on local
feature/integration branches, not promoted to `main`. The public GitHub destination
still awaits organization creation by the owner; no remote or push exists.

## Delivered snapshots and current gates

| Snapshot | Observable output | Current qualification |
| --- | --- | --- |
| `spike/m0-durable-core` / `55d3c054` | Real `AgentTeamForge.slnx`, Host → Business → DAL, .NET 11 fake-child daemon/SQLite/private IPC/MCP; bridge death and fresh-client result retrieval. | Independent Codex reproduced format, zero-warning build, 37 tests, AOT publish and 13 published process scenarios. **Changes required:** hidden dispatcher faults and deadline not covering initial delivery. Fresh Claude repair in progress. No real backend/platform qualification. |
| `spike/m0-codex-safety` / `6e06de77` | Stopped-session deletion refusal, atomic launch intent, malformed-history rejection. | Independent per-slice Codex review in progress. Native visible-TUI/thread binding and strict human-input race prevention remain unresolved, not solved by these fixes. |
| `spike/m0-transport-bounds` / `8f7b6344` | Bounded JSON-RPC request lifecycle and Herdr CLI output. | Independent Codex review in progress; whole Codex-command budget remains a separate concern. |
| `spike/m0-claude-isolation` / `8a5e385d` | Unsafe Claude managed send/cancel blocked; concurrent hook appends bounded. | Independent Codex review in progress. This refuses unsafe behavior; it does not supply missing native Claude origin/interrupt capability. |
| `main` / `0dbf44b` | F27 operator-console plan and static HTML mockup; shared roadmap has 27 feature rows. | Mockup rendered in Chromium; no runtime web UI. Independent Codex design/security review in progress; user visual feedback separate. |

The legacy `AtfSpike` source is retained only as an isolated experimental baseline.
The new core has real product naming. Consolidate reviewed reusable code/tests and
retire replaced spike projects/scripts; do not delete live sessions or unique
recovery evidence during file cleanup.

## Current independent lanes

| Agent / branch | Bounded next handoff |
| --- | --- |
| Claude `m0-core-fault-fix` / `spike/m0-core-fault-fix` | Repair core B1/B2 with red regression tests; independent Codex re-review before promotion. |
| Claude `m0-demo-runner` / `spike/m0-demo-runner` | Safe one-command fake-core demo, unique owned state and bounded cleanup; no fixed-path destructive cleanup. |
| Claude `m0-job-inspection` / `spike/m0-job-inspection` | Bounded authenticated read-only job listing for CLI/IPC; no browser implementation or extra scheduler. |
| Claude `codex-control-probe` / `spike/codex-control-probe` | C# fixtures/probe for actual start-or-steer races and history shape. Optional native run only against isolated loopback mock responses with no real credentials/model calls. |
| Codex `m0-codex-verifier` / `verify/m0-e2e` | Exact-commit independent review of the three legacy safety lanes, per-slice verdicts and combined tests. |
| Codex `m0-codex-integrator` / `integration/m0-e2e` | Core re-review and later merge of approved small snapshots; combined gates. Parked between immutable inputs, not authorized to self-approve executable fixes. |
| Codex `operator-console-plan-review` / `review/web-console` | Independent F27 architecture/security review only. |

Approximately six useful active workers is the target; parked integration work
need not be kept artificially busy. Finished writers have been retired after
committing their handoff; use fresh bounded writers for new repairs. No 400k-token
implementation session. Codex owns verification/reviews/integration; Claude owns
runtime code. Semantic merge fixes return to a Claude writer and independent
Codex verification.

## Important evidence and coordination boundaries

- `.tools/dotnet11/` in the main checkout is an authorized isolated SDK install:
  `11.0.100-rc.1.26425.128`, official archive SHA-512 verified. Select per command;
  global SDK/PATH unchanged. Legacy experiment stays .NET 10 until deliberately
  retired/migrated.
- Source verification of Codex 0.157.1 found `turn/start` can steer an already
  active turn and lacks expected-idle preconditions. `turn/steer` has an expected
  turn ID but does not solve atomic idle-start. No public TUI-client/thread
  attestation was found. Do not replace those gaps with guessed APIs or a second
  idle read. Isolated fake-core progress is independent of those real-adapter gates.
- The original interactive report and FIXES are implementer evidence, not
  approval. `RE-REVIEW.md` records remaining blockers; newer branch fixes need
  their own immutable-snapshot review.
- Native wake is standard. Public PR #70 is merged and reports Linux/Windows
  Codex-concept evidence; Windows-Claude safe refusal is not native wake support.
  Pi and remaining native host transports still need their own evidence.
- Agent-team queued follow-up messages have **no background dispatcher**. Drain
  the exact pending key once its intended target is waiting; do not blindly drain
  old messages addressed to retired agents. One-shot watcher exits must be
  rearmed after consuming the relevant agent's checkpoint. Prefer exact committed
  reports over unsupported status claims or repeated progress polling.
- Before publication, sanitize reports and exact staged source; raw state,
  transcripts, credentials, evidence, build outputs and worktrees remain ignored.
  Some branch-local review commands may still require personal-path normalization.
