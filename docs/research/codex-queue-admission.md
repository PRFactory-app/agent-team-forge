# Codex 0.157.1 queue admission: public-source answer

## Decision

**Use the native queue as the next bounded real-agent E2E candidate, not
`turn/start`.** Queue dispatch really uses atomic idle-only admission. An active
human turn is not steered by the queued input. This closes the *missing native
idle-only primitive* source question; it does **not** close strict human-wins,
visible-TUI binding, revocation or crash/exactly-once gates.

In particular, “wait until idle” is not “pause after foreign activity until
AgentTeamForge explicitly reconciles.” Queue add authorizes later automatic
execution, including after a human turn completes. Do not silently substitute
that weaker contract. No second model loop or application queue-draining loop
is needed to demonstrate the native mechanism.

## Provenance and boundary

Read `AGENTS.md`, `HANDOFF.md`, [downstream research](native-downstream-delivery.md)
and [control verification](../spikes/codex-native-control-verification.md).
Unauthenticated GitHub API reads confirmed both repositories are public:
[`openai/codex`](https://api.github.com/repos/openai/codex) and
[`mikaelliljedahl/agentic-coder-teams-mcp`](https://api.github.com/repos/mikaelliljedahl/agentic-coder-teams-mcp).
Codex [`rust-v0.157.1` tag reference](https://api.github.com/repos/openai/codex/git/ref/tags/rust-v0.157.1)
resolves through [annotated tag `ac0e23e`](https://api.github.com/repos/openai/codex/git/tags/ac0e23e5232692b95268583c8278c50b8c436d2b)
to **`36650394c5b38c2990ccf2a3457165ca3e9d9726`**. Reference commit
**[`5149f3e1280749b52988a26952573cee61d2daff`](https://api.github.com/repos/mikaelliljedahl/agentic-coder-teams-mcp/commits/5149f3e1280749b52988a26952573cee61d2daff)**
was also verified. All source/test links below pin those commits, not branches.

The installed-version identification comes from the existing local verification
report; no Codex executable was invoked here. Source and tests were **read, not
run**. No private repositories, credentials, live processes, model calls,
installs or upstream execution. Downloads are ignored research artifacts inside
this repository. This report is not implementation or independent plan approval.

## ANSWER matrix

“Closed” means a source/protocol question is answered, not a native platform pass.
“Impossible” means unavailable through this pinned queue contract, not impossible
for every future backend or a newly reviewed application contract.

| Blocker / question | Answer at this pin | Disposition |
| --- | --- | --- |
| Add during an active human turn: wait, steer or reject? | **Accept and persist, then wait.** Busy state alone does not reject add. Automatic dispatch uses `start_turn_if_idle`; explicit `thread/queue/start` returns busy and preserves the row. Validation, storage/capacity and thread restrictions can still reject. [A][add] [D][dispatch] [T1][busy-test] | **Closed by existing public experimental contract/source.** Confirm installed native behavior in the bounded probe. |
| Atomic idle-only admission? | **Yes.** Core checks and reserves `active_turn` under the same lock; existing activity yields `NotIdle`. Queue dispatch never selects start-or-steer. [D][dispatch] [C][core] | **Closed for non-steering of an already-active turn in that runtime.** Not an idle-read/send workaround. |
| Strict human precedence and pause until explicit reconciliation? | **No.** No human-origin priority, generation/expected-idle token, reconciliation lease or held-add option. If queue wins admission, a later ordinary human start can steer that turn. If human wins, queue waits and can auto-start afterward. [P][types] [C][core] [T2][new-turn-test] | **Impossible through queue alone.** A probe cannot establish a missing universal guarantee. Keep strict gate blocked unless a reviewed contract changes. |
| Durable acceptance and scheduling? | SQLite queue row keyed by thread and server-generated UUID; FIFO dispatch on eligible idle lifecycle; interrupted lifecycle pauses, failed lifecycle drains. Cold resume can trigger persisted input. [S][storage] [D][dispatch] [T3][lifecycle-tests] [T4][cold-test] | **Closed source design; bounded native restart/receipt probe needed.** Not power-loss or exactly-once proof. |
| Durable identity versus the actual visible TUI? | UUID identifies a thread in the selected server/store, not a TUI, process generation or terminal tab. Add can target a stored, unloaded thread without resuming it. [A][require-thread] [T4][cold-test] | **Thread binding closed; visible binding needs bounded TUI switch/disconnect/reconnect qualification.** Native display attestation is absent; probing a constrained launch is not a new attestation API. |
| Subscription and multiple clients? | Experimental queue API plus lightweight thread-scoped notifications to subscribers; subscriptions are many-to-many. Neither subscriber presence nor resume identifies the displayed thread. [E][experimental] [N][notifications] [N2][subscriptions] | **Public multi-client mechanism exists; native shared-endpoint delivery/catch-up needs probe.** No exclusive client/consumer lease. |
| Safe delete/revoke? | Delete returns whether a row was removed; same-service dispatch/delete share a lock. Start occurs **before** row deletion. No tombstone, revoke-before-publish fence or execution rollback. [R][delete] [D][dispatch] [S][storage] | **Closed for row-removal semantics.** Unconditional “never executed / cannot execute” proof is **impossible** from `deleted` alone. Test only a narrowly controlled pending-row cancellation profile. |
| Lost reply, retry and receipt? | Add returns queue ID and caller correlation ID, not turn completion. Every insert generates a new queue ID; client ID is not deduplication. Empty queue may mean dispatch, deletion or rejection. [P][types] [S][storage] [T5][receipt-test] [T6][rejection-test] | **Closed ambiguity; no blind replay.** Native correlation/history and outage reconciliation remain probe gates. |

## Mechanism and limits that matter

### Admission is genuinely different from direct turn start

`codex queue --thread … --message …` resolves UUID/name and sends
`thread/queue/add`; it is not an alias for `turn/start`. The CLI generates a
client message ID and prints `Queued message <id> for thread <id>.` It discovers
a shared server or uses an explicit remote endpoint, rejects local `--no-daemon`,
and refuses unsupported shared-server queueing rather than silently falling
back. Prefer an explicit owned endpoint and UUID, not name resolution. [CLI][cli]

Add validates thread/input, serializes `TurnInput`, persists it, emits queue
change, then wakes a loaded eligible thread **before returning**. Thus execution
can begin before the caller receives acceptance. There is no inert “prepare”
step followed by a mandatory caller-controlled commit/start. The public methods
are experimental and require the experimental handshake. [A][add] [Q][enqueue]
[E][experimental] [T0][handshake-test]

Automatic and explicit queue starts both call core `start_turn_if_idle` and
remove the selected row only after `Started`. The atomic reservation is stronger
than an idle precheck. It supplies no preference for a human whose input has not
yet acquired admission. Existing [direct-start verification](../spikes/codex-native-control-verification.md#7--busy-start-and-human-wins)
remains correct for `turn/start`, but must not be generalized to queue dispatch.

Native scheduling is already implemented: same-service add invokes idle
lifecycle; completed/failed idle can drain the next item; interrupted idle does
not. A native extension checks external SQLite changes every 10 seconds for
loaded/newly resumed threads and retries eligible dispatch. This is backend
machinery, **not a requirement to add an AgentTeamForge watcher or model polling**.
Neither CLI success nor the polling interval is a delivery deadline. [W][watcher]
[D][dispatch] [T3][lifecycle-tests]

### Storage, recovery and identity

The local store uses [`queue_1.sqlite`][db-name]; inserts atomically allocate order and
enforce capacity, and deletion is scoped to `(thread_id, id)`. SQLite is WAL with
`synchronous(Normal)`: process-restart persistence is not a promise that every
acknowledged insert survives power loss. Queue storage and execution/history
are not one transaction. [S][storage] [DB][sqlite]

Freeze endpoint/server ownership, store/home configuration, thread UUID,
AgentTeamForge generation, nonce, client message ID and returned queue ID on the
attempt. These are application fences, not native queue capabilities. A stored
thread can be queued while unloaded; `thread/read`/list need not load it;
`thread/resume` can cause pending work to run without any visible TUI. Explicit
queue start requires a loaded thread. Archived stored threads, ephemeral loaded
threads and restricted spawned subagents have rejection paths. A successful
resume is therefore **not** proof that the intended TUI is displaying the target.
[A][require-thread] [T4][cold-test]

Queue change only identifies the thread; it is not a durable receipt stream.
List pagination uses live offsets, without a snapshot. Multi-client subscribers
can receive changes, but queue add itself is not a TUI subscription/binding
handshake. Use lifecycle events plus bounded durable-history reconciliation for
missed events; never infer absence from a failed/partial read. [A][add]
[N][notifications] [N2][subscriptions]

### Delete and receipt: keep the uncertainty barrier

Within one uninterrupted service, a delete winning the dispatch lock before any
start removes the pending row. If normal dispatch wins and removes it, delete
returns false. However:

- Core can start, then queue deletion can fail or the process can die. The row
  can remain after presentation; a later `deleted: true` cannot disprove that
  earlier execution. A restart can encounter that surviving row again.
- Dispatch locks are process-local. Two runtimes loading the same thread/store
  have no queue-level claim/consumer lease across read → start → delete. SQL
  removal in one process cannot retract input already read by another.
- Deleting an ID does not fence a delayed add, another submission, stale sender
  or a replacement generation. `deleted: false`, lost delete response and queue
  absence are not cancellation proof. Killing the queue CLI does not revoke it.

These are inferences from [lock/delete ordering][delete], [dispatch][dispatch]
and [SQL][storage], not observed failures in this investigation. They rule out
promoting a plain delete response into the reference's proposed authoritative
removal proof without additional, proven ownership/recovery constraints.

Use caller-chosen `clientUserMessageId` plus an attempt nonce for correlation,
not authorization or idempotency. The native test carries that client ID into
`item/started` and correlates completion by turn ID. Another test deliberately
starts an ordinary turn with the same queued client ID and leaves the queue
intact: matching IDs do not consume/deduplicate a submission. Hook-rejected
queued input can also be consumed without any model request. [T5][receipt-test]
[T2][new-turn-test] [T6][rejection-test]

At reference pin `5149f3e…`, [S-1][reference-spike] reports idle Windows native
TUI delivery and a rollout nonce after 8.3 seconds, but explicitly leaves busy
behavior and deletion qualification open. Its [runner][reference-runner] treats
post-launch timeout/nonzero/missing-ID outcomes as uncertain; fake
[runner tests][reference-tests] are not native cancellation proof. Keep the
reference's unresolved-carrier barrier: neither absent nonce nor empty queue
authorizes resend. A nonce in user history proves presentation, not successful
action; completion needs the correlated terminal turn outcome.

## Smallest next probe proposal — no implementation here

1. **First, a small .NET 11 fake test slice.** Model native queue add/auto-start,
   not busy rejection or steering. Cover human-first busy retention, queue-first
   human steering, automatic post-human drain, lost add response, start-before-
   delete crash, late add after revoke, and two consumers. Assert that uncertain
   attempts stay fenced and strict human-pause cannot be marked satisfied. Keep
   it in the existing slice/test structure, not a new scheduler or model loop.
2. **Then one bounded native, no-real-model probe.** Requires explicit live
   process ownership/authorization even with a mock provider. A .NET harness
   owns an isolated temporary home, one pinned Codex server, one thread and two
   clients; a loopback fake Responses endpoint supplies deterministic barriers,
   no user credentials or external model traffic. Enable experimental API.
   Hold a human-labelled turn, add one nonce, verify no queued input reaches its
   request and explicit queue start returns busy; release and verify a separate
   queued turn/client ID/completion. In a second case delete while the human
   turn is held, release and verify no later queued turn. Drop the add response
   and reconcile without resend; cold restart only these owned resources to
   check persistence. Bound wall time to five minutes and scripted turns to six;
   no commands/tools or upstream test execution. This validates native ordering,
   not universal revocation or power-loss semantics.
3. **Fastest actual real-agent checkpoint after that:** request explicit budget
   and ownership for one disposable visible Codex TUI attached to the same owned
   endpoint in the selected terminal, one human turn plus one queue follow-up,
   maximum two model turns/five minutes with a user-approved token/cost cap and
   no tool side effects. Record native version/OS, displayed thread, distinct
   turn IDs, nonce receipt and completion. Include a bounded disconnect/reconnect
   check; qualify thread switching separately before claiming general visible
   binding. No use of retained sessions, credential inspection or silent headless
   fallback. Run promised Windows/macOS checks on those platforms, not by inference.

**Practical priority:** stop researching whether queue steers active human turns;
source answers that. Prefer typed `thread/queue/add` on the already-owned shared
server for controllable correlation over spawning a CLI with generated IDs.
Keep the CLI as the public reference path, not a second backend implementation.
Proceed toward the narrow queue E2E checkpoint, while explicitly retaining strict
human-pause, TUI binding and uncertainty gates. If strict pause is a prerequisite
for any live downstream dispatch, resolve that specific contract decision first;
more probes cannot manufacture a held queue or human-priority primitive.

## Local validation

Documentation-only checks: UTF-8, final newline, trailing whitespace, balanced
fences, local links, pinned source line ranges and `git diff --check`. No runtime,
.NET build/AOT, upstream tests, platform tests or independent implementation review
were run. Only this research document is a deliverable; no commit or other document
changes. No repository-wide documentation checker was identified for this task;
a reusable bounded Markdown/link check would be useful.

[add]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/src/request_processors/thread_queue_processor.rs#L77-L137
[require-thread]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/src/request_processors/thread_queue_processor.rs#L192-L324
[enqueue]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/ext/queue/src/service.rs#L246-L280
[dispatch]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/ext/queue/src/service.rs#L364-L489
[core]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/core/src/session/turn_input.rs#L376-L517
[types]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server-protocol/src/protocol/v2/thread.rs#L898-L1003
[experimental]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server-protocol/src/protocol/common.rs#L623-L658
[cli]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/tui/src/session_queue_commands.rs#L27-L128
[watcher]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/ext/queue/src/service.rs#L89-L244
[storage]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/state/src/runtime/queued_items.rs#L77-L166
[sqlite]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/state/src/sqlite.rs#L297-L311
[delete]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/ext/queue/src/service.rs#L246-L354
[db-name]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/state/src/sqlite.rs#L26-L34
[notifications]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/src/extensions.rs#L164-L201
[subscriptions]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/src/thread_state.rs#L342-L426
[handshake-test]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/tests/suite/v2/thread_queue.rs#L67-L114
[busy-test]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/tests/suite/v2/thread_queue.rs#L701-L772
[new-turn-test]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/tests/suite/v2/thread_queue.rs#L860-L943
[lifecycle-tests]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/ext/queue/tests/queue_service.rs#L480-L570
[cold-test]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/tests/suite/v2/thread_queue.rs#L430-L557
[receipt-test]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/tests/suite/v2/thread_queue.rs#L369-L427
[rejection-test]: https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/ext/queue/tests/queue_service.rs#L727-L825
[reference-spike]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/docs/features/native-downstream-delivery/spikes.md#L7-L59
[reference-runner]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/src/claude_teams/native_wake.py#L532-L648
[reference-tests]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/tests/test_codex_queue_runner.py#L25-L127
