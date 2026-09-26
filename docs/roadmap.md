# AgentTeamForge — full-product waterfall roadmap

## 1. Purpose and authority

This roadmap covers the path to a **complete, supported tool**, not only a
prototype. It replaces the earlier M0–M5 execution outline with eight ordered
phases. The detailed [product scope](product-scope.md) defines what “full” means;
[architecture](architecture.md) defines how the code is organized.

**Status: proposed planning baseline. No phase is declared complete by this
document.** Phase plans are authored in parallel by GPT-6 Astra at medium effort;
their synthesis is not an independent plan-review verdict. Required reviews,
user decisions and actual evidence remain gates before implementation/release.

Use waterfall for scope, dependencies and phase handoffs, not for postponing
tests. Each phase implements end-to-end vertical slices in
**Host → Business → DAL**, with pragmatic TDD and opposite-family code review.

## 2. Delivery sequence

```text
P01  Requirements, architecture and feasibility baseline
 │   Freeze scope, contracts, supported configurations and risk decisions
 ▼
P02  Durable local runtime
 │   SQLite + independent daemon + private IPC/MCP + fake crash scenarios
 ▼
P03  Managed real agents
 │   Claude/Codex/Pi, including native Windows Pi; interactive/headless
 │   └── Early checkpoint: labelled Linux/Codex end-to-end demo
 ▼
P04  Complete local team orchestration
 │   Teams, nested delegation, messages/events, native wake, workspaces
 ▼
P05  Three-platform operator experience
 │   Services/desktop sessions, setup/doctor, CLI, packaging/update/uninstall
 ▼
P06  Attachment and optional integrations
 │   Supported attachment/join, additional lead hosts, opt-in connector
 ▼
P07  Integrated product qualification
 │   Cross-platform security, recovery, load, upgrade and user acceptance
 ▼
P08  Public release and operations
     Authorized distribution, documentation, support and maintenance
```

Every phase has one detailed plan. The plan's `Pnn-Wnn` work packages define the
implementation order within that phase; `Pnn-Gnn` gates define its exit evidence.
Later phases cannot bypass a failed predecessor by calling an unsupported
feature “done.” Returning to a contract decision is explicit change control.

Early platform feasibility probes happen in P01. P05 is production integration
and qualification, not the first time Windows/macOS are investigated. P07 is
whole-system qualification, not the first time security, testing or recovery is
addressed.

## 3. Phase summary and handoffs

| Phase / plan | Entry requirement | Concrete output | Exit decision / successor |
| --- | --- | --- | --- |
| [P01 — Foundation](planning/full-product/01-foundation.md) | Proposed full scope, existing evidence and unresolved findings visible. | Requirements/configuration baseline; architecture, delivery/identity contracts; .NET 11/AOT and backend/platform feasibility; decision log and risk dispositions. | Independently reviewed design and explicit supported scope, or stop/replan. Hands versioned contracts to P02. |
| [P02 — Durable core](planning/full-product/02-durable-core.md) | P01 baseline and required SDK/build access. | Three-project core; durable acceptance/idempotency/attempt/result transactions; private IPC/MCP; fake child and deterministic crash evidence. | Published core behaves honestly under failure; no duplicate unsafe dispatch. Hands storage/application/process contracts to P03/P04. |
| [P03 — Managed agents](planning/full-product/03-managed-agents.md) | P02 core; P01 verified backend/provider contracts; spike blockers repaired/re-reviewed before reuse. | Managed Claude/Codex/Pi lifecycle, including native Windows Pi, in explicit interactive/headless modes, native evidence, safe ownership, approval/cancel/recovery semantics; real-agent demo. | Required backend/mode capabilities pass their gates; unsupported capability requires decision, not simulated success. Hands execution API/capabilities to P04. |
| [P04 — Team orchestration](planning/full-product/04-team-orchestration.md) | P03 verified execution and P02 durable state. | Multi-team/nested control, durable message/read-ack, approvals/budgets, scheduling, host wake/catch-up, workspace/artifact safety and operator operations. | Isolation, delivery cursors, policy and supported-host live acceptance pass. Hands stable feature APIs/CLI requirements to P05. |
| [P05 — Platform experience](planning/full-product/05-platform-experience.md) | Stable P04 feature surface and access to real supported desktop environments. | User-context service/desktop integration on Linux/Windows/macOS; explicit setup, doctor, CLI, safe config merge, packages, upgrade/rollback/uninstall. | Fresh-profile install/use/recovery/update/uninstall passes for each claimed platform; missing access blocks that platform. Hands release-like runtime environments to P06/P07. |
| [P06 — Extensions/integrations](planning/full-product/06-extension-integration.md) | Stable local product and public capability/application contracts. | Supported attached/join modes, additional lead-host integration using the P03 Pi backend, and opt-in generic external connector with scoped credentials, offline ownership and approved export. | Each promised combination is tested; connector disabled means no contact or dependency. Hands integrated feature matrix to P07. |
| [P07 — System qualification](planning/full-product/07-system-qualification.md) | Feature-complete baseline from P01–P06 with reviewed artifacts. | Integrated failure/security/platform/load/upgrade evidence, bounded resource measurements, backup/restore validation and operator acceptance. | No unresolved release-blocking correctness/security finding; complete support/limitation matrix and accepted release candidate for P08. |
| [P08 — Release/operations](planning/full-product/08-release-operations.md) | Qualified release-candidate snapshot plus explicit owner publication/license decisions. | Sanitized public repository/docs, reproducible distributable artifacts, notices, installation/runbooks, compatibility/update/security maintenance policy. | Authorized release and verified fresh-user onboarding; operational handoff and follow-up ownership. |

See the [planning brief](planning/full-product/README.md) for common constraints
and authoring conventions, and [cross-phase contracts](planning/full-product/contracts.md)
for resolved scope, schema, cursor, wake and limit boundaries. The 26-feature inventory in [product scope](product-scope.md#2-product-feature-inventory)
provides the feature-to-phase mapping; each phase supplies finer work-package
and gate traceability.

## 4. Near-term end-to-end checkpoint

The user's speed target is a real runnable demonstration within a few hours.
Treat it as a priority and scope constraint, **not a promise of full-product
completion within hours**.

The demonstrator is deliberately narrower than P03 exit. A reviewed isolated
Linux experiment can proceed before full multi-platform phase qualification;
it cannot mark P01/P02/P03 complete. See [the experiment boundary](planning/full-product/contracts.md#6-waterfall-path-versus-early-experimental-preview).

Demo acceptance:

1. .NET 11 daemon starts independently of the client.
2. Explicit Herdr interactive mode opens one proven-owned Codex TUI.
3. A real MCP client submits a small harmless job through the bridge.
4. SQLite commits acceptance and attempt intent at the correct boundaries.
5. Kill the client/bridge while the job runs; keep daemon/agent alive.
6. A fresh client reads the authoritative committed result.
7. A follow-up reaches the same conversation; retry does not duplicate work.

See [the demo increment](spikes/e2e-demo-plan.md) and
[the durable-core implementation plan](spikes/m0-durable-core-plan.md).
Fake-backend tests are the first engineering checkpoint, not the user-facing
end-to-end claim. JIT-only operation, missing human interaction tests, blocked
Claude cancellation or untested platforms must be stated explicitly.

Existing interactive feasibility has independent safety-review blockers until
repairs are re-reviewed. Its previous unit-test pass does not approve promotion
into product code. Do not exchange ownership, correlation or authorization
checks for demo speed.

## 5. Parallelism without losing the waterfall baseline

Safe parallel work:

- Write phase plans from the shared brief; parent synthesis resolves differences
  before the requirements/design baseline is frozen.
- Review a stable earlier artifact while its author repairs another independent
  component, or while a later design draft is prepared.
- Run platform feasibility probes early when environments are available.
- Within a phase, implement independent slices against an agreed contract, with
  explicit file/worktree ownership and required re-review after changes.
- Repair the existing experimental adapter while reviewing the durable-core
  design. Implement the core after its design gate, independently of adapter
  repair; integrate only reviewed compatible parts.

Not safe parallelism:

- Two writers changing the same contract without coordination.
- Coding against an unresolved ownership/security decision and assuming review
  will authorize it later.
- Reusing an old review after the reviewed source changed.
- Starting the next full phase without required predecessor acceptance, or
  presenting a limited preview as the completed phase.

Native session wake is the standard mechanism, following the public
[PR #70](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70).
P01 proves required native transports; P04/P06 implement host integrations;
P05/P07 qualify actual installed platforms, including Windows. Commit messages
before wake and fetch them through authenticated read/ack. The reference's
Linux-Claude/Codex evidence does not establish Windows-Claude, macOS or Pi.
Missing required native wake blocks that support cell; manual catch-up is visible
degraded recovery, not normal watcher/model polling or a silent scope reduction.

No independent plan review is required for every small fix. Major architecture,
persistence/security or phase-contract changes get plan review; all implementation
gets opposite-family code review per [AGENTS.md](../AGENTS.md).

## 6. Shared definition of done

For each vertical work package and phase, as applicable:

- Required behavior and negative cases implemented within its accepted scope.
- Business-critical red/green/refactor tests pass; no brittle prose/formatting
  assertions or arbitrary coverage percentage.
- Full applicable format, analyzers/build, tests and published-binary gates pass.
  A filtered pass does not imply the whole solution is green.
- Native AOT and native SQLite assets exercised on every claimed runtime ID;
  source/API availability is not runtime compatibility evidence.
- Actual platform GUI/service/backend scenarios run where required; fake and
  headless evidence never imply interactive support.
- Opposite-family code review blockers closed and relevant fixes re-reviewed.
- Versioned contracts/schema changes, migration/rollback and security impact
  documented; successor phase receives explicit artifacts.
- Evidence identifies code/build, environment, command, result, failure/unrun
  cases and limitations. No model self-report as authoritative completion.
- Public documentation and examples contain no private source dependencies or
  credentials; raw runtime logs require a separate publication review.

Suggested automated gate categories are format/lint, Release build with warnings
as errors, unit/contract tests, real SQLite/IPC/process crash tests, native publish
and smoke, dependency/security checks, and documentation links. Exact commands
are supplied by implementation; this roadmap does not claim those scripts exist.

## 7. Full-product acceptance and scope changes

Full acceptance uses the feature inventory and all required phase gates. The
existing [C01–C32 / L01–L12 PoC cases](poc.md) and
[T01–T19 terminal cases](terminal-modes.md) remain relevant baseline tests,
not the entire feature-complete test plan. Pi, nested teams, attachment, connector,
updates and operations add focused phase-specific gates.

Managed Claude Code, Codex and Pi, required launch modes, and all three target
platforms remain in the full baseline. Spawned Pi is required in P03, including
actual native Windows spawn, follow-up, results, interrupt/stop and reconnect;
P05 verifies installed contexts. Linux runs and cross-builds cannot prove Windows
support. Supported join, additional lead-host integration and connector features
are planned in P06. A public Linux-only preview may be useful, but
is not a full-product release. Arbitrary Desktop attachment, custom dashboards,
new vendors/RIDs, broad legacy compatibility and seamless arbitrary process
adoption remain [decision-gated options](product-scope.md#3-explicit-option-boundary).

If a required backend cannot provide safe control, authentic correlation or
interrupt evidence, stop that gate. Resolve the protocol, select another verified
adapter, or obtain an explicit scope decision. Do not silently replace an
interactive TUI with a log tail or present unknown execution as failed/safe to retry.

For a change, record affected F IDs, phase/work-package dependencies, migration,
verification impact, accepted risk, and owner decision. Rebaseline downstream
plans rather than allowing contradictory contracts to accumulate.

## 8. Estimates and critical path

Phase plans use relative effort (S/M/L), confidence and external dependencies.
A calendar baseline can be set after P01 feasibility and access decisions;
there is no trustworthy full-delivery date from documentation alone.

The main external risks are safe Claude control/correlation, real
Windows/macOS GUI/service test access, .NET 11/MCP/SQLite AOT compatibility,
supported lead-host wake, attached-session native protocols, connector contract
availability, and release signing/distribution decisions. CPU time or number of
agents does not remove these dependencies.

Optimize time-to-feedback through the labelled P03 demonstration and parallel
reviews. Do not pad the critical path with an unnecessary dashboard, generic
framework, exhaustive low-value tests, or a rewrite of existing agent CLIs.

## 9. Mapping from earlier milestone names

Old documents and test reports may still mention M0–M5. They remain historical
scope labels, not a second execution roadmap:

| Earlier label | Covered by new phases |
| --- | --- |
| M0 contracts/spikes | P01 and early P02/P03 experiments. |
| M1 local/full PoC | P02–P03 plus the relevant P05 production-context platform gates. |
| M2 hardened core | P02/P04 implementation and cumulative P07 qualification. |
| M3 local MVP/distribution | P04–P05 with a scoped release decision; full release remains P08. |
| M4 optional connector | P06 connector work, preserving local-only operation. |
| M5 expanded teams/options | Required full-baseline nesting in P04; managed Pi in P03, attachment/additional hosts in P06; remaining options only by explicit change decision. |

Earlier review approval of M0 investigation is not approval of these new full-
product phase plans. Current implementation/evidence status must be read from
the actual reports and reviews, not inferred from a phase title or checklist.
