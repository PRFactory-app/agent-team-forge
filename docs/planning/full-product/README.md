# Full-product waterfall planning brief

## Mandate and status

Plan the complete AgentTeamForge product, not just the first demonstration.
Phase plans may be authored in parallel, but implementation follows an ordered
waterfall baseline with entry criteria, concrete deliverables, acceptance gates,
and change control. These documents are **draft plans**, not implementation
approval, passed reviews, or evidence of runtime support.

The user wants rapid progress and a real end-to-end demonstration within hours.
That is a near-term checkpoint, not a credible promise that the complete
multi-platform product can be delivered within hours. Preserve the separately
scoped [Linux demo](../../spikes/e2e-demo-plan.md) while planning the full path.

## Shared constraints for every phase author

- All authored documentation is English, public-facing and self-contained.
  No private repository/company references, absolute personal paths, secrets,
  internal tracker IDs, or dependence on inaccessible source material.
- .NET 11 is the selected target. RC1 public SDK candidate is
  `11.0.100-rc.1.26425.128`; source research is not compile/AOT evidence. See
  [research](../../research/net11-process-api.md). No installation in this task.
- Exactly three production projects: **Host → Business → DAL**. Feature-first
  vertical slices, direct Business-to-DAL dependency; no imposed Clean
  Architecture, generic mediator/repository stack, or extra production assembly.
- Local daemon owns accepted work; thin MCP bridges are clients. SQLite stores
  structured state; files remain appropriate for workspaces/artifacts/logs.
- Real interactive agent TUIs are mandatory in Herdr/Linux and visible selected
  Windows/macOS terminals; headless is an explicit setup choice. No log-tail or
  silent fallback. Managed sessions and later attachment remain distinct.
- Acceptance and unattempted intent commit together. Attempt generation commits
  before external effects. No blind replay after uncertain delivery. Distinguish
  client crash, daemon reconciliation, and backend/machine failure.
- No PID-only adoption or teardown; no text-tag/model-self-report completion;
  human/foreign activity pauses automation until authorized verified-idle
  reconciliation. Turn interrupt and agent stop are distinct.
- Native session wake is the standard mechanism, per the public
  [PR #70](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70), not
  optional watcher-driven normal operation. Durable messages/read-ack remain
  authoritative; host-native notices are scoped, coalesced and bounded. Manual
  catch-up is explicit degraded recovery, not a silent replacement. Native
  Windows/macOS/Pi gaps need proof and implementation, not inherited PR approval.
- Pragmatic TDD for business-critical behavior, not prose assertions or numerical
  coverage targets. Independent plan review for major changes; opposite-model-
  family code review. No automatic review waiver from an earlier spike.
- No mandatory external orchestrator, cloud, telemetry, distributed scheduler,
  custom model loop, billing, or dashboard. Existing agent TUIs do not count as
  building a product dashboard. Installation/setup remains the main product UI.
- Planning does not authorize Git initialization, license selection on behalf of
  the owner, publication, platform access, live model spending, or system changes.

## Proposed full-product baseline and option boundary

**Full-product baseline to plan:** durable multi-team execution; managed Claude
Code, Codex and **spawned Pi** in both selected launch modes; lifecycle, messages,
durable read/ack,
approvals, budgets, capabilities and parent/child/nested team relationships;
authenticated MCP/CLI control; supported-host wake/catch-up; workspaces/artifacts;
Linux/Windows/macOS install/configuration/doctor/update/uninstall; backup,
recovery, bounded resource usage and security; public documentation/distribution;
explicitly supported attachment/join flows and an optional external orchestrator
connector through a documented generic contract.

**User clarification:** spawned Pi is mandatory in P03, not deferred to P06 and
not satisfied by attach-only or lead-host Pi. It must work natively on **Windows**
as well as the baseline platforms. Plan actual Windows start/follow-up/result/
interrupt/stop/reconnect tests with real CLI and GUI sessions for interactive
mode; cross-build or Linux tests do not pass these gates. Record command-wrapper,
argument/Unicode, process/handle, credential/profile and terminal-binding behavior.
Use neutral public capability descriptions, not private reference-tool names.

These expanded capabilities are a **proposed full-product planning baseline**,
not a declaration that arbitrary existing desktop sessions or unknown third-party
servers can be controlled. Pin supported provider/host/protocol combinations and
name required external decisions/tests. Connector deployment remains opt-in.
Each currently unsupported capability needs a gate or explicit scope decision,
not simulated success.

**Decision-gated options, not automatic v1 requirements:** custom dashboard/web
UI, additional backend vendors beyond the named set, arbitrary legacy API
compatibility, multiplexing as an optimization, additional CPU architectures or
terminal providers beyond the chosen support baseline, seamless arbitrary process
adoption after daemon death, and replacing someone else's worker. Record where
these would fit without allowing them to silently expand the critical path.

## Ordered phases and document ownership

| Phase | Owned plan | Main outcome |
| --- | --- | --- |
| P01 | `01-foundation.md` | Scope/acceptance, architecture and capability/SDK risk decisions frozen. |
| P02 | `02-durable-core.md` | Durable local daemon, SQLite, private IPC/MCP and fake crash-tested vertical slice. |
| P03 | `03-managed-agents.md` | Safe real Claude/Codex/Pi managed interactive/headless execution, including native Windows Pi; early Linux E2E checkpoint. |
| P04 | `04-team-orchestration.md` | Complete team control, nested delegation, messages/events, approvals/budgets, wake, workspaces/artifacts. |
| P05 | `05-platform-experience.md` | Real three-platform service/desktop integration, setup/CLI, packages/update/uninstall. |
| P06 | `06-extension-integration.md` | Supported attached/join modes, additional lead-host integration, optional generic external connector. Managed Pi is already required in P03. |
| P07 | `07-system-qualification.md` | Whole-product security, recovery, load, upgrade and cross-platform acceptance qualification. |
| P08 | `08-release-operations.md` | Public release gate, distribution/docs, operational maintenance and compatibility lifecycle. |

[Cross-phase contracts](contracts.md) resolves scope IDs, schema ownership,
delivery cursors, native wake, proposed limits and the early-experiment boundary.
It is draft synthesis, not independent approval.

Execution dependency: **P01 → P02 → P03 → P04 → P05 → P06 → P07 → P08**.
Early research and necessary platform probes belong to P01; do not defer discovery
of a fundamental Windows/macOS blocker until P05. P07 is cumulative system
qualification, not the first time tests/security are considered. Each feature
phase already owns TDD, applicable integration tests, and reviews.

P03 may expose a labelled Linux/Codex demonstration before its complete exit,
but that preview does not let later waterfall gates pretend P03 is complete.
A fixed-scope preview and the full-product critical path are different claims.

## Required template for each phase plan

1. Objective, scope, explicit exclusions, and current evidence/status.
2. Entry criteria and inputs from named predecessor(s), including unresolved
   decisions that block a safe start.
3. Numbered vertical work packages (`Pnn-W01`, etc.), in order. For each:
   observable result, Host/Business/DAL touchpoints, preceding package dependency,
   deliverables and meaningful tests. Do not prescribe needless classes.
4. Contracts handed to subsequent phases: data/API/lifecycle/capability choices,
   schema/version impact, ownership/security invariants and a verification artifact.
5. Gate table (`Pnn-G01`, etc.): reproducible verification method, exact success
   criterion, real-platform versus fake evidence, and blockers. Reuse C/L/T cases
   from `docs/poc.md`/`terminal-modes.md` where they apply; partial mapping is not
   full coverage. Commands may be proposed but must not be described as existing.
6. User/operator acceptance demonstration, including negative/failure cases.
7. Risks, dependencies/decision log, stop/go rules, and rollback/safe recovery.
8. Size/risk estimate and sequencing assumptions. Use relative effort (S/M/L)
   and confidence, not invented calendar certainty. Identify external wait time.
9. Exit handoff checklist and definition of done, including documentation/review.

Keep plans implementable and bounded rather than writing an enterprise process
manual. Read the existing architecture/plan/PoC/roadmap for detail; preserve their
safety contracts. Current spike code review has unresolved safety findings until
fixes are independently checked. Reuse feasibility evidence with exact limits;
never promote it as a completed product layer.

The parent owns this index, final scope/traceability synthesis, and
`docs/roadmap.md`. Each phase author writes only its assigned plan.
