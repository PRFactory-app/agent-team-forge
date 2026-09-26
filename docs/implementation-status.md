# Implementation checkpoint — 2026-09-26

This is a coordination snapshot, not release approval. Runtime work is on local
feature/integration branches, not promoted to `main`. The public GitHub destination
still awaits organization creation by the owner; no remote or push exists.

## Delivered snapshots and current gates

| Snapshot | Observable output | Current qualification |
| --- | --- | --- |
| `spike/m0-durable-core` / `55d3c054` | Real `AgentTeamForge.slnx`, Host → Business → DAL, .NET 11 fake-child daemon/SQLite/private IPC/MCP; bridge death and fresh-client result retrieval. | Independent Codex reproduced format, zero-warning build, 37 tests, AOT publish and 13 published process scenarios. **Changes required:** hidden dispatcher faults and deadline not covering initial delivery. Claude repair committed at `1d3c409`; independent Codex re-review in progress. No real backend/platform qualification. |
| `spike/m0-codex-safety` / `6e06de77` | Stopped-session deletion refusal, atomic launch intent, malformed-history rejection. | Bounded primitives independently approved and merged into integration at `1f68da5`; complete item validation and identity-bound teardown still open. Native visible-TUI/thread binding and strict human-input race prevention remain unresolved, not solved by these fixes. |
| `spike/m0-transport-bounds` / `8f7b6344` | Bounded JSON-RPC request lifecycle and Herdr CLI output. | Bounded primitive independently approved and merged into integration; whole Codex-command budget remains a separate concern. |
| `spike/m0-claude-isolation` / `8a5e385d` | Unsafe Claude managed send/cancel blocked; concurrent hook appends bounded. | Bounded refusal/hook-cap changes independently approved and merged into integration. This refuses unsafe behavior; it does not supply missing native Claude origin/interrupt capability. |
| `main` / `0dbf44b` | F27 operator-console plan and static HTML mockup; shared roadmap has 27 feature rows. | Mockup rendered in Chromium; no runtime web UI. Independent review `a113bc53` requires pairing-authority and reload-recovery corrections; Pi plan revision in progress. User visual feedback is separate. |

The legacy `AtfSpike` source is retained only as an isolated experimental baseline.
The new core has real product naming. Consolidate reviewed reusable code/tests and
retire replaced spike projects/scripts; do not delete live sessions or unique
recovery evidence during file cleanup.

## Current independent lanes

| Agent / branch | Bounded next handoff |
| --- | --- |
| Claude `m0-client-deadlines` / `spike/m0-client-deadlines` | Bound the entire IPC client operation; preserve uncertain acceptance and daemon job independence. |
| Claude `m0-teardown-fence` / `spike/m0-teardown-fence` | Refuse destructive name-based teardown when provider identity cannot be bound atomically; replacement-race regressions. |
| Codex `m0-codex-verifier` / `verify/m0-e2e` | Independent review of demo `aba651b` and job inspection `d54ff3a`, separately and combined. |
| Codex `m0-codex-integrator` / `integration/m0-e2e` | Re-review core B1/B2 repair `1d3c409`; merge only if approved, then combined gates. |
| Codex `codex-control-probe-review` / `review/codex-control-probe` | Review fake-only probe `73dab8d`; no native capability inferred from fixtures. |
| Pi `operator-console-plan-revision` | Address independent F27 design findings; no runtime web implementation. |

The four previous Claude writers committed their handoffs and were stopped with
`kill_agent`. The completed web-plan reviewer and obsolete research worker were
also retired. New Claude workers use win-agent-teams; new Codex/Pi workers use
native subagents. The two existing MCP-hosted Codex reviews finish their current
assignments before retirement.

Approximately six useful active workers is the target; waiting workers do not
count as active work and should be retired after their handoff is captured. Finished writers have been retired after
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
