# M0 spike: durable acceptance through MCP, IPC, SQLite, and Native AOT

## 1. Status and decision

**Plan only. No implementation, test pass, or AOT compatibility is claimed.**
This is the next bounded M0 spike after the interactive-control investigation,
not a replacement for it and not the full M1 PoC.

The user requested architecture documentation first, then this plan. Follow the
[three-project vertical-slice architecture](../architecture.md) and
[contributor policy](../../AGENTS.md). Independent plan review is required before
implementation because this slice establishes persistence, authorization, and
process-lifetime contracts. The earlier exemption applied only to the first
interactive spike. Code review later uses the opposite model family.

**Question:** Can a published C#/.NET Native AOT executable, running as separate
daemon and MCP-bridge processes, durably accept one fake-backend job, keep it
running when the bridge is killed, and return its committed result through a
new bridge, without duplicate execution after a lost response?

Success permits continued core development. It does not approve real backend
support, production service installation, or the whole M0/M1 gate.

## 2. Scope

### Implement in this spike

- Exactly three runtime projects: **Host → Business → DAL**, plus a lean test
  project. Feature-first vertical slices, not Clean Architecture: Business calls
  concrete feature-specific DAL operations; DAL does not reference Business.
  No mandatory repository-port inversion or extra Domain/Contracts project.
  Target .NET 11 RC1, C# only; verify installed-ref-pack and package compatibility
  against the sourced candidate SDK. Minimal shell command glue is acceptable.
- One independently started daemon, one private local state directory, one
  fixed test team/agent, and one bound local operator principal.
- Linux reference transport: Unix domain socket; bounded, versioned JSON frames.
- A thin stdio MCP bridge with SDK handshake, tool discovery, `job_submit`, and
  `job_get`. These are spike tool names, not the final public API commitment.
- Real SQLite transactions for acceptance, pre-effect attempt-start, and
  completion. Caller-supplied idempotency keys and durable results/events.
- One dispatcher, one active fake run, and a bounded persisted queue. Accepted
  work is driven by the daemon without a connected MCP client.
- A deterministic fake backend running as a real child process, with explicit
  synchronization barriers and correlated acknowledgment/completion messages.
- Recovery that dispatches unattempted work and quarantines uncertain started
  attempts as `needs_reconciliation`, without blind retry or PID-based killing.
- JIT tests plus a published Native AOT smoke/crash scenario on the tested Linux
  architecture. Record Windows/macOS as unrun unless actually exercised there.

### Keep out of this spike

- Real Claude/Codex adapters, terminal launch, human input, approvals, and wake.
- Setup screens, service installation, autostart, system configuration changes,
  or publishing/distribution. The daemon is started independently by the test
  harness, not installed as a service.
- General team/child delegation, multi-client consumer fencing, event read/ack,
  interrupt/agent-stop tools, and operator reconciliation commands. Preserve
  their future contracts; never fake support for them.
- Transparent adoption of orphaned backend processes or exactly-once external
  side effects. Result retrieval is by job ID, not an event-delivery guarantee.
- External orchestrator connector, network/cloud access, a web UI, EF Core, or
  a broker.
- Comparative performance claims, full stress benchmarks, and production-grade
  migration/backup. Do record basic published size and startup observations.

These are bounded spike omissions, not removals from the
[full PoC](../poc.md). A missing capability is explicit, not a successful stub.

## 3. Preconditions and file ownership

1. Read the architecture, transaction/recovery rules in `docs/plan.md`, and the
   first interactive spike's available evidence. Separate observations from
   approved contracts; do not copy its incidental transport implementation.
2. Obtain independent plan review and implementation authorization. Git
   initialization/publication remains a separate explicit decision. If Git is
   authorized, initialize the base first, then use a branch/dedicated worktree.
3. Record OS/architecture, SDK, native compiler/linker, and SQLite native asset
   requirements. [RC1 research](../research/net11-process-api.md) confirms
   candidate SDK `11.0.100-rc.1.26425.128` from the public release manifest.
   After approved provisioning, pin it and target `net11.0`; verify the actual
   installed reference pack because release-note examples cite a different build.
   Existing .NET 10 evidence is version-specific, not .NET 11 compatibility.
   Do not retarget the concurrent experiment or install an SDK as a docs change.
4. Resolve package versions from the actual feed when implementing, record/pin
   them, and evaluate MCP registration, JSON generation, and SQLite under AOT.
   Do not invent version numbers or hide trim warnings. Missing native build
   prerequisites are blockers, not permission for system installation.
5. No real model calls are required. Use disposable private directories, safe
   fixed fake tasks, deadlines, and cleanup of only proven test-owned processes.

Proposed isolated implementation location:

```text
spikes/m0-durable-core/
  AgentTeamForge.Spike.slnx
  global.json
  Directory.Build.props
  src/
    AgentTeamForge.Business/
    AgentTeamForge.DAL/
    AgentTeamForge.Host/
  tests/AgentTeamForge.Tests/
  scripts/verify.sh
  scripts/published-smoke.sh
  README.md
  evidence/                  normalized run manifests and bounded logs
```

Use the feature organization from `docs/architecture.md`, not separate
horizontal implementation phases. All three runtime projects stay inside this
isolated solution; do not create duplicate product projects at repository root.

The Host executable supplies spike-only `daemon`, `mcp`, and `fake-backend` modes.
Fake mode is selected explicitly in the spike profile, never a fallback from a
real backend. Test controls are available only with an explicit test profile
and private control channel; they must not become public MCP tools. These
experiment modes are not an approved production command surface.

Do not edit the separately owned `spikes/m0-interactive/` experiment or unrelated
repositories while implementing this plan. Promote useful code to a production
solution only through a later reviewed change, rather than maintaining two
permanent copies.

### .NET 11 process API choices to prove

Use the research as a candidate list, not a reason to expand this Linux slice
into a general process library. Compile and exercise the selected direct-child
`SafeProcessHandle` lifecycle/exit APIs against the pinned SDK and published AOT
binary. Exit status stays diagnostic evidence, never backend-turn completion.
Retain the ordinary `Process` API if it better fits bounded redirected streams;
record the choice rather than forcing every new API into the spike.

Do not use `StartAndForget` for managed runs or rely on lookup helpers to prove
ownership. Capture helpers have no output byte cap, so the fake adapter keeps
bounded draining. `StartSuspended` is Windows/macOS-only and is deferred to those
platform spikes, not silently simulated on Linux. Parent-exit killing or signal
escalation requires an explicit accepted policy; no new default is implied.

## 4. Minimal contracts

### Process and security contract

- The test supervisor starts the daemon and bridges as separate children;
  neither daemon nor fake backend belongs to the bridge's kill scope.
- Only the daemon opens SQLite. Bridge mode composes transport clients, not DAL
  services, even though both modes use the same executable.
- Hold an OS advisory lock on an open file descriptor in the private state
  directory, automatically released on process death; a PID/existence file is
  not a lock. Acquire it before opening/migrating the DB or unlinking/binding
  the socket. A losing daemon exits without touching the endpoint, DB or lock
  file. Verify immediate restart after SIGKILL in an existing crash scenario.
- Use an owner-private runtime/state directory and socket, reject unsafe
  ownership/symlink cases, and cap inbound frame size and read time.
- Provision one private operator credential for the test profile. Load it from
  a permission-checked file; pass only its nonsecret path when necessary. The
  bridge sends the credential in the private handshake; never argv, stdout,
  logs, exceptions, or saved evidence. The daemon binds one preconfigured
  principal/team/agent and rejects client-supplied identity changes.
- This tests a trusted local operator profile, **not** the later child-capability
  bootstrap or isolation against an agent with the same user's filesystem access.
  Do not claim full C24/C25 or production authentication from this result.
- Daemon lifetime tokens control background work. Client request/disconnect
  tokens may stop waiting for a reply, but must not cancel a committed job.

Set small named limits before tests: frame/prompt/result size, queue length,
maximum fake runtime, busy timeout, and scenario deadline. Values belong in one
spike configuration and evidence manifest, not duplicated magic constants.
Queue admission and acceptance must be atomic, including concurrent submissions.

### Job acceptance and idempotency

`job_submit` accepts an idempotency key and a bounded fake instruction/options.
The target and identity come from the configured profile, not arbitrary IDs.
Return job ID and acceptance state only after a successful commit.

Scope the durable key to principal + team + operation. A stable fingerprint of
validated semantic fields includes target, instruction, and execution options;
do not hash arbitrary JSON property order or include transport request IDs.
Same key/same meaning returns the same job; same key/different meaning returns
a structured conflict. No duplicate dispatch intent is inserted under races.
A retry after completion still resolves to the original job/result. Disable
key pruning in the spike; retention policy belongs to a later slice.

After bridge death, callers can use the same key to recover the job ID even if
the acceptance response was never delivered. Business authorization applies to
both `job_submit` and `job_get`.

### Small storage schema

| Record | Purpose |
| --- | --- |
| `schema_migrations` | Known version; refuse unknown newer schema without mutation. |
| `jobs` | Identity, principal/team/target, semantic payload, accepted policy, scoped unique key/fingerprint, status. The idempotency record can live here. |
| `dispatch_intents` | One unique queued/unattempted intent per accepted job; separate from an attempted external effect. |
| `runs` | Attempt generation, correlation, attempt-start state, backend evidence, final result/error. |
| `events` | Durable ordered acceptance/completion notices; consumer read/ack API is deferred. |

Use real on-disk SQLite, WAL, foreign keys, `synchronous=FULL`, short
transactions, and bounded busy behavior. Schema and startup checks must not
silently create empty replacement state after a database error. Accept-or-get,
begin-attempt and completion use short `BEGIN IMMEDIATE` write transactions.
Lookup, queue-capacity check and writes are inside the same acceptance transaction;
the scoped-key unique constraint remains the final arbiter. Never opt into a
deferred read-to-write upgrade or a separate capacity read. Busy/locked exhaustion
returns a stable retryable error with no acceptance, never fabricated success.

The feature-specific DAL API exposes these atomic operations directly to
Business, with names finalized in code. Keep its records and storage outcomes
in DAL; do not invent Business-owned repository interfaces:

1. Accept-or-get: job/key/fingerprint + unattempted intent (+ acceptance event)
   in one commit, or return existing/conflict/capacity failure.
2. Begin-attempt: conditionally claim an unattempted intent and store generation
   and correlation before spawning the fake process or writing its instruction.
3. Complete: require matching job/run/generation/correlation and permitted state;
   persist result + terminal status + completion event in one commit.
4. Get-result and inspect-recovery-candidates: read only; no implicit resend.

Do not expose a transaction handle to Host/Business or split acceptance into
multiple repository commits. Return stable machine-readable errors; tests need
not assert their explanatory English text.

### Fake backend and recovery

The fake backend is an actual process, not just an in-memory mock. It reads a
framed request and emits a correlated ack, then a distinct final result. Give
the test supervisor deterministic control over barriers before/after each
meaningful durable boundary. Test-control channels are separate from output;
readiness is explicit and waits are bounded. Continuously drain redirected
stdout/stderr within limits even without a client.

A tiny test-owned receipt/trace records observed fake invocations so tests can
detect duplicate execution. It is evidence for the test supervisor, not a
replacement database or a production result/adoption protocol.

On restart:

- A committed job with no attempt marker is eligible for one dispatch.
- A started attempt with no committed authoritative completion is uncertain.
  Mark it `needs_reconciliation`; do not spawn a replacement.
- A committed completed run stays completed and readable.
- A live or possibly surviving fake child does not authorize PID-only adoption
  or termination. Test cleanup uses process handles/verified provenance held by
  the supervisor. Cleanup is not evidence that product recovery can adopt or
  safely stop an arbitrary orphan.

Generation and correlation checks fence late evidence. Correlated fake
completion proves only the fake protocol contract; real interactive completion
still depends on the first spike's evidence. EOF, malformed output, ack, or a
missing child never counts as successful completion.

While the daemon is alive, EOF before result, malformed/oversized output, child
exit without correlated result and deadline expiry after attempt-start atomically
transition the matching run to non-retryable `needs_reconciliation`, retaining
its intent history and ownership uncertainty. No failure re-creates a dispatch
intent. This spike conservatively avoids a separate retryable failed state.
On deadline, it may terminate only its own direct fake child through the held
process handle (explicit spike-only policy), then record the same uncertain
outcome. Test EOF-before-result and deadline expiry, not just successful output.
If a final-state write fails, stop claiming new intents until recovery; do not
continue dispatch with unrecorded work. Unknown and other-principal job IDs both
return the same not-found outcome.

## 5. Vertical implementation slices

Work red → green → refactor within each slice; do not batch all tests last.

### S1 — one durable accepted job through the real boundaries

- Build the minimal three-project solution and one test project.
- Start with a failing real-SQLite acceptance test, then implement the Business
  operation, DAL transaction, and Host mapping together.
- Add the actual MCP SDK handshake/tool registration and local IPC path. A
  tools/list response alone is not sufficient: invoke `job_submit`, reconnect,
  and invoke `job_get` through the bridge.
- Establish private identity binding, protocol bounds, single-instance lock,
  and unknown-schema refusal before accepting work.
- Publish AOT early and exercise the same call path. If registration or SQLite
  native loading fails, stop here with evidence rather than build a JIT-only
  subsystem and label it AOT-ready.

Exit: one committed acceptance is observable through a fresh process, and no
acceptance is returned on a failed transaction.

### S2 — dispatch and completion without a client

- Red tests for attempt-start-before-effect and correlated final completion.
- Implement the Business dispatcher, DAL attempt/completion operations, thin
  hosted-service wrapper, and explicit fake-process adapter.
- Test a queued job with no connected client and a running job after bridge
  termination. Release the fake barrier through the supervisor; fetch the
  committed result through a new bridge.
- Include mismatched/stale completion and EOF; neither falsely completes work.

Exit: dispatcher and fake run progress independently of bridge lifetime; stored
completion survives reconnection and a subsequent daemon restart.

### S3 — lost response and crash boundaries

- Red tests for repeated/concurrent acceptance and conflicting keys.
- Use deterministic barriers and real abrupt termination before acceptance
  commit, after commit/before response, and after attempt-start commit/before
  external effect. Exercise receipt-with-lost-ack as the same uncertainty policy.
- Restart against the same DB. Prove safe dispatch of unattempted work, no
  duplicate accepted job, and quarantine of an uncertain attempt.
- Inject completion-write failure and prove no partial completed state/event;
  return uncertainty rather than silently dispatching again.
- Check DB write/lock failure, second-daemon refusal, bad identity/version,
  oversize input, and newer-schema refusal with a small set of focused tests.

Exit: each tested failure window has saved outcome evidence and no blind retry.

### S4 — published scenario and handoff

- Run the principal acceptance → bridge kill → completion → reconnect scenario
  with published AOT daemon, bridge, and fake-backend modes, not `dotnet run`.
- Repeat lost-response and attempt-start uncertainty on the published binary.
- Save exact commands, versions, commit/content identity if available, test
  results, artifact size, and any justified analyzer limitations.
- Obtain opposite-family code review, address blocking findings, re-review fixes,
  then rerun applicable gates. Record unrun platform and full-PoC tests explicitly.

Exit: bounded report supports continue, rework, or blocked; it does not claim
full M0/M1 completion.

## 6. Lean verification matrix

Group tests by business invariant, not one test per method or sentence. The
existing [PoC matrix](../poc.md#6-automated-failure-and-contract-matrix) remains
the full contract; only the coverage below is commissioned here.

| Invariant / scenario | Essential assertions | Related PoC cases and limits |
| --- | --- | --- |
| Durable idempotent acceptance | Same identity/intent after retry and concurrent equal requests; conflicting payload has no side effect; Unicode/newlines preserved. | C01–C03, C27 fake transport only. |
| Client-independent lifetime | Killing bridge does not kill daemon/fake; queued work also starts with no client; fresh bridge retrieves stored result. | C04–C05 with fake, not L03. |
| Safe acceptance crash boundary | Before commit: no accepted job. After commit/no attempt: retry returns same job and one attempt may dispatch. | C06–C07 using actual abrupt process death. |
| Uncertain attempt is not replayed | After attempt-start or lost backend ack: no automatic replacement; status is `needs_reconciliation`. | C08–C09 fake evidence; no terminal-launch proof. |
| Atomic correlated completion | Result/status/event are atomic; stale/mismatched evidence, ack-only, EOF, and failed write cannot report completion. | C10, C13, C17 at fake-protocol scope. |
| Fail-closed persistence and ownership | Busy/write failure yields no false acceptance/effect; second daemon cannot take over endpoint; newer schema unchanged. | Selected C19 paths, C26, C30. Disk-full/corruption/power-loss coverage is not implied. |
| Private bounded API | Wrong credential/identity/version and excess sizes/capacity rejected before writes; no credential in emitted logs/responses/argv. | Selected C20/C24/C25/C29 paths; not general teams or child bootstrap. |
| Published equivalence | Real MCP initialize/tools/list/call; accept/result path plus client-kill and uncertain-attempt scenarios run in AOT. | AOT/packaging gate on actual OS/architecture only. |

Prefer temporary real SQLite databases to a duplicate mock suite. Use a fake
storage failure only where needed for a precise write-failure boundary, and do
not present injected failure as proof of actual disk-full behavior. Fixtures
share barriers and cleanup, not hidden sleeps. No assertion on prose labels,
ordinary logs, folder layout, constructor style, or arbitrary coverage target.

## 7. Planned commands and evidence

These are **future commands against the proposed layout**, not commands already
available or executed. Implementation supplies the two shell wrappers with
bounded cleanup and explicit exit codes; their orchestration/test logic stays
in C# rather than a second Python/Node harness.

From `spikes/m0-durable-core/` after scaffolding:

```bash
dotnet --info
dotnet restore AgentTeamForge.Spike.slnx
dotnet format AgentTeamForge.Spike.slnx --verify-no-changes
dotnet build AgentTeamForge.Spike.slnx -c Release --no-restore -warnaserror
dotnet test AgentTeamForge.Spike.slnx -c Release --no-build
dotnet publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -p:TreatWarningsAsErrors=true \
  -o artifacts/linux-x64
./scripts/published-smoke.sh artifacts/linux-x64/AgentTeamForge.Host
./scripts/verify.sh
```

`linux-x64` is the reference example; confirm host architecture before running.
Enable trim/AOT analyzers early and treat their diagnostics as gates. Do not
suppress broad warning categories to obtain a pass. Any unavoidable narrow
suppression needs written justification and a published test of that exact path.
A single-file JIT publish is not an acceptable substitute.

`verify.sh` should run the applicable format/build/test/published gates and
repository document checks without quietly filtering out failing tests.
`published-smoke.sh` should invoke the C# process scenarios against the supplied
binary, using temporary private state and no real agent credentials/model calls.
If dependencies/tooling are unavailable, report blocked commands; do not install
system components or change security controls without permission.

Evidence output:

- `README.md`: commands, known limits, deterministic barrier descriptions.
- `evidence/report.md`: scope, go/no-go, gate results, opposite-family review
  disposition, and explicit unrun cases/platforms.
- Environment/package manifest and normalized scenario logs with job/run IDs,
  observed transitions, process provenance, and fake invocation counts.
- Build/test/AOT diagnostics and checksums for the tested published artifacts.
  Keep raw databases, secret files, binary build outputs, and unbounded logs out
  of committed evidence; export sanitized state summaries instead.

## 8. Go/no-go and follow-on work

**Continue this core approach** only if the published chain works, accepted work
survives bridge death, the durable/uncertain boundary is demonstrated, no unsafe
redelivery occurs, and independent code review has no unresolved blockers.

**Rework or stop** if AOT requires a large undocumented workaround, the bridge
must own execution, persistence cannot atomically express acceptance/attempts,
or the design needs to guess whether an uncertain effect happened.

Missing platform access or native toolchain yields **blocked**, not a pass.
Process-crash evidence does not prove power-loss behavior. Fake-backend success
does not establish interactive support, permissions, human-input reconciliation,
or a guaranteed hard budget/cancel policy.

After both M0 spike reports are available:

1. Compare actual interactive requirements with the Business execution features
   and the small real/fake backend boundary.
2. Review deviations and explicit ADRs before promoting code or starting M1.
3. Plan the next vertical capability: authenticated team/agent binding and
   result/event retrieval with fenced read/ack, then real adapters and separate
   interrupt/stop/reconciliation under their verified contracts.
4. Keep Windows/macOS, setup/provider choice, approvals, outage replay, wake, and
   performance gates on the roadmap. Do not silently call them covered by this
   small Linux fake-backend experiment.
