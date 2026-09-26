# Codex 0.157.1 native-control protocol verification

2026-09-26; independent GPT/Codex verifier. Scope: RE-REVIEW gates **#3 history,
#5 visible TUI binding, #7 human interleaving**. Protocol/source investigation,
not feature-code approval or a live qualification run.

## Evidence boundary

- Installed versioned `codex` executable reports `codex-cli 0.157.1`.
  SHA-256: `3e2584f3f3829a43a0495011a1cecb2facbe64a2403e2b682351fd9c2983f970`.
- Generated JSON schemas offline, both default and `--experimental`, using
  `codex app-server generate-json-schema --out <ignored-artifact-directory>`.
  Generation exited 0. No app-server listener, daemon, TUI or model turn started.
- Public `rust-v0.157.1` tag resolves through annotated tag
  `ac0e23e5232692b95268583c8278c50b8c436d2b` to commit
  **`36650394c5b38c2990ccf2a3457165ca3e9d9726`**. Links below pin that commit.
  Source inspection is not proof that the installed binary reproduces every
  upstream behavior; relevant generated fields agree with the pinned types.
- [Official App Server documentation](https://learn.chatgpt.com/docs/app-server)
  was fetched for context; it is rolling documentation, not version-pinned
  evidence. Conclusions below rely on installed schema and pinned source.
- Downloaded public files and generated schemas remain under ignored
  `artifacts/codex-protocol/` in this worktree. No credentials inspected,
  service/Herdr calls, installs, production source changes or live operations.
  Initial wrapper invocation reported zero tools installed; subsequent commands
  used the existing executable directly.

## #3 — Successful history responses

**Verified schema:** request requires `threadId`; optional `cursor`, `limit`,
`sortDirection`, `itemsView`. Default direction is descending, default items
view is summary. Response requires **`data`**, an array of `Turn`; optional
nullable strings `nextCursor` and `backwardsCursor`. It has **no response
`threadId` or snapshot token**. Each turn requires string `id`, array `items`,
and known `status`; timestamps are nullable/optional. `itemsView` defaults to
full on the Turn type when omitted. Empty/nonempty string restrictions on IDs
are application safety validation, not schema constraints.
[Pinned history types](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server-protocol/src/protocol/v2/thread.rs#L1701-L1734)
and locally generated `v2/ThreadTurnsList{Params,Response}.json`.

**Verified implementation:** ordinary history is rebuilt per request, with a
live active-turn snapshot merged before pagination; rollback/compaction can
change earlier turns. Paginated storage has a separate path; requesting full
items hydrates each returned turn before responding. These paths do not expose
an atomic history-to-send lease or multi-page snapshot guarantee.
[Pinned history handler](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/src/request_processors/thread_processor.rs#L3043-L3257).

**Recommendation:** request `itemsView: "full"` explicitly; validate the whole
page and every usable turn/item before treating history as available. Reject
missing/malformed data rather than filtering invalid entries into an empty
list. Validate cursor types when present, follow non-null `nextCursor` with the
same bound thread/direction/view, and bound pages, bytes, turns and total time;
detect repeated cursors even on empty pages. A later-page error invalidates the
aggregate. Use JSON-RPC request correlation and validate `thread/read.thread.id`
against the bound ID; do not invent a required page `threadId`. Omitted cursors
are schema-valid; a stricter envelope requirement must be an explicit local
safety profile, not a purported native requirement.

`backwardsCursor` supports reversing direction and re-including an anchor to
catch updates, not exclusive ownership. A complete valid empty traversal says
what was read, not that a human cannot start afterward. Unknown status,
incomplete item views or unclassified live turns cannot authorize dispatch or
pause clearance. The schema also does not establish the spike's `ProvenEmpty`
exception from local `HistoryEstablished=false`; keep that exception separately
unqualified or disable it in the safe path.

## #5 — Native TUI-client/thread association

**Verified:** `thread/loaded/list` returns loaded thread IDs with pagination,
not client/PID/pane ownership. Initialization accepts caller-provided client
name/version and returns server metadata, not a TUI-to-thread attestation.
The complete experimental request schema exposes no local client/subscriber
enumeration method proving the selected thread of a particular visible TUI.
`remoteControl/client/list` is environment-scoped remote-device metadata;
its client records have no thread/connection/PID association.
[Pinned remote-client types](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server-protocol/src/protocol/v2/remote_control.rs#L102-L145).

**Verified internal mechanism, not public proof:** the server maintains
connection-to-thread subscription sets and has internal subscriber queries.
Subscriptions support events and can cover multiple threads; they do not prove
which thread a human is viewing. No corresponding public query was found in
the generated request API.
[Pinned subscription state](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/src/thread_state.rs#L340-L422).

**Recommendation:** keep #5 open for the full visible-agent adapter. One loaded
thread plus an alive `--remote` TUI is a constrained deployment inference, not
a native binding. Require separately qualified disconnect/reconnect/switch
behavior or a new reviewed binding contract before promotion. Private socket
access and caller-supplied `clientUserMessageId` provide transport isolation and
correlation respectively; neither authenticates human versus daemon origin.

## #7 — Busy start and human-wins

**Verified implementation, strongest finding:** `turn/start` calls
`start_or_steer_turn`. It accepts both Started and Steered submissions, then
returns a turn object without a discriminator telling the caller which happened.
A pinned upstream test explicitly expects a second start to steer the active
turn and return that active turn's ID. This is source/test evidence; that Rust
test was read, not run here.
[Pinned start handler](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/src/request_processors/turn_processor.rs#L650-L715),
[pinned active-turn test](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/tests/suite/v2/turn_start.rs#L649-L755).

**Verified API distinction:** `turn/start` has no expected-turn, expected-idle,
or expected-previous-turn precondition in either generated schema. `turn/steer`
requires nonempty `expectedTurnId`; core checks that ID under its active-turn
lock. This fences steering of a known active turn, not starting a new daemon
job without interfering with a racing human.
[Pinned steering handler](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/src/request_processors/turn_processor.rs#L1021-L1091),
[pinned core input logic](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/core/src/session/turn_input.rs#L628-L652).

Core does have `start_turn_if_idle` and an internal continuation with an
expected previous turn. Neither is exposed as an idle-only `turn/start`
option in this app-server API. Server `TurnAdmission` controls shutdown drain,
not per-thread exclusive idle admission.
[Pinned core helpers](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/core/src/codex_thread.rs#L351-L389),
[pinned drain admission](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/app-server/src/turn_admission.rs).

**Safety implication/inference:** if a human turn becomes active after idle
preflight, daemon `turn/start` can inject its instruction into that turn. Serial
request processing or a second read cannot make read-plus-write atomic; later
detection/interrupt cannot undo injection and may interrupt human work.
Keep strict human-wins and full adapter promotion blocked. Safe primitives
and the independent fake-core checkpoint can proceed under their own gates.

## Minimal next verification boundary

Safety author can immediately use C# fake tests for malformed pages, cursor
cycles, partial-page failure, detached/switched TUI facts and the precise
idle-read → human-start → daemon-start interleaving. Model the documented
start-or-steer behavior, not a fictitious busy rejection; assert safe-path
refusal/fence retention until a reviewed admission contract exists.

A future native confirmation needs separate authorization and disposable,
proven-owned resources: deterministic mock Responses endpoint (no real model
or credentials), two clients, explicit barriers, then separately visible-TUI
disconnect/reconnect/switch qualification. Those experiments were **not run**
here. Do not test against retained sessions. This report does not approve any
moving feature branch; code review and negative gates remain commit-bound.
