# AgentTeamForge architecture

## 1. Decision and status

**Feature-first vertical slices, with three pragmatic .NET projects:**

```text
AgentTeamForge.Host → AgentTeamForge.Business → AgentTeamForge.DAL
```

This is **not Clean Architecture**. Do not add inward-facing repository ports,
Domain/Application/Infrastructure assemblies, or layers of mappings to satisfy
an architectural template. Business can depend directly on DAL. Features are
the unit of implementation and navigation; projects separate responsibilities,
not development phases.

This is the selected target structure, not a claim that it is implemented or
runtime-verified. It replaces the earlier many-project proposal in
[the implementation plan](plan.md). Native AOT, process durability, and platform
support still require evidence.

### Vision

Build a small local-first engine for coding agent teams. A separate .NET daemon
owns accepted work independently of the lead agent or MCP bridge. SQLite holds
durable structured state. Claude Code, Codex and Pi remain external agent programs, not
model loops implemented here. An external orchestrator may connect through an
optional future integration.

Human-facing surfaces are service setup/diagnostics and the newly requested
small **text-only operator web console** (F27): status/output, human follow-up
and confirmed agent stop. A plan and static HTML mockup precede implementation;
there is no working web application yet. **Host**, rather than UI, remains the
project name because it also contains CLI entry points, MCP, IPC, composition
and daemon lifecycle. The console reuses authenticated application contracts,
not a second scheduler or direct DB access. The agents' real terminal TUIs stay
external applications; the console is not their terminal emulator.

## 2. Vertical slices come first

Implement one observable behavior through the stack, including its meaningful
tests. For example, “accept a job durably” includes its MCP/IPC entry point,
Business rules, DAL transaction, and failure checks in one change. Do not build
all repositories, then all services, then all endpoints.

Keep each feature's behavior and helpers together. A feature may span the three
projects, with matching names for navigation, but should touch only the layers
it needs. Do not force a handler/interface/validator/mapper/repository/event for
every method. A small feature can be one class with colocated request/result
records. Add interfaces for actual substitution needs, such as real/fake backend
control, not automatically for every concrete class.

Illustrative layout; it is not a file-creation checklist:

```text
src/
  AgentTeamForge.Host/
    Features/
      Jobs/                  MCP tool + IPC endpoint mapping for submit/get
      Setup/                 launch-mode prompts, installation, doctor commands
      Diagnostics/           status and diagnostic presentation
    Hosting/                 daemon lifetime and single-instance ownership
    Transport/               genuinely shared IPC/MCP framing and clients
    Composition/             explicit role-specific registrations
    Program.cs
  AgentTeamForge.Business/
    Features/
      Jobs/
        AcceptJob.cs          validation, authorization, idempotency meaning
        DispatchJob.cs        scheduling and pre-effect attempt policy
        CompleteJob.cs        correlated evidence and final transition
        GetJob.cs             authorized result lookup
      Agents/
        StartAgent.cs
        InterruptTurn.cs
        StopAgent.cs
        Backends/             verified Claude/Codex/Pi control adapters
        Terminals/            Herdr/Windows/macOS launch and binding
      Recovery/               reconcile evidence and physical ownership
      Setup/                 setup rules and validated mode/provider choice
    Runtime/                 proven shared process/clock helpers, only as needed
  AgentTeamForge.DAL/
    Features/
      Jobs/                  job records, parameterized queries, atomic writes
      Agents/                persisted agent/session bindings
      Setup/                 stored launch configuration
    Sqlite/                  connections and common transaction mechanics
    Migrations/
tests/
  AgentTeamForge.Tests/
    Features/Jobs/            rules and real SQLite behavior
    Features/Recovery/
    Scenarios/               process crash and published-binary paths
```

Feature-specific models, result records, interfaces, and helpers belong beside
their callers/implementations. Do not create global `Interfaces/`, `Dtos/`,
`Validators/`, or `Repositories/` directories that scatter every change across
the solution. Promote something to a shared location only when multiple actual
features need it. Shared invariants are useful; a generic base handler usually
is not.

## 3. Three projects and dependency rules

### Host

Owns entry points and process composition:

- CLI, setup/installer presentation, status, and doctor commands.
- Stdio MCP tools, local IPC server/client, wire framing and serialization.
- Startup, daemon lifecycle, service-manager integration, and identity bootstrap.
- Thin background-service wrappers that invoke Business operations.

Host parses input, establishes transport identity, delegates, and maps results.
It does not decide job state transitions, retry uncertain prompts, or run SQL.
The setup screen gathers the explicit launch-mode choice; Business validates
it and DAL persists it. Full installer implementation remains M3.

Host references Business. A direct reference to DAL is permitted **only for
composition/startup registration** if needed; feature entry points must not
bypass Business to access storage. In bridge/client mode, do not initialize
writable DAL even if its assembly is present in the executable package.

### Business

Owns feature behavior and orchestration:

- Job/team/agent rules, state transitions, authorization, idempotency meaning,
  budget/permission policy, scheduling, and recovery decisions.
- The feature services that call DAL directly. A concrete feature-specific DAL
  store/query is sufficient; there is no mandatory Business-owned storage port.
- Backend and terminal integrations colocated with agent execution features.
  Keep protocol/OS mechanics distinct from the feature's policy, but do not
  introduce another project or generic adapter framework for that distinction.
- Capability/evidence interpretation, foreign/human-activity rules, and separate
  turn interruption versus stopping an agent.

Business references DAL, not Host. It does not contain MCP handlers, IPC server
framing, CLI prompts, or service installation screens. Low-level implementation
helpers may use .NET libraries directly; this is not a “framework-free core”
requirement. An interface is justified when it isolates a real external boundary
or permits a useful fake, not to invert the entire dependency graph.

### DAL

Owns data representation and persistence:

- SQLite connections, schema/migrations, feature-specific storage records,
  parameterized SQL, constraints, and row mapping.
- Atomic acceptance, attempt-start, and completion operations required by the
  feature's durability contract.
- Idempotency keys/fingerprints, dispatch intents, generations, results/events,
  later consumer cursors, and local artifact metadata as needed.
- Bounded busy handling, storage failures, and schema compatibility checks.

DAL references neither Business nor Host. Its storage API and records live in
DAL and are consumed by Business; do not move repository interfaces upward just
to create dependency inversion. Small DTO/record reuse inside the process is
fine. Map only where security, protocol versioning, or a different meaning makes
it necessary; never serialize a whole database record onto the public API by
accident.

DAL does not launch agents or decide whether a prompt can be replayed. SQLite
constraints enforce invariants but do not replace Business authorization and
policy. Prefer `Microsoft.Data.Sqlite` and explicit SQL initially; neither EF
Core nor a generic CRUD repository is required.

### Project count

Keep **three production projects**. Tests and temporary experiment executables
are not extra production layers. An isolated feasibility harness can remain
small; promotion into product code follows these boundaries. A fourth product
assembly requires a demonstrated need and explicit decision, not a folder that
could theoretically become a library.

## 4. Design rationale

These choices follow the local runtime's requirements, without depending on
another project's architecture or source code:

| Requirement | AgentTeamForge choice |
| --- | --- |
| Small, maintainable implementation | Feature-first Host → Business → DAL; no mandatory dependency inversion or additional domain layers. |
| Cohesive use cases and durable transactions | Business owns feature behavior and calls DAL directly; DAL owns SQLite transaction mechanics. |
| Reusable behavior across entry points | Thin Host endpoints invoke Business. Separate bridge processes use IPC because an actual process boundary exists. |
| Cheap, reconnectable MCP clients | A thin stdio-to-local-IPC bridge, with no credentials in argv; prove tool registration under Native AOT. |
| Work survives client loss | Daemon-owned dispatch and verified process/session ownership; Host wires lifecycle, Business owns execution policy. |
| Local recovery without a central service | No mandatory remote lease or account. Remote-job ownership rules belong to the later opt-in connector. |
| Atomic structured state with bounded storage | SQLite for durable records; files for artifacts, logs, workspaces, and backend session records. |
| Honest distribution and resource claims | Measure JIT and Native AOT directly; single-file publication is not evidence of AOT compatibility or memory savings. |

Do not add a web framework, server database, tenant/fleet/billing concepts, or
central execution ownership to satisfy an optional connector. Keep tests
risk-driven rather than adopting a numerical coverage quota. Any future code
reuse requires a separate license and dependency assessment.

## 5. One executable, separate lifetimes

Three projects do not mean three processes. Host can be published once and
started in separate roles:

```text
lead agent → [Host: mcp] ───── local IPC ───────┐
operator   → [Host: CLI] ───── local IPC ───────┤
                                              ▼
                                      [Host: daemon]
                                       Business features
                                       DAL / SQLite
                                              │
                                  real Claude/Codex/Pi processes
                                  in selected terminal or headless
```

The binary name `atf` and command names remain provisional. Same binary does
not imply shared lifetime or an in-process daemon inside the MCP bridge.

- Only daemon mode opens the job DB for normal runtime operations. MCP bridges
  and ordinary operator clients use IPC, never direct database access.
- Start the daemon outside the lead/bridge process tree. Client cancellation
  must not cancel a committed job. Daemon lifetime and accepted policy govern it.
- Host checks transport identity; Business authorizes the bound principal and
  action. Do not trust role/team names supplied in JSON.
- MCP stdout contains protocol only; diagnostics use stderr. Register services
  explicitly per role rather than booting the daemon in every bridge process.
- If the interactive spike requires a child-side helper or desktop-session
  launcher, it may use a restricted Host mode. It must not own another durable
  scheduler or receive unrestricted operator authority.

### Native wake boundary

Native session wake is standard, following the public
[PR #70 design](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70).
Host owns host-local notifier wiring and authenticated IPC; Business owns scoped
registration, native transport selection, coalescing/backoff and capability rules;
DAL persists durable messages, delivery state and generation-bound registrations
through the daemon. A host-local notifier never opens the runtime database or
becomes a second scheduler.

Commit before wake. Send a short unread-work notice, then let the recipient fetch
through authenticated read/ack. Wake success is not message acknowledgment or
model execution. Validate native host identity, fence stale registrations and
scrub inherited wake credentials/handles on child launch. Missing Windows/macOS/Pi
transport proof blocks promised support; manual catch-up is explicit degraded
recovery, not normal watcher/model polling. See [cross-phase contracts](planning/full-product/contracts.md#4-host-wake-and-backend-support-are-different).

## 6. Transactions without architecture ceremony

Business decides whether an operation is allowed and invokes the corresponding
atomic DAL method. DAL owns the transaction and returns a concrete outcome.
Keep this call chain short; no generic unit-of-work/mediator stack is required.

1. **Accept:** atomically store job, scoped idempotency key/fingerprint, and
   unattempted dispatch intent; acknowledge only after commit.
2. **Begin attempt:** conditionally store generation/correlation and attempt-start
   before any process spawn, terminal launch, or prompt delivery.
3. **External I/O:** perform backend work outside the DB transaction.
   Acknowledgment does not mean completion.
4. **Complete:** Business checks authoritative evidence; DAL atomically stores
   terminal state, result, and completion event with expected-generation and
   allowed-state conditions.
5. **Recover:** dispatch unattempted queued work; uncertain started attempts
   require reconciliation rather than automatic retry.

Do not replace atomic operations with several separately committed CRUD calls.
Business defines the semantic payload fingerprint; DAL enforces uniqueness and
compares/returns existing results under concurrent requests. WAL, foreign keys,
short transactions, bounded busy handling, and preliminary `synchronous=FULL`
remain required. Process-kill tests do not prove hardware power-loss durability.

A stale generation cannot update the current run, but DB fencing does not stop
an old process writing files. Keep the full [recovery contract](plan.md#6-delivery-idempotency-and-recovery)
and [terminal ownership rules](terminal-modes.md#5-ownership-and-crash-rules):
no PID-only adoption, no automatic termination of live interactive TUIs, no
invented completion, and no replay of uncertain prompts.

## 7. Lean TDD and review

A slice starts with a failing test for meaningful behavior, then minimal code
and refactoring. Concentrate on:

- Business rules for transitions, authorization, correlation, and uncertainty.
- Real temporary SQLite tests for uniqueness, atomicity, and persistence. A
  mocked store cannot prove commit-before-acknowledgment.
- Few deterministic process scenarios for bridge/daemon death and AOT behavior;
  barriers and abrupt termination, not sleep-only synchronization.
- Exact protocol/security values where they are part of the contract.

Avoid assertions on prose, installer labels, log wording, private helpers,
constructor style, or class counts. No numerical coverage target. One meaningful
scenario can cover several failure-matrix rows without duplicating tests.

Check project references cheaply; review for feature locality and thin Host
entry points. Do not build a large reflection test suite to police folder names.

[AGENTS.md](../AGENTS.md) defines reviews. The next durable-core spike needs
independent plan review before implementation because it introduces persistence
and lifetime contracts. The first interactive spike's explicit exemption does
not extend to later work. Implementations require opposite-family code review.

## 8. AOT and next step

Runtime code, spike harnesses, and tests are C#/.NET. Use explicit registrations
and source-generated JSON where required by AOT. Verify the actual package APIs
under trimming and the published runtime. Native SQLite packaging and external
agent CLIs remain visible dependencies.

No dashboard, custom model loop, general plugin platform, real external
orchestrator connector, or full installer belongs in the next experiment. The fake-backend
slice tests durability without model calls; it never proves interactive or
Windows/macOS support. Both M0 investigations remain necessary.

Next: [durable-core spike plan](spikes/m0-durable-core-plan.md).
