# Managed Pi control: concrete feasibility findings

## Decision and evidence boundary

**Pi RPC is headless, not the interactive TUI and not a control server for an
existing TUI.** For required visible execution, the smallest credible candidate
is the normal Pi TUI plus a narrowly scoped extension loaded into that same
process. The extension calls supported Pi APIs; Pi retains its model loop,
conversation, tools, credentials and UI. Authentication, transport, ownership
fencing and replay are adapter work, not built-in Pi guarantees.

Research baseline: `@earendil-works/pi-coding-agent` **0.87.1**, confirmed from
installed public package metadata. Public tag `v0.87.1` resolves to commit
`f07218c4d4bbc12bef056a7058c3dd49dfe41abe`. Installed `rpc.md`, `rpc-commands.md`,
`sdk.md` and `extensions.md` byte hashes match that commit. This installation
contains a packaged executable, not unpacked `dist` source; implementation
inspection below used the public tagged repository, not private repositories.
No executable/source equivalence or runtime qualification is claimed.

Read-only document/source lanes ran in parallel. Native subagent tools were
unavailable; no child researchers or Pi/model processes were launched. Relevant
Pi Markdown pages were read completely, including linked protocol, session,
security and interactive references. No user state, credentials, configuration
or installations were changed. This is research, not an approved implementation
plan or independent review.

Normative constraints: [P03 §§3.1–3.5](../planning/full-product/03-managed-agents.md),
[cross-phase wake contract](../planning/full-product/contracts.md#4-host-wake-and-backend-support-are-different),
[terminal evidence/ownership](../terminal-modes.md), and
[execution/recovery](../plan.md). Managed Pi remains required in both modes,
including native Windows. **Windows qualification is blocked: no native Windows
machine/GUI access was available to this research.** All runtime probes are unrun.

## Native API decision matrix

“Documented” and “source-confirmed” below describe API evidence, not tested support.

| Need | Headless RPC | Same-process visible TUI extension | Decision / remaining gap |
| --- | --- | --- | --- |
| Launch | `--mode rpc`; long-lived stdin/stdout JSONL. | Normal Pi with terminal stdin/stdout and explicit `--extension`; `ctx.mode === "tui"`. | Documented distinct modes. Opening RPC in a tab does not create a real TUI. Keep TUI stdio attached to the selected terminal. [D1–D3] |
| Existing conversation / text follow-up | Repeated `prompt` on the same process/session; `steer` and `follow_up` are queues. | `pi.sendUserMessage(text)` targets the active session; `deliverAs: "steer"` or `"followUp"` queues during streaming. | No new process/session for ordinary follow-up. Keep ATF jobs queued in the daemon until reconciled idle rather than preloading Pi's queues. [D2, S1–S2] |
| Literal text | RPC `prompt` dispatches extension commands and expands skills/templates; no documented RPC expansion-disable field. | `sendUserMessage` defaults `expandPromptTemplates` to false in this version. | Do not treat RPC slash-leading input as guaranteed literal text. An allowlisted extension operation can supply literal semantics; input handlers may still transform it. [D2, S2] |
| Command acknowledgment | Optional command `id` is echoed in `response`; prompt success means accepted, queued or handled. | Public `pi.sendUserMessage` / `pi.sendMessage` return **void**; asynchronous failures go to runtime extension-error reporting. | Extension function return is not native acceptance. Observe authenticated attempt binding and native lifecycle evidence; timeout is uncertain, not permission to resend. [D2, S1–S2] |
| Status and final result | `get_state`, `get_messages`, `get_entries`, `get_last_assistant_text`; consume events continuously. | `ctx.isIdle()`, `ctx.hasPendingMessages()`, read-only `ctx.sessionManager`, lifecycle subscriptions. | `message_end` is a completed message; **`agent_settled`**, not `agent_end`, ends automatic work. Inspect error/aborted stop reasons; settlement is not task success. [D2–D4] |
| Correlation | Session ID is available; ordinary events do not echo prompt request IDs. Wire turn events have no durable job/run ID. | `input.source` distinguishes interactive/RPC/extension; extension `turn_end` has `turnIndex`, `messageEntryId`, `toolResultEntryIds`. Custom messages retain structured `details`. | Native entry IDs help, but neither source category nor resetting turn index authenticates an ATF job. See correlation probe below. [D4–D5, S1–S2] |
| Interrupt | `abort` waits for idle. **Queued messages can continue** unless `clear_queue` precedes abort. Direct RPC bash has separate `abort_bash`. | `ctx.abort()` is void. TUI binding restores queued messages to the editor, clears queues and requests session abort. | Neither API accepts an expected native turn ID. Adapter must fence expected activity locally; never interrupt whatever happens to be active. Tool/descendant cancellation remains unproven. [D2, S3] |
| Whole-agent stop | Closing stdin requests shutdown/disposal. | `ctx.shutdown()` requests graceful exit; busy TUI defers shutdown until settlement. | Neither is confirmed process-tree termination. Journal separate authorized stop; observe process and descendants. No automatic interactive kill on daemon recovery. [D2, S3] |
| Mixed human input | No built-in Pi TUI; external clients must identify their own callers. | Native editor remains usable. `input`, `user_bash`, session switch/tree/fork and UI events expose relevant activity. | `input` alone misses commands handled before that hook. Persist foreign activity and pause machine follow-ups even after idle; race-time mixed results become uncertain. [D6, S1–S3] |
| Approvals/questions | `extension_ui_request` has unique ID; reply with matching `extension_ui_response`. | `tool_call` can block; `ctx.ui.select/confirm/input/editor` support human decisions. Source also exposes `ui_prompt_start/end`. | Pi has no universal per-tool approval gate. Generic TUI UI events have kind/title, **not request IDs or a generic remote-answer API**. Qualify owned approval extensions separately. [D7, S1] |
| Reconnect / resume | Stdio is not a reconnect endpoint. EOF shuts down RPC. `--session` restores history in a new process, not a live process. | Extension can reconnect a scoped socket while TUI survives; session/reload invalidates old contexts. | Client reconnect through surviving daemon is distinct from daemon restart and backend restart. No second Pi may open the same live conversation to “reconnect.” [D2–D5] |
| Native wake | An authenticated runner can deliver a notice through native prompt control, subject to run fencing. | `pi.sendMessage({customType, content, display, details}, {triggerTurn: true})` can start an idle model turn; busy delivery can use `followUp`. | Native injection primitive exists; authenticated wake service does not. Full wake/catch-up contract and separate Pi lead-host qualification remain unverified. [D3, S2] |

`ctx.hasUI` is **true in RPC** because supported dialogs can be forwarded. It does
not prove a visible terminal. SDK `createAgentSession()` embeds a conversation in
Node/Bun; it neither attaches to an existing CLI nor makes a .NET process a Pi
host. SDK is unnecessary for the first .NET headless adapter. [D1, D3, D7]

## Minimal .NET path and transport choice

1. **Explicit headless:** the daemon's bounded C# runner owns one Pi RPC process,
   serializes allowlisted mutations, continuously drains stdout/stderr and maps
   JSONL responses/events using `System.Text.Json`. Split on LF, accepting CRLF;
   preserve Unicode U+2028/U+2029 inside strings. Bound frames, output, waits and
   queues. Subscribe before send. Persist sessions in a test-owned location,
   rather than copying the documentation's ephemeral `--no-session` example.
   No TypeScript client library or custom model loop is required. Direct stdio
   does not promise daemon-crash survival or reconnectable pipes.
2. **Required interactive:** launch the real TUI in Herdr / selected visible
   Windows or macOS tab. Load one explicit, trusted control extension; retain
   terminal stdio and use a separate host-local channel. Begin resources from
   `session_start`, close them idempotently at `session_shutdown`, and rebind on
   reload/session replacement. Do not wrap RPC in a replacement terminal UI.
3. **Prefer a daemon-owned IPC listener and extension connection:** Windows
   user-restricted named pipe; Unix private-domain socket. Public Node `node:net`
   supports those transports [N1]. C# `NamedPipeServerStream` / Unix-domain
   `Socket` are integration candidates, not tests. This keeps ACL/endpoint
   creation in .NET and the extension transport small. A terminal-local extension
   reconnects after daemon loss; it does not own the job DB or dispatch schedule.
4. **Single owner is a protocol invariant, not a socket option.** Authenticate
   both endpoints through a reviewed private bootstrap, bind agent/session/process
   incarnation and daemon generation, permit one machine controller, serialize
   effects, reject stale sequences and duplicate/competing owners. Re-registration
   after restart must prove the survivor and replay history before new sends.
   Pipe names, PID, session name, `input.source` and Node `exclusive` alone are
   not authentication. Do not expose raw Pi RPC as an administrative passthrough.
5. **Loopback TCP is technically possible**, using the same native extension
   calls, but needs mutual authentication, exact loopback binding, bounded framing
   and browser-origin protections if HTTP/WebSocket is chosen. Existing
   [plan §3.3](../plan.md#33-local-ipc) says no TCP ports; using loopback would need
   an explicit reviewed decision. It is not the default recommendation.

A small TypeScript extension is a **non-C# dependency/language exception requiring
explicit approval before implementation**. All daemon orchestration, storage and
probe harnesses remain C#/.NET. Without that exception, the documented headless
path remains available but required interactive control stays blocked. .NET 11
and published AOT compatibility must be tested separately; Pi/Node are external
runtime dependencies, not compiled into the .NET binary.

Bootstrap secrets must not enter argv, logs or inherited tool environments.
Use test-owned resource/config/session roots, an explicit discovery allowlist and
credential provisioning approved separately. `--no-extensions` still permits
explicit `-e`; project trust is not a sandbox, and context files load separately.
Same-user secrets do not isolate a malicious process with the user's permissions.

## Hard gaps: correlation, replay, approvals and wake

**Correlation:** stock RPC request IDs stop at command replies. For one exclusive
headless session, serialized dispatch plus complete observed lifecycle is a
candidate mapping, not a durable native delivery receipt. Lost replies or event
gaps must quarantine the attempt.

For interactive control, probe two supported paths rather than inventing a Pi
`runId`: ordinary `sendUserMessage` with a private in-process pending-attempt
record, and `sendMessage` with authenticated attempt metadata in custom-message
`details`. The latter preserves structured metadata without depending on a
model-written/text marker; custom content is converted into user-role model
context. It is not identical to an ordinary user transcript entry. Establish the
mapping to native session/branch/message entry IDs and final settlement before
allowing completion. `input.source === "extension"` identifies all extensions,
not ATF. Human input, another extension, branch changes, delayed finals or session
replacement invalidate attribution unless positively reconciled. Pin/allowlist
extensions; do not assume handler ordering eliminates asynchronous races.

**Replay:** session JSONL has stable entry IDs and a branch tree; `get_entries`
includes abandoned branches, and an unknown cursor fails rather than returning
empty history. It is not a persisted settlement-event journal. Source
`SessionManager._persist` delays a new file's initial flush until an assistant
message exists and uses synchronous writes without an explicit fsync in that
method [S4]. Therefore `pi.appendEntry()` before first delivery is not sufficient
commit-before-effect evidence, nor proof of power-loss durability. Keep ATF intent
in SQLite first. Probe a bounded private lifecycle spool or verified custom final
entry written from native settlement observation; missing/torn/full-disk evidence
must produce reconciliation. A final assistant text alone does not prove no
queued automatic work followed. Do not claim zero-loss replay.

**Approvals:** the bundled permission-gate example shows a pattern check followed
by a human dialog, not a complete authorization policy. RPC dialog IDs correlate
UI answers but do not inherently bind the requested action to an ATF run. TUI
`ui_prompt_start/end` can support an honest waiting indicator, not arbitrary
remote approval. An owned approval extension would need durable request identity,
expected generation, action/tool binding, expiry, human-versus-machine arbitration
and lost-answer handling. Unknown third-party dialogs remain explicitly
unsupported for automatic response; do not auto-approve or fabricate `running`.

**Wake:** use an authenticated push channel to the in-process extension, not the
bundled file-watch example. Commit messages first; coalesce bounded notice-only
wakes, reject stale registrations and fetch actual bodies through authenticated
read/ack. Idle `triggerTurn: true` supplies native wake; busy `followUp` must not
be injected into ambiguous human activity. `notify()` only updates UI; `nextTurn`
does not itself wake an idle model. Successful injection is neither read/ack nor
model action. Managed-backend wake and P06 Pi lead-host wake require separate
platform evidence; neither is established by the public PR #70 reference.

## Bounded next probe — proposed, not authorized or run

**Stop before coding:** obtain the small-extension exception, independent safety
plan review, disposable profiles, tiny model budget and actual Windows access.
Do not launch another generic roadmap exercise.

- **Offline C# fixtures first:** missing/duplicate IDs, events before command
  reply, retries after `agent_end`, terminal error/abort, mixed input, stale
  generation, oversized JSONL, lost response and unknown replay cursor. Pass:
  no false completion, blind resend or cross-session control.
- **One Pi per selected mode, no recursive agents:** one harmless initial prompt
  and one same-conversation follow-up. Record session/branch/entry identity,
  acceptance evidence, authoritative message and `agent_settled`. In visible
  mode, human input races a send, then a session change and extension reload.
  Pass: human remains able to use TUI; automatic jobs pause on foreign activity;
  ambiguous attribution stays uncertain. Compare custom-message and ordinary
  user-message correlation paths before selecting one.
- **Control/recovery:** targeted interrupt preserves Pi/tab; separate authorized
  stop checks owned descendants and neighboring tabs. Kill only the test client,
  then separately restart the daemon while TUI survives and finishes work.
  Rebind/replay or quarantine, never duplicate spawn/send. Exercise a pending
  owned approval, an idle notice wake and bounded busy-notice coalescing.
- **Native Windows is mandatory and currently blocked:** pin Windows/terminal,
  Pi distribution, Node and wrapper versions. npm distribution requires Node
  `>=22.19.0`; native tools use Git Bash by default, optional PowerShell [D8].
  Test `.cmd` resolution and wrapper exit; consider direct verified `node.exe`
  plus the package's declared CLI entry rather than shell-string launch. Test
  spaces, quotes, metacharacters, newlines, Unicode and non-ASCII profiles;
  terminal/tab-to-process binding; restricted pipe ACLs, spoofed/replaced
  endpoints and competing owners; credential/environment sentinels; Node/tool
  grandchildren, PID reuse and Job Object assumptions. Repeat follow-up/final,
  interrupt/stop/reconnect in both modes and the intended desktop launch context.
  WSL, Linux runs and Windows cross-builds cannot pass these gates.

Stop the probe at the first unsafe origin/finality/ownership gap and retain a
small sanitized trace. No automatic fallback to headless or keystroke injection.
This research ran only document/source checks; no build, runtime, model, native
Windows, process-recovery or AOT tests were run.

## Versioned sources

All Pi links below are pinned to `v0.87.1` (commit above), not mutable `main`.

- **D1:** [README](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/README.md), [CLI](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/cli.md), [CLI integration](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/cli-integration.md).
- **D2:** [RPC](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/rpc.md), [commands](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/rpc-commands.md).
- **D3:** [Extensions](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/extensions.md), [SDK](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/sdk.md), [TUI](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/tui.md).
- **D4:** [JSON events](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/json.md), [message types](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/message-types.md).
- **D5:** [Sessions](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/sessions.md), [session format](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/session-format.md).
- **D6:** [Usage](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/usage.md), [keybindings](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/keybindings.md), [terminal setup](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/terminal-setup.md).
- **D7:** [RPC extension UI](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/rpc-extension-ui.md), [security](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/security.md), [permission-gate example](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/examples/extensions/permission-gate.ts).
- **D8:** [Windows](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/windows.md), [package metadata](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/package.json), [configuration](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/configuration.md), [environment](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/environment-variables.md).
- **S1:** [Extension types](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/src/core/extensions/types.ts): `ExtensionContext`, `InputEvent`, `TurnEndEvent`, `UIPromptStartEvent`, `ExtensionAPI`.
- **S2:** [AgentSession](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/src/core/agent-session.ts): `prompt`, `sendUserMessage`, `sendCustomMessage`, `_emitAgentSettled`, `runner.bindCore`; [send-user-message example](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/examples/extensions/send-user-message.ts).
- **S3:** [InteractiveMode](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/src/modes/interactive/interactive-mode.ts): `bindCurrentSessionExtensions`, `restoreQueuedMessagesToEditor`, `agent_settled`; [RPC mode](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/src/modes/rpc/rpc-mode.ts): `handleCommand`, `shutdown`, `onInputEnd`.
- **S4:** [SessionManager](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/src/core/session-manager.ts): `_persist`, `_appendEntry`, `appendCustomEntry`.
- **N1:** [Node 22 net IPC](https://nodejs.org/docs/latest-v22.x/api/net.html#ipc-support), including named-pipe lifetime and `server.listen` options. This is a rolling Node 22 reference, not tested Pi/.NET interoperability.
