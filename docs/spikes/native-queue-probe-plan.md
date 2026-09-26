# .NET 11 native queue: bounded feasibility probe

## Proposal and evidence boundary

**Draft for major-plan review; no coding or native execution authorized here.**
Next artifact: a failing fake-queue conformance test, then the smallest C# probe.
This is not a product adapter, phase completion, or permission to integrate queues.
Read [queue admission](../research/codex-queue-admission.md),
[downstream delivery](../research/native-downstream-delivery.md),
[core contracts](../planning/full-product/contracts.md), and
[control-probe rereview](codex-control-probe-rereview.md).
The newer queue research supersedes the older missing-idle-primitive question,
not the older human-pause, ownership, receipt, or visible-binding gates.

Confirmed at Codex 0.157.1: `thread/queue/add` accepts and persists while busy;
native dispatch uses atomic `start_turn_if_idle`, not start-or-steer.
Strict human priority/pause, unconditional safe deletion, and TUI binding are
**not** supplied by that primitive. Do not spend another lane rediscovering this.

## Fixed protocol inputs

- Codex source pin: `36650394c5b38c2990ccf2a3457165ca3e9d9726` (`rust-v0.157.1`).
  Use the queue research's pinned types, processor, service, storage and tests;
  reference design pin: `5149f3e1280749b52988a26952573cee61d2daff`.
- Start from reviewed `CodexControlProbe` snapshot
  `5dc36318301eebe71a4122e13998de423050e8ba`, not a moving worker tree.
  Its seven schemas and start-or-steer fake do not yet model the queue protocol.
- Freeze hashed experimental schemas/fixtures for initialize, queue add/list/start/
  delete, thread read/resume/history, item-started and turn lifecycle messages.
  Pin mock Responses streaming fixtures to the same native source. Unsupported
  schema keywords, unknown variants or binary/schema mismatch fail closed.
- Use typed RPC to one explicit owned endpoint and thread UUID; experimental
  handshake required. No name discovery, CLI fallback, direct SQLite mutation,
  idle-read/`turn/start` substitution, or invented native idempotency/lease fields.

## Safety profiles: separate verdicts

**Unchanged product gate:** foreign/human activity pauses automatic follow-ups
until explicit reconciled idle; uncertain carriers block every new carrier to
the target, including another sender, replacement generation or disabled flag.
Queue alone cannot satisfy this gate: it may auto-drain after human completion;
if queue wins first, an ordinary human start can steer the queued turn.
Keep strict dispatch refused; never turn a source finding into a product waiver.

**Candidate weaker checkpoint: “existing human turn not steered.”** On explicitly
owned disposable resources, enqueue during an already-active human-labelled turn;
observe a distinct queued turn afterward. Acceptance authorizes that later drain.
This may pass while strict human-pause remains blocked. Reviewer and owner must
explicitly accept this experimental scope before any native dispatch.

## Readiness gates, in order

1. **Before code:** independent major-plan review of this exact document revision,
   protocol pins, safety profiles, file ownership, isolation and abort rules;
   resolve blocking findings and obtain rereview. Name reviewers and record scope.
   Obtain explicit coding authorization; unavailable review means blocked.
2. Freeze the shared wire fixtures and lane seam below in that reviewed revision.
   Verify available .NET 11 SDK `11.0.100-rc.1.26425.128` and offline dependencies;
   no installation, silent retargeting or inherited .NET 10 compatibility claim.
3. **Before native execution:** reviewed combined code and passing fake/isolation
   tests, plus separate explicit user authorization naming binary, OS, resources,
   weaker profile and six-turn/five-minute budget. Coding approval is insufficient.
   Select and verify OS network isolation; inability to enforce it blocks launch.

## Two disjoint Claude C# lanes

Use separate feature branches/worktrees. Claude via win-agent-teams writes code;
separate Codex sessions review each actual diff and evidence, then rereview fixes.
Independent Codex GPT-6 Sol high integrates reviewed snapshots; semantic fixes go
back to Claude. Capture handoffs and retire completed Claude workers.

Smallest tree, relative to existing `spikes/codex-control-probe/`:

| Owner | Files / responsibility |
| --- | --- |
| A: fake queue + uncertainty | `src/CodexControlProbe/Queue/{QueueModel,QueueEvidence}.cs`; `tests/CodexControlProbe.Tests/Queue/*`; `schema/codex-0.157.1/queue/*` pinned fixtures/hashes |
| B: isolated native harness | `tools/NativeQueueProbe/{NativeQueueProbe.csproj,Program.cs,MockResponses.cs,OwnedRun.cs,QueueRpc.cs}`; `tests/CodexControlProbe.Tests/Isolation/*` |
| Integration only | Necessary solution/project references; no shared-file edits by both lanes |

A owns only new queue files; leave existing Dispatch/Fake/History semantics intact.
B consumes frozen wire fixtures and existing history validator, not A's fake runtime.
Use a bounded transport wrapper for history byte/time caps absent from the old probe.
B's console is a probe entry point, not a fourth production project or framework.
No custom model loop, second product scheduler, watcher, or application queue drain.
Native Codex owns scheduling; mock Responses returns fixed scripted events only.

## Lane A: first red tests, then minimal green model

- Busy add persists; explicit queue start reports busy and retains its row;
  human completion permits automatic distinct-turn drain without steering.
- Queue-first human steering and post-human automatic drain cannot pass strict
  priority/pause. Distinguish source-model conformance from observed native facts.
- Lost add reply, timeout or malformed reply retains the uncertainty fence;
  `clientUserMessageId`/nonce correlate, never deduplicate or authenticate origin.
- Start-before-delete crash, late add after revoke, two consumers, stale server/
  target generation, sender death and flag-off recovery never authorize replay.
- Empty queue, absent nonce, `deleted: true`, partial history and failed/interrupted
  outcomes are not unconditional cancellation/completion proof. Complete validated
  user history proves presentation; correlated terminal outcome proves completion.
  Bound reads; malformed later pages invalidate the whole aggregate.

## Lane B: isolation first, native script only after authorization

TDD launch refusal for inherited credentials/config, wrong endpoint/home/thread/
server generation, failed egress isolation, unexpected requests and exhausted caps.
Create a private temporary home and workspace within ignored repository artifacts;
use an allowlisted environment, isolated HOME/CODEX_HOME/config/cache, no user
credentials, keychain access, inherited MCP servers, hooks, proxies or login flow.
Bind mock Responses to loopback only. Enforce OS-level deny-all external egress
for the entire owned process tree, allowing only the private control/mock endpoints;
verify blocked outbound connectivity before Codex launch and retain network evidence.
Endpoint configuration alone is not isolation. Disable tools/commands and external
services; unexpected tool requests abort rather than being executed.

Own one pinned native server and two clients, with one target thread at a time.
Freeze endpoint, canonical home/store, thread UUID, server creation identity and
run generation, nonce, client message ID and returned queue ID per attempt.
Prove ownership with launch handles and private endpoint access, not PID alone.
Restart only after confirmed exit; replacement server gets a new generation and
requires explicit rebind to the same owned store. Never attach retained sessions.

| Script | Maximum scripted turns and observable result |
| --- | --- |
| Busy retention | 2: hold human-labelled Responses stream; add nonce; queue list retains row and explicit start is busy; release; separate queued request/turn/client ID completes |
| Pending deletion | 1: hold human-labelled turn; add then delete pending row in the uninterrupted single service; release; bounded observation sees no queued request, not universal revocation proof |
| Lost reply + cold resume | 1: create then unload owned thread; persist add but drop reply; no resend; restart owned server, reconcile pending row and resume only under approved script; correlate one queued completion |

Total planned: four turns; hard ceiling six across all requests/restarts and five
minutes from first process launch, including cleanup. No automatic reruns or blind
RPC/provider retries. Unexpected extra request, identity drift, network violation,
ambiguous evidence or deadline triggers abort: stop issuing work, retain uncertainty,
close mock, and stop only proven-owned probe processes through retained handles.
Capture bounded sanitized evidence; never kill unrelated processes or erase unique
recovery evidence. A failed cleanup is a failed run, not permission for PID scanning.

## Exit evidence and next decision gaps

Run offline restore, format verification, warnings-as-errors build and tests for
`CodexControlProbe.slnx`; verify fixture hashes. Test the published .NET 11 harness
under the approved isolation profile; report JIT and AOT separately, never infer AOT.
Record snapshot/hash, SDK/native version, OS, caps, network proof, request/queue/turn
correlations and each pass/fail/blocked gate. Fake success is not native evidence.

Separate later visible-TUI real-model gate: explicit terminal/platform, disposable
owned TUI on the same endpoint, one human turn plus one queued follow-up, maximum
two model turns/five minutes and separately approved token/cost budget. Verify visible
thread, human input, completion and bounded disconnect/reconnect; qualify switching
separately. No silent headless fallback; Windows/macOS require native platform runs.

Open decisions: approve weaker experimental profile or keep all live dispatch
blocked; select enforceable isolation per OS; approve native execution separately;
choose visible platform/budget. Product queue integration requires later contract
review for strict pause, revocation, uncertainty and daemon ownership through
Host → Business → DAL. This prototype cannot silently change those contracts.
