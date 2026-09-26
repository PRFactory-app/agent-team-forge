# AgentTeamForge — PoC specification

## 1. What the PoC must decide

**Can a small .NET/AOT daemon own a local Claude/Codex team, let a lead agent
control it through a thin MCP bridge, and recover honestly after disconnection
or a crash, without relying on custom cross-file transaction machinery?**

The PoC is a limited basis for a technical decision, not the first product
release. Every feature and result in this document remains planned until there
is evidence.

Three main questions:

1. Do the process and delivery contracts hold under real failures?
2. Can the desired backend control be built on sufficiently stable protocols in
   both real interactive and explicitly selected headless modes?
3. Does the architecture simplify operation measurably without unreasonable
   resource cost?

The fixed setup and terminal requirement is in
[terminal-modes.md](terminal-modes.md). A visible log tail of a headless agent
does not count as interactive mode.

## 2. In scope

| Area | Minimum PoC deliverable |
| --- | --- |
| Daemon | Standalone process, one user, one active daemon, private local IPC. |
| Team | One team with a lead agent and at most two concurrent managed children. |
| Backends | Claude and Codex: launch, result, follow-up, turn interruption, and separate agent stop in the selected mode. |
| Setup choice | Minimal setup with a mandatory, persisted interactive/headless choice; no silent fallback. |
| Terminals | Herdr on Linux, a visible Windows tab, and macOS equivalent; real agent TUI and separate control path. |
| MCP | Thin stdio bridge with a small tool set and reconnection. |
| Persistence | SQLite for agents/jobs/runs, idempotency, delivery status, and results/events. |
| Job queue | Durable acceptance, autonomous dispatch, and one active turn per conversation. |
| Output | Continuous draining, bounded buffers, compact result, and log reference. |
| Recovery | Lost response, client crash, daemon crash, and backend failure. Uncertainty is exposed. |
| Authorization | Private endpoint, explicit team/agent identity, no arbitrary identity switches. |
| Policy | Concurrency limit, queue limit, runtime timeout, separate turn interrupt/agent stop, and approval blocking. |
| AOT | Daemon and bridge on Linux x64, Windows x64, and one selected macOS architecture in the platform spikes. |
| Measurement | Reproducible baseline, fake-backend measurements, and small real agent experiments. |
| Wake spike | Separate test of waking an idle Claude lead on Linux through the host's own bridge. |
| Connector seam | In-process fake external command source using the same API, without network access or a real external orchestrator. |

### Platform guarantees and separate PoC decisions

- **Linux reference PoC:** full automated matrix and real Claude/Codex turns
  in both Herdr interactive and headless modes. Run applicable T01–T12 and
  T15–T19.
- **Windows spike:** AOT/fake/IPC/process smoke tests in a real Windows
  environment and real interactive Claude/Codex sessions in visible tabs. GUI
  tests require a desktop session; a headless CI runner or cross-build alone is
  insufficient.
- **macOS spike:** select a terminal provider, run real interactive backend
  turns and AOT/fake/IPC smoke tests on the selected architecture. Test
  TCC/automation where needed.
- **Windows/macOS live headless:** recommended early smoke tests, required before
  the respective platform is marketed as supporting both modes in the MVP.

For the full PoC, T04, T07, and T10 run once on each platform with the daemon
started in its intended production context. Temporary systemd user units,
logon tasks, or LaunchAgents are acceptable; building the full installer is not
part of the PoC. The Linux daemon must be outside Herdr and outside the client
process tree terminated by T07.

The Linux reference PoC can receive its own go/no-go decision while a platform
spike is blocked. Its report must then say **Linux reference PoC passed;
three-platform requirement unverified**. The full PoC also requires the Windows
and macOS spikes. Lack of a test machine is a blocking gap, never an implicit
exception to the user's terminal requirement.

## 3. Out of scope

- GitHub publication or production distribution as an automatic consequence of
  the plan.
- Full automatic OS service installation or update mechanism. Minimal setup to
  choose and save the mode is in scope.
- Real external orchestrator connector, remote commands, or upload of work data.
- Pi backend or arbitrary plugin system.
- All previous MCP tools, disk formats, or automatic legacy migration.
- General attachment to already open Desktop/terminal agents.
- A complete custom TUI, web UI, or remote UI. The backend's existing
  interactive TUI and verified binding/reconnection to tabs we launch are in
  scope.
- Unlimited nesting or peer-to-peer topologies between agent teams.
- Seamless survival of an agent process and its I/O through a daemon crash.
- Exactly-once execution of shell commands, code changes, or external API calls.
- Perfect parity between every Claude and Codex feature.
- Firm promises of total token or memory savings before measurement.
- Production-ready backup/restore and schema migration across multiple
  releases. Schema versioning and refusal to open an unknown newer schema are
  still required.

## 4. Proposed work order

### P0 — baseline and contracts

- Select supported installed CLI versions and document authentication mode.
- Record a documented comparison workflow using comparable tasks and permissions.
- Define accepted, backend-acknowledged, completed, and reconciliation states.
- Define IPC schemas, maximum sizes, and identity bootstrap.
- Document how the parent disconnects without canceling jobs.
- Decide how approvals block or are handled for each backend without bypass.
- Establish the setup choice, terminal provider, and backend × mode matrix.
- Decide the policy for human and lead-agent input in the same interactive
  session. The v1 candidate is conservative human-wins: foreign/human activity
  pauses automatic follow-ups until explicit verified-idle reconciliation.
- Define the interactive evidence hierarchy: only run-correlated authoritative
  lifecycle signals or verified durable backend records complete a job; model
  self-report is informational and terminal text/silence is non-authoritative.
- Record a platform × mode × intended production-launch-context matrix for
  `PATH`, home/profile, config, credential store, and authentication, and define
  the setup-doctor checks.

Deliverables: short ADRs under `docs/decisions/` and an experiment manifest
under `evidence/poc/`. Create those directories when experiments actually begin.

### P1 — AOT and backend spikes

Small separate programs before broad product code:

1. AOT binary with MCP handshake/tool call, SQLite transaction, and local IPC.
   Verify whether MCP tool registration is trim/AOT-clean or requires
   source-generated registration.
2. Codex: start a thread/turn, obtain IDs, read the final result, resume, and
   interrupt.
3. Claude: the same flow through the documented CLI streaming contract.
4. For each backend: approval request and disconnection during streaming.
5. Interactive: launch real Claude/Codex TUIs in Herdr and the selected terminal
   on each platform. Verify human input, foreign-activity handling, machine
   follow-up/results, approval visibility, and replay of completion while the
   daemon is unavailable. Keystroke injection is not a supported v1 transport.
6. Run the published binary; merely producing it is insufficient. Do not claim
   that a headless protocol controls a TUI without verification.

Stop expanding scope if a backend only works through an unknown permission
bypass, unverified acknowledgment, or a large undocumented control protocol.

### P2 — vertical slice with a fake backend

- Durable job acceptance and idempotency.
- Start/follow-up/turn-interrupt and separately authorized agent-stop through a
  thin bridge.
- Dispatcher independent of client connections.
- Results/events with explicit read/ack and rereading.
- Simulated approvals, output bursts, failures, and crash windows.
- Local identity control and bounded resources.

The fake backend must be a real small child process with controllable barriers,
not just a mocked interface. This tests pipe EOF, output draining, process
identity, and shutdown as well as domain logic.

### P3 — real backends

Replace the fake with Codex first, then Claude. Keep the same domain contract
and crash tests. Allow adapter-specific capability differences.

Run the Linux flow in both setup modes using separate test profiles. Run all
relevant T01–T12 and T15–T19 against real interactive backends in the platform
spikes.

Use disposable test repositories, small tasks, predetermined permissions, and
a budget. No test prompt should instruct a model to modify the user's real
projects or setup configuration.

### P4 — wake, measurement, and go/no-go

- Run end to end with a real lead agent, not only an MCP test client.
- Run the wake spike separately so manual status reads cannot mask its failure.
- Run the failure matrix and resource measurements against a published AOT
  binary.
- Write a report with an outcome for each criterion, deviations, and the next
  decision.

## 5. Demonstration the user should be able to see

1. Run minimal setup, select interactive Herdr, and start the daemon outside
   both Herdr and the lead's process tree. Connect the lead agent's MCP bridge.
2. Start Claude and Codex in visible Herdr tabs/panes and isolated workspaces.
   Show the real TUI and human input, not just a log view.
3. Observe durable job IDs and both agents entering active turns.
4. Abruptly kill the lead agent and its bridge. The daemon and children must not
   be in the killed client's process group.
5. Let the jobs finish without a connected lead agent.
6. Start a new lead agent/bridge, connect to the same team, and retrieve both
   results.
7. Send a follow-up to one agent and verify the correct backend conversation.
8. Interrupt a new interactive turn and show that the tab/agent remains; then
   separately exercise an authorized agent stop and verify the observed state.
9. Demonstrate a separate daemon crash: restart finds the job and reports its
   correct status or `needs_reconciliation`, never invented success or a blind
   duplicate run.

Repeat the core flow with an explicit headless choice. The Windows/macOS spikes
show the corresponding visible terminal flow with the selected provider. See
T01–T19 in the terminal requirements.

Live tests must record versions, timestamps, and job/run IDs. Evidence must
not include credentials, full sensitive prompts, or private repository results.

## 6. Automated failure and contract matrix

| ID | Test | Expected evidence |
| --- | --- | --- |
| C01 | Accept job, lose response, retry with same key | Same job/outbox identity; at most one attempt per job until authorized reconciliation. |
| C02 | Same key, different payload | Conflict; no extra job or external effect. |
| C03 | Two concurrent identical requests | One durable operation; both receive the same identity. |
| C04 | Client/bridge receives SIGKILL or equivalent | Active fake run continues; final result can be read later. |
| C05 | Disconnect while job is queued | Dispatcher starts it later under accepted policy, without a new client turn. |
| C06 | Crash before commit | No accepted operation; retry may create one. |
| C07 | Crash after acceptance commit, before response; queued outbox is unattempted | Same job is found; no attempt exists, so the dispatcher may atomically start and dispatch one attempt. |
| C08 | Crash after attempt-start transaction, before process spawn/tab launch/prompt acknowledgment | Generation and correlation exist; external effect is uncertain, so reconciliation is required and blind redelivery is forbidden. |
| C09 | Backend receives work but response is lost | Correlation is found or uncertainty is explicit; no automatic duplicate prompt. |
| C10 | Backend finishes, DB final write fails | Recover from authoritative evidence or mark uncertainty. |
| C11 | Read events, lose read response | Same unacknowledged IDs can be read again. |
| C12 | Ack is lost/retried or tries to skip an unread batch | Monotonic, consumer-bound cursor; no skipped events. |
| C13 | Late event from an old generation | Cannot change the current run or falsely complete a new job. |
| C14 | PID reused / start identity differs | Foreign process is never adopted or killed. |
| C15 | Process ownership uncertain after daemon restart | Automatic new run is blocked; a live interactive TUI is not auto-killed. |
| C16 | Verified orphan with shell children | No new run until old work is confirmed stopped. |
| C17 | Backend dies, EOF, or incomplete JSON | Explicit failure/uncertainty; silence does not complete a job. |
| C18 | Output faster than a slow client reads | Agent output is drained; memory bounded; client can reread events. |
| C19 | Disk full, SQLite busy/corrupt, or lost commit | No false acceptance, empty-state fallback, or new dispatch. |
| C20 | Queue/output/limit exceeded | Documented limit error or backpressure, not unbounded growth. |
| C21 | Interrupt before start / during turn / after completion; separately stop agent | Idempotent, honest status; interactive interrupt keeps the tab/agent by default, and stop requires separate authority/policy. |
| C22 | Runtime timeout with and without accepted termination escalation | Verified interrupt/stop outcome; otherwise blocked/unconfirmed, with no hidden survivor or false guaranteed hard cap. |
| C23 | Approval with no connected decision maker | Observable blocked/waiting or explicit unsupported per adapter contract; never fabricated `running` or bypassed. |
| C24 | Different team/agent ID, or child capability used to reattach as lead | Authorization error; no leak, mutation, or role escalation. |
| C25 | Invalid/reused bootstrap nonce, credential, IPC version, or large payload | Reject before side effects with understandable, bounded error; no secret in argv/logs. |
| C26 | Two daemons start against the same state | Second is refused without DB or endpoint takeover. |
| C27 | Prompt with Unicode, newline, and shell metacharacters | Exact prompt bytes/text reach adapter; no shell interpretation. |
| C28 | Fake connector redelivers external command ID | Same local operation through same API; no external orchestrator connection needed. |
| C29 | Secrets in failure and diagnostic paths | Token values absent from logs, argv, and normal tool responses. |
| C30 | Unknown newer DB schema | Startup refused without mutation. |
| C31 | Authorized reconciliation request is lost/retried; outcomes include accept evidence, abandon, or request new attempt | Resolution is idempotent and evidence-bound; physical ownership remains blocked until verified idle/termination, and any new attempt uses a new key. |
| C32 | Lead reconnects while an old bridge for the same team role reads/acks | Cursor follows the durable role principal; reconnect fences to one active generation and stale read/ack is rejected without skipping events. |

Crash points must be deterministic barriers in the fake backend/test host. Use
real abrupt process termination; thrown exceptions or graceful shutdown alone
do not count as crash evidence. Avoid random sleeps as the only synchronization.

Tests requiring privileges or a separate OS user may live in platform
integration, but must be listed as run or explicitly not run.

## 7. Live matrix

| ID | Scenario | Gate |
| --- | --- | --- |
| L01 | Codex first job → final result → follow-up in the same verified session | Linux: Herdr interactive and headless. |
| L02 | Equivalent Claude flow | Linux: Herdr interactive and headless. |
| L03 | Both run; lead agent and bridge crash; new lead retrieves results | Required in both Linux modes, three successful repetitions per mode. |
| L04 | Interrupt active turn; inspect remaining processes and status | Required for both backends in both Linux modes; interactive default preserves the tab/agent. |
| L05 | Approval/disallowed action without an active lead agent | Required for both backends in both Linux modes; blocked state is surfaced or capability is explicitly unsupported. |
| L06 | Daemon crash with active backend, including an interactive completion during outage; reconcile at restart | Required for both backends in both Linux modes; interactive TUI is not auto-killed, replay is verified or uncertainty/unsupported is explicit, and blind retry is forbidden. |
| L07 | Idle Claude lead is woken by bridge when child result is saved | Separate wake spike with its own go/no-go. |
| L08 | Windows published AOT daemon, IPC, fake child, client crash, and recovery | Required for full PoC. |
| L09 | Windows/macOS real headless Codex/Claude start-result-interrupt/stop | Recommended early; required before support for both modes in the MVP. |
| L10 | Windows: real interactive backend tabs, all relevant T01–T12 and T15–T19 | Required for full PoC and Windows support. |
| L11 | macOS: real interactive backend tabs, all relevant T01–T12 and T15–T19 | Required for full PoC and macOS support. |
| L12 | macOS published AOT, IPC, and fake/process smoke | Required for full PoC; specify selected architecture. |

For L07, log the host version, notifying process, number of notices, and model
wakes. Test disconnection, reconnection, duplicate notifiers, and a rapid burst
of results. An MCP tool call from a test client is not evidence of an idle
model wake.

A failed L07 may yield **Linux reference PoC passed / hands-free operation not
demonstrated**, but must never be marketed as fully autonomous lead-agent
integration. Without a working selected wake path, explicitly replan the MVP
milestone.

Codex acting as a lead host is explicitly unverified until its own wake and
catch-up path is tested; the Claude L07 result cannot be generalized to it.

## 8. Measurement plan

### 8.1 Preconditions

Record OS/architecture, .NET/CLI versions, model/effort, test repository,
instructions, configuration, log level, and measurement tools. Compare on the
same machine with tasks as similar as possible. Separate cold/warm startup and
cache hits/misses.

Baselines:

1. A documented comparison workflow with equivalent agent tasks, permissions,
   and launch mode; record its setup so contributors can reproduce it without
   private source access.
2. AgentTeamForge JIT with the same features.
3. AgentTeamForge AOT with the same features.

If the comparison baseline is unavailable, record that gap and continue
correctness spikes, but make no comparative efficiency or improvement claim.
The small fake external command-source seam remains part of the full PoC.

### 8.2 Workloads

- Zero jobs and no clients: ten minutes idle.
- Two connected bridges, no active turns: ten minutes idle.
- Two fake agents with predictable output.
- One team with one Claude and one Codex agent doing small equivalent tasks.
- Burst of 20 queued fake jobs with at most two active.
- Output burst, slow event consumer, and reconnection with a backlog.

### 8.3 Metrics and preliminary budgets

| Metric | PoC target / assessment |
| --- | --- |
| Idle CPU for daemon + two bridges | Under 1% of one logical core on average over ten minutes on the reference machine. |
| Local durable acceptance, fake backend | p95 under 100 ms at 20 sequential accepts/s; report fsync/disk environment. |
| Commit to event readable by connected client | p95 under 250 ms; model wake is not included. |
| AOT startup to IPC ready | Under 1 s for a warm start on the reference machine, median of at least 20 starts. |
| Each AOT host/child bridge | Warm startup under 500 ms and idle memory under 40 MiB on the reference machine; report platform metric, spread, and whole bridge count separately. |
| Orchestration memory | Daemon + bridges must not grow linearly with completed jobs; compare with the documented baseline and JIT. |
| Whole process tree memory | Report separately; no percentage promised before baseline. |
| Queue/recovery | No lost accepted jobs in C01–C32; uncertain attempts may remain visibly blocked. |
| Tokens/tool calls | Count and reported usage for the same task; lower runtime memory is not evidence of token savings. |
| Wake | No notice storm; document coalescing and catch-up after disconnection. |

The budgets are preliminary and have not been measured. Changes require a
reasoned decision before a new measurement series; do not move targets after a
failed result without reporting it. Model randomness means small live token
experiments should be described as indicative, not statistically established
savings. Run at least five comparable small tasks per live configuration if the
budget allows; otherwise report the gap.

Local IPC/fake performance requires at least 1,000 operations per configuration
and saved raw data for percentiles. Approve the token cost of real model tests
before experiments begin.

## 9. Go/no-go

### Linux reference PoC may pass when

- C01–C32 and T01–T12/T15–T19 pass where applicable, with documented coverage.
- L01–L06 have recorded passing evidence in both Herdr interactive and headless
  modes.
- A real lead agent has used the MCP interface before and after a client crash.
- Both backends run without unverified permission bypass.
- Published AOT binaries run on Linux; unanalyzed trim/AOT warnings must not be
  hidden. Necessary suppressions need narrow justification and a runtime test
  of the specific path.
- Results can be retrieved without the previous client process's memory.
- At least the preliminary idle/accept/event/startup budgets are met, or an
  explicit decision documents why a deviation is acceptable.
- No critical correctness/security finding remains open after independent
  review.

### Full PoC additionally requires

- L08, L10, L11, and L12 pass on real platforms, with a GUI session where
  needed and applicable terminal test cases recorded.
- T04, T07, and T10 have also run in each platform's intended production launch
  context; temporary launch configuration is sufficient, while full install
  remains M3.
- The selected macOS terminal provider and its limitations are documented.
- No headless/fake smoke test is presented as evidence of interactive support.

### Pause or reduce scope if

- Managed Claude or Codex requires extensive undocumented protocol cloning.
- Acknowledgment cannot be interpreted honestly, or progress requires automatic
  resending of an unknown delivery.
- Old processes can continue making external side effects while a new attempt
  starts.
- AOT requires a dependency tree that defeats a small runtime without a clear
  benefit.
- The new solution has worse overhead without enough operational or
  maintenance benefit.
- Wake requires an unsafe bypass of host identity or permission rules.
- Interactive mode lacks a safe control/result path and can only be demonstrated
  as a log tail. The user requirement must not be removed without a new explicit
  decision.

Possible outcomes: **continue**, **continue with explicit limits**, **redo a
risk spike**, or **decline the rewrite**. AOT is a goal, not a reason to sacrifice
correctness; a JIT fallback requires a new explicit product decision.

## 10. Deliverables after completing the PoC

Planned artifacts, not files that already exist:

```text
docs/decisions/                  verified ADRs and scope decisions
docs/poc-report.md               summary, go/no-go, remaining risks
evidence/poc/environment.json    versions and machine profile, no secrets
evidence/poc/test-matrix.md      C/L ID → command, platform, result, log
evidence/poc/benchmarks/         raw data and reproducible run descriptions
evidence/poc/crash/              logs and normalized DB/event extracts
scripts/                        test, crash, and measurement scripts
```

The report must distinguish **planned**, **implemented**, **automatically
tested**, and **live verified** features. It must also name tests not run and
explain why.
