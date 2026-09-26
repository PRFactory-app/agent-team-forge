# Native downstream delivery: pinned public research

## Scope and provenance

**Useful transport and uncertainty design; not a completed native-delivery feature
or a solution to AgentTeamForge's real-agent E2E blockers.** Research only: no
upstream code/tests executed, installs, agent launches, native effects, or contract
changes. Access was limited to this repository and the explicitly authorized
public reference repository. Existing Codex protocol evidence was read locally;
its external source links were not fetched for this investigation.

GitHub API checks confirmed [repository metadata][repo-api] has `private: false`,
[commit][commit] `5149f3e1280749b52988a26952573cee61d2daff` exists, and
[branch][branch] `feat/native-downstream-delivery` points to that exact SHA.
The [branch comparison][branch-compare] returned `identical`, ahead/behind 0/0.
The [comparison to main][main-compare] returned `behind`, ahead 0 / behind 13,
with merge base `471a17514d041e09b69cb24b910e418da28d2027`: at inspection, main
was the ancestor, not a branch containing this work. These API/branch links are
mutable; all source links below pin the requested SHA.

The commit itself changes **only two documents**: adds the implementation
handoff and appends round-4 review. Its parent is
`1459cc82cac9fa813eec3240c65d278e6a3f583d`. The handoff's historical “not pushed
yet” text is superseded by public availability, not evidence of a merge.

## What exists versus what is proposed

[Implementation][implementation], [plan][plan] (§2.9 overrides earlier rules),
and [review][review] separate four parts:

| Part | Evidence at the pin |
| --- | --- |
| A: Codex downstream via `codex queue` | Shared queue runner exists; delivery selection, durable native settlement, frozen carrier, N5 barrier and finalization remain TODO. |
| B: Claude downstream through child-local mailbox poster | Mailbox store, CAS transitions, `DeliveryPoster`, recovery and integration remain TODO. Hook counters and record metadata exist. |
| C: Windows Claude named-pipe transport | Implemented helper and wake wiring; live pipe-owner observation, not completed Claude delivery/wake qualification. |
| D: Codex lead wake on child reply | Registration, provisional binding and notifier integration remain TODO. Existing external-member wake is a different path. |

Source agrees: [`_do_follow_up`][server] still persists an attempt, shuts down
an owned live child and calls `backend.resume`; it does not dispatch native
follow-ups. Round-4 `APPROVED_WITH_NITS` approves the amended plan and reviewed
runner/pipe fixes, explicitly not unfinished implementation or live gates.
Command-budget UTF-16 accounting and epoch lifecycle completion remain review
follow-ups. Some earlier epoch findings have code at this pin; the handoff's
open-items list is not a precise substitute for inspecting that code.

## Delivery, receipt and crash semantics

- **Acceptance is separate from attempt.** Existing [delivery store][store]
  persists `queued/pending` before waiting. Caller chooses the idempotency key
  before sending; namespace is `(sender, key)`, fingerprint covers target,
  prompt and options. Reusing a key with changed input conflicts. Before resume,
  `_mark_attempt_sent` records nonce, operation ID and target snapshot as
  `queued/sent`; terminal outcomes cannot be overwritten. Files use locks and
  temporary-file replacement, without an explicit fsync in `save_records`.
  This is not proof of SQLite atomic job + idempotency + outbox acceptance or
  power-loss durability.
- **Queue acceptance is not presentation or completion.** The implemented
  [runner][wake] separates `Popen` construction from `communicate`. Only a
  construction failure proves no enqueue. Exit 0 plus parsed submission UUID
  means enqueued; timeout, nonzero exit, missing ID and post-launch errors remain
  uncertain. Killing the queue CLI does not establish cancellation of its
  durable submission. [Runner tests][queue-tests] exercise these distinctions
  with fakes; they were read, not run here.
- **Receipt is backend-record evidence, not an agent callback.** The
  [scanner][delivery] looks for a random attempt nonce in named user-input
  records, not arbitrary assistant text. For Codex this is a rollout
  `response_item` with user role. A receipt establishes presentation in backend
  history, not completed job action, exclusive human/daemon origin, or exactly-once
  side effects. Full scans distinguish found, absent, indeterminate and ambiguous.
  Hook callbacks update activity markers; they are not authoritative completion
  acknowledgments.
- **Durable native carriers require stronger recovery than resume.** Proposed
  N5 blocks every new carrier to the target while any sender has a native
  `sent/unconfirmed` row, even after flags turn off, process death or name reuse.
  Freeze session/home/transcript/epoch/host identity on the attempt and CAS the
  submission ID onto that same attempt. Absence of a nonce—even after kill or
  reboot—must not permit resend: queued work might execute later. Exit requires
  receipt, separately verified native deletion plus rescan, or explicit operator
  release warning that work may still execute. Deletion spike S-5 is unrun.
  **These protections are a plan, not installed downstream behavior.**
- **Client-crash recovery is not daemon ownership.** Queryable keys and proposed
  child-local posting help survive lost call responses; published work might
  continue if the child poster survives its lead. Existing pending responses
  still carry sender obligations. Neither the reference's file stores nor its
  lead-owned calls demonstrate our independent daemon draining accepted work
  after bridge death. No automatic replay of an uncertain prompt is justified.

## Claude mailbox, host trust and retries

Proposed flow: persist attempt → publish mailbox entry → child's own MCP server
posts through its own authenticated host channel → scan frozen transcript.
This is host-local control, not a generic MCP callback capable of starting turns
in an arbitrary host. Mailbox transitions are locked CAS operations:
`offered → taken → posting → posted/uncertain`; authoritative pre-write failure
or successful retraction can permit retry. Initialize the mailbox before the
first attempt; missing/corrupt later storage means unknown, not empty. Revoke
and publish serialize through tombstones, preventing late publication after
recovery. Posted/uncertain entries are not replayed by replacement posters.

Proposed capability proof binds backend session, dispatch epoch, host PID plus
creation token, poster owner lock and fresh heartbeat. Existing [hooks][hooks]
count transitions into idle, not duplicate Stop events, count user submissions,
and reject older epochs. Planned persisted `consumed[epoch] = {seq, nonce}`
limits posting to one entry per observed idle transition; a pre-write failure
rolls back only its own consumption. This improves missed-edge/restart handling,
**not atomic admission**: the plan explicitly permits the residual human-input
or wake race between idle observation and posting (“never knowingly mid-turn”).

[Windows transport][pipe] checks server PID on the same handle used to write,
uses overlapped I/O and bounded cancellation drain, and retains buffers/handles
for undrained writes. Case-insensitive per-path reservations and a global cap
bound parked writes; notifier ticks reap them. `write_started` conservatively
separates provably unsent from uncertain writes. Successful write has no host
acknowledgment. Current [wake code][wake] supports Linux/Windows channel discovery
and rejects macOS; this is source capability, not a platform pass.

Do not copy upstream permission assumptions. `_prepare` explicitly calls its
self-asserted environment identity/parentage check an **accident guard, not a
security boundary**. Host-channel authentication and thread existence likewise
do not authorize arbitrary job bodies, homes or sessions. AgentTeamForge still
needs authenticated principal/session scope, budget and approval enforcement,
revocation, credential scrubbing and generation fencing. No refusal/bypass or
approval-lifecycle qualification follows from a successful transport write.

## Source versus reported run evidence

[Spikes][spikes] report actual Windows 11 Pro 26200 observations, Codex CLI
0.157.1 from Desktop 26.924.22138, Claude Code CLI 2.1.281 under Desktop 2.9939.2.
These are upstream reports, not independent reruns here.

| Evidence | What it establishes / does not establish |
| --- | --- |
| S-1, Windows native `codex.exe` | Message queued into an idle interactive child; submission ID returned; nonce appeared in rollout after 8.3 seconds; child replied; multiline/metacharacter text survived. Concrete in-place transport feasibility, not integrated A or human-race safety. Busy case and large argv were not measured. |
| S-4, Windows Claude | Opened channel, queried pipe-server PID matching nearest Claude host, closed **without writing**. Useful ownership evidence; no actual wake, prompt receipt or approval evidence. |
| S-2 / S-3 | Claude child propagation and native user-record/size evidence blocked or unrun; Windows CLI not logged in. No safe disposable Claude child was used. |
| Automated gates reported at parent `1459cc8` | Windows: 1899 passed, 7 skipped; format/lint clean; two pre-existing type diagnostics. [Pipe tests][pipe-tests] use a test pipe server; [logic tests][pipe-logic] use controlled seams. Neither means real Claude E2E. |
| Round-4 reviewer runs | Stable pipe/runner/wake scope: 91 passed, 4 skipped. Concurrent evolving hook/record snapshots also had failures (103 passed / 7 failed / 4 skipped; 94 passed / 3 failed / 4 skipped). Review explicitly attributes them to unfinished/changing implementation; do not combine these with the handoff count into an independently green final run. |
| Remaining qualification | N1/N3/N5/N6/N7/N8 merge gates remain future work; N2 busy behavior, S-5 queue deletion and S-6 lead self-registration remain unproven. No new macOS or native Pi evidence. |

Test names and Linux tests do not establish Windows support. Here the narrow
Windows feasibility claim rests on explicit S-1/S-4 reports; our published .NET
binary, selected terminal, launch context and native platform gates remain open.

## Fit with AgentTeamForge and PR #70

Read alongside [native-wake research](native-wake-pr70.md),
[architecture](../architecture.md), [full-product contracts](../planning/full-product/contracts.md)
and [source-pinned Codex control verification](../spikes/codex-native-control-verification.md).
PR #70 is the wake baseline; this branch extends it but is not merged evidence.

- **Keep wake and downstream jobs separate.** PR #70-style native wake remains
  standard: commit messages, send notice-only wake, authenticated read/ack,
  generation-bound registration, coalescing, bounded retry and unread catch-up.
  Downstream native delivery carries the actual prompt and nonce; it cannot turn
  a notice into a job receipt or completion. Upstream's opt-in policy, retained
  watcher/Stop-hook behavior and resume fallback are not our product contract.
  Inherited Codex member wake still depends on a later send to reconsider retry;
  this pin does not establish durable timer-driven final-message catch-up.
- **Codex idle-start/human race remains blocked.** Queue transport is worth
  investigating as an alternative to `turn/start`, whose pinned behavior can
  steer a racing human turn. But S-1 only proves idle enqueue/presentation; E6
  remains an idle-marker precheck, with no demonstrated atomic idle-only or
  human-wins admission. Do not infer such guarantees from the queue's name.
- **Visible TUI binding remains blocked.** A valid thread/home, live PID, nonce
  receipt and `interactive` launch flag do not attest which thread a particular
  visible TUI currently displays, nor switching/disconnect/reconnect behavior.
- **Claude uncertainty narrows, not closes.** Child-local relay and Windows
  handle ownership/cancellation give concrete design material. Mailbox/poster,
  live child receipt, approvals, busy races, completion and recovery remain
  unqualified; “never knowingly mid-turn” is weaker than our human-wins contract.
- **Pi gap is unchanged.** Native Pi delivery is explicitly a non-goal; upstream
  proposes resume fallback. This supplies no managed Pi same-session control,
  native wake, interrupt/stop/reconnect or Windows visible-TUI qualification.

## Recommended bounded follow-up

1. Research only first: inspect the exact supported Codex version's public
   queue admission/start/delete implementation.
   Determine busy semantics, human precedence, cancellation races and durable
   identity. Existing local protocol report does not answer these queue questions.
2. If a viable contract emerges, independently review a small .NET 11 spike plan.
   Preserve Host → Business → DAL: daemon-owned acceptance/attempt/receipt state,
   thin authenticated host-local bridge, no second scheduler or writable bridge DB.
   Test unresolved-carrier barriers, lost enqueue response, publication/revocation
   races, stale epochs, sender death and flag-off recovery with deterministic fakes.
3. Only with explicit live authorization, qualify disposable owned agents:
   queue versus human start with barriers; TUI switching/reconnect separately;
   Claude native receipt/approvals and own-host relay; each promised native OS.
   Pi needs its own managed-control investigation, not an inferred transport.

**E2E verdict:** reduces uncertainty about idle Windows Codex delivery and Claude
Windows pipe mechanics. Closes none of the current strict human-race, TUI-binding,
Claude full-control or Pi real-agent E2E gates. No production promotion, permission
relaxation, automatic interactive kill/resume fallback or contract change follows.

## Validation

Documentation-only checks: whitespace, final newline, local link existence and
pinned public source availability. No upstream tests, .NET build, runtime/AOT or
native-platform tests run. Research is not implementation/review approval.

[repo-api]: https://api.github.com/repos/mikaelliljedahl/agentic-coder-teams-mcp
[commit]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/commit/5149f3e1280749b52988a26952573cee61d2daff
[branch]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/tree/feat/native-downstream-delivery
[branch-compare]: https://api.github.com/repos/mikaelliljedahl/agentic-coder-teams-mcp/compare/5149f3e1280749b52988a26952573cee61d2daff...feat%2Fnative-downstream-delivery
[main-compare]: https://api.github.com/repos/mikaelliljedahl/agentic-coder-teams-mcp/compare/5149f3e1280749b52988a26952573cee61d2daff...main
[implementation]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/docs/features/native-downstream-delivery/implementation.md
[plan]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/docs/features/native-downstream-delivery/plan.md
[review]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/docs/features/native-downstream-delivery/plan-review.md
[spikes]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/docs/features/native-downstream-delivery/spikes.md
[server]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/src/claude_teams/server_simple.py#L4792-L4899
[store]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/src/claude_teams/delivery_store.py
[delivery]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/src/claude_teams/delivery.py
[wake]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/src/claude_teams/native_wake.py
[hooks]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/src/claude_teams/hooks.py
[pipe]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/src/claude_teams/winpipe.py
[queue-tests]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/tests/test_codex_queue_runner.py
[pipe-tests]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/tests/test_winpipe.py
[pipe-logic]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/5149f3e1280749b52988a26952573cee61d2daff/tests/test_winpipe_logic.py
