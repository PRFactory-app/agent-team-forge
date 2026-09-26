# M0 interactive spike report — Claude and Codex TUIs in Herdr (Linux)

Date: 2026-09-26. Author: Claude Opus 5.5 (Claude Code), acting as implementer.
Status: **ready for opposite-family (GPT/Codex) code review, not self-approved.**
The user waived plan review for this spike only.

Editorial note: machine-specific session details have been generalized for
public documentation. Findings and gate results remain the implementer's
claims pending independent code review. Raw evidence is local verification
material, not cleared for public publication; see [the publication boundary](README.md#publication-boundary).

**Post-review repair:** the independent review ([CODE-REVIEW.md](CODE-REVIEW.md)) did not
approve this spike. Its blocking findings were repaired afterwards; per-finding dispositions
and test evidence are in [FIXES.md](FIXES.md). Sections below describe the original run and its
evidence; where behaviour changed (claims, native binding, foreign-turn pause, ownership,
`--allow-busy` removed, Claude cancel reported unsupported), FIXES.md is authoritative.

## 1. Outcome in one paragraph

On Linux with Herdr, both backends ran as **real interactive TUIs** that a human can
attach to, while a separate C# process sent machine follow-ups into the **same
conversation**. It observed **authoritative completion** and survived **SIGKILL of
the controlling process** without losing or duplicating work. **Codex** has a
coherent native path: its TUI is a `--remote` client of an app-server in a
Herdr pane, and the harness is a second app-server client. That path gave
correlation (`clientUserMessageId`), completion, and targeted cancel
(`turn/interrupt`). **Claude** has a native delivery path (the documented
per-session inbox socket, accepted by a per-launch `crossSessionInbound: accept`)
and a native completion path (per-launch command hooks), but **no native targeted
cancel**: an Esc keystroke sent by Herdr stops the turn but leaves no
authoritative evidence. That gate is **blocked**. Human input in the tabs was
**not verified by a human** and is **unrun**. Several defects in the backends and
Herdr were found and handled; they are listed in §5.

## 2. Environment and exact versions

| Item | Version / value |
| --- | --- |
| OS | Linux 7.2.5-3-omarchy, x86_64 (single machine; no Windows/macOS access used) |
| .NET | SDK 10.0.401, runtime Microsoft.NETCore.App 10.0.12 |
| Herdr | 0.8.2 (protocol 20); 0.9.1 was offered by its updater and **not** installed |
| Claude Code | 2.1.283; model `haiku` (TUI shows Haiku 4.5); default ("manual") permission mode |
| Codex CLI | 0.157.1; model `gpt-6-luna`, `model_reasoning_effort=low` |
| Test packages | xunit 2.9.3, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.0.1 (all from the local NuGet cache) |
| Model budget used | About 14 Claude (Haiku) turns and 27 Codex (Luna low) turns, all short, no file-changing tasks |

The spike isolates Codex from the user's MCP servers with
`-c mcp_servers.win-agent-teams.enabled=false -c mcp_servers.node_repl.enabled=false`
and starts Claude with `--strict-mcp-config`. The app-server still started a
`cua-repl`/`node_repl` child (a Codex feature, not the disabled MCP server).

## 3. What was built (C#/.NET)

Launch provider and control/result transports are separate.

| Axis | Claude | Codex |
| --- | --- | --- |
| Launch provider | `herdr tab create` (one tab per agent, env via `--env`) → `herdr agent start --kind claude -- --session-id <uuid> --settings <file> --model haiku --strict-mcp-config` | Tab with an app-server pane (`codex app-server --listen unix://…`) and a split TUI pane (`herdr agent start --kind codex -- --remote unix://… -m gpt-6-luna`) |
| Stable identity | Our pre-assigned session id = `SessionStart` hook `session_id` = Herdr `agent_session`. The hook parent PID is verified by `/proc/<pid>/cmdline` containing the session id. The binding stores PID + kernel start time. | App-server thread id, discovered with `thread/loaded/list` (exactly one loaded thread per per-agent app-server), plus Herdr pane `terminal_id`. |
| Follow-up | Inbox socket from `CLAUDE_CODE_MESSAGING_SOCKET` (recorded by the `SessionStart` hook). One JSON line is written and the socket half-closed. The token is never read or stored. | `turn/start` with `clientUserMessageId = job id` on the same thread. |
| Delivery evidence | `UserPromptSubmit` hook whose prompt carries `[atf-job:<id>]`. Its `prompt_id` becomes the turn key. | `turn/start` response turn id. The listed user message carries `clientId = job id`. |
| Authoritative completion | `Stop` hook with the same `prompt_id` (`last_assistant_message`, capped at 4000 chars). `StopFailure` means failed. | `thread/turns/list` status **only when `completedAt` is set** (§5 F2). `turn/completed` notifications only wake the loop. |
| Targeted cancel | Candidate only: `herdr agent send-keys <verified pane> esc` | `turn/interrupt` |
| Approval/question | `PermissionRequest` and `Notification(permission_prompt)` hooks give a blocked state; never answered by the harness. | Server requests are surfaced and deliberately left unanswered, so the human decides in the TUI. **Not exercised live.** |

Durable-ish spike state (`.run/spike/state.json`, atomic replace under a lock
file) records `Accepted` before any dispatch and `Dispatching` before any
transport write. A backend-neutral reconciler (`Delivery/JobReconciler.cs`)
derives the job state only from correlated backend evidence. It never redispatches
anything that is not `Accepted`, never rewrites a terminal state, reports two turns
with one job id as duplicate delivery, and turns "attempted but no evidence" into
`NeedsReconciliation`, which later evidence can resolve.

### Tests (TDD for critical logic)

49 xUnit tests; the red phase was observed for each group before implementation.
They cover:

- Reconciliation and redelivery rules, including terminal finality, duplicates,
  and foreign or human turns.
- The job tag.
- Claude hook records: the token and raw prompt are never recorded, and results
  are bounded.
- Claude hook folding: `prompt_id` correlation, supersede, `SessionEnd`,
  `StopFailure`, blocked state, other sessions, and torn lines.
- The Claude inbox wire and real Unix-socket post: exact bytes, half-close, and
  "not sent" on connect failure.
- Inbox target checks: symlink, PID name, and non-socket.
- JSON-RPC: out-of-order correlation, errors, transport loss meaning "may have
  been delivered", server requests never auto-answered, and malformed frames.
- WebSocket over a real Unix socket: a message over 64 KiB with multibyte text
  and odd server fragmentation.
- Codex turn evidence, including the transient-listing rule.
- Process identity: parsing and PID reuse.
- Launch-environment scrubbing.

No tests assert prose, UI text, or formatting.

## 4. Results

"Live" means that it ran against the real TUIs. "Run 2" is the clean-environment
run (§5 F7). Run 1 evidence is kept but was produced with a contaminated
environment. Jobs `x-j2`, `x-j5`, `x-j6`, `x-j8`, `x-j9`, and `x-j10` in run 1
were wrongly persisted as `Interrupted` before the F2 fix. They are retained as
evidence of that defect.

| Gate / case | Result | Evidence |
| --- | --- | --- |
| T04 real visible TUIs launched by the spike | **Pass (Claude, Codex)** — real TUIs in Herdr tabs of a dedicated session; `herdr session attach atf-m0-spike` shows them. Not a log tail. | `evidence/run2/launch.jsonl`, `pane-*.txt` |
| Stable identity/binding | **Pass** for Claude (three-way id agreement + PID/start time + cmdline). **Pass with caveat** for Codex (single-thread discovery per app-server; Herdr exposes no Codex `agent_session` for `--remote`). | `launch.jsonl` |
| T06 / L01, L02 (interactive half) first job → result → follow-up in same conversation | **Pass both** — follow-up answered with the previous reply's first word (`FOLLOWUP-2 READY-1`). | `evidence/run2/l01-l02-first-and-followup.jsonl` |
| T11 Unicode, quotes, `$HOME`, backticks, `$(id)`, real newlines | **Pass both** — SHA-256 of what Claude's hook saw and of Codex's stored user message equals what was sent; nothing shell-interpreted. | `evidence/run2/t11-*.{txt,jsonl}` |
| Idempotent retry / conflicting payload | **Pass** — retry returned stored result, not redispatched; same id + different text refused. | Run 1 console output (not saved); job history in `evidence/run1-inherited-env/state.json` |
| C08/C09-style crash barriers (SIGKILL after dispatch intent / after transport write) | **Pass both** (run 1) — after-post recovered `Completed` from backend evidence without resend (Codex: one turn with that `clientId`); after-intent → `NeedsReconciliation`, retry blocked. | `evidence/run1-inherited-env/live/crash-points.jsonl` |
| T07-style controller death (SIGKILL harness mid-turn, new process retrieves result) | **Pass: 3× Codex, 3× Claude** (1 each in run 1, 2 each in run 2); the TUIs kept running. One earlier Codex attempt was invalid (process not killed) and is labelled so. | `evidence/run2/bridge-kill-*.jsonl`, `evidence/run1-inherited-env/live/bridge-kill-*.jsonl` |
| L04 / T08 targeted cancel, neighbours unaffected | **Codex pass** — `Interrupted` with `completedAt`; Claude tab/process unaffected. **Claude blocked** — Esc stops the turn (TUI "Interrupted", Herdr `done`) but no hook fires; job stays unresolved → `NeedsReconciliation` once superseded; Codex unaffected. | `evidence/run2/l04-cancel.jsonl`, `codex-tui-after-interrupt.txt` |
| Cancel after completion | **Pass (Codex)** — refused honestly; job reconciled to `Completed`. | `l04-cancel.jsonl` (`x-c1`) |
| L05 approval without a lead (Claude) | **Pass (Claude)** — blocked state surfaced, nothing auto-approved, file not created; declined via Esc. **Codex unrun.** | `evidence/run2/l05-claude-approval-block.jsonl`, `claude-approval-pane.txt` |
| T15 human + machine concurrency | **Partial** — policy implemented: refuse while busy/blocked; explicit `--allow-busy` override. Human turns are observed as turns without a job id. Not exercised with a real human. | code + `l04-cancel.jsonl` |
| T05 human types in the agent tab | **Unrun** — requires a human. See §7. | — |
| T09 closed tab / terminal server death, T10 daemon restart with live tabs, T12 mode change | **Unrun**. A Herdr server restart was observed incidentally (§5 F6). | `evidence/herdr-restore-stale-metadata/` |
| T01–T03, T13, T14 setup/service | **Out of scope for this spike; unrun.** | — |
| Headless modes, SQLite, MCP bridge, real lead agent, L06, L07 wake | **Unrun** (not this spike's scope). The "bridge" here is the C# CLI harness, not a real MCP lead. | — |
| Windows / macOS | **Blocked** — no environment used; no support claimed. | — |

## 5. Findings

**F1 — Codex `resume --remote` cannot attach to a fresh `thread/start` thread.** A
thread created with `thread/start` has no rollout until its first turn, so `codex
resume <id> --remote` failed with "no rollout found". The TUI therefore starts
its own thread on the shared app-server, and the harness discovers it. One
app-server per agent makes the discovery unambiguous. More than one loaded thread
is refused as ambiguous.

**F2 — Transient wrong terminal status in `thread/turns/list`.** On the connection
that sent `turn/start`, a finishing turn was listed as
`status: "interrupted", completedAt: null` with its final answer already present.
About 80 ms later, `turn/completed` reported `completed`. This happened in 6 of 7
`send --wait` runs before the fix and in 3/3 traced runs after it. A 100 ms
poller on a second connection did not see it. **Rule adopted:** a listed terminal
status is final only when `completedAt` is set. Genuine interrupts had
`completedAt` set. This is a backend defect or undocumented behaviour; report it
upstream or keep it covered by a contract fixture.

**F3 — Codex history APIs before the first user message.** `thread/turns/list`
answered `-32601 "list_turns is not supported yet"` in one run and `-32600 "not
materialized yet"` in another. `thread/resume` answered "no rollout found".
`thread/read` with `includeTurns` failed the same way; that was tested only
before the first turn. Any first-page listing
error is now treated as absence of evidence, which can never become success.
Subscription is best-effort, and `turn/start` works without it.

**F4 — Codex thread status lags turns.** `thread/read` reported `idle` while
the latest turn was already listed `inProgress`. The busy check uses both.

**F5 — Herdr misreports Codex `--remote`.** Herdr 0.8.2 classifies the idle
`--remote` TUI as `working`, so `agent start` times out. It also labels the
app-server pane as a Codex agent. In both the probe and the spike, the TUI stayed
`working` after completion. Herdr status is used only as an advisory signal.
Claude's `idle`/`done`/`blocked` matched the visible TUI state in these runs.
After Esc, Herdr showed `done` while the hooks still showed an open turn.

**F6 — Herdr restores stale agent metadata.** After our session's server was
stopped and started again, Herdr restored the workspace from `session.json`. It
listed a pane as an idle `claude` agent named `cl1` with the old session id, and
likewise a Codex pane with the old thread id. Those panes ran only `bash`, and
`pane read` said the pane did not exist. Herdr names or `agent_session` values
are therefore not proof of a live agent. The spike binds with `terminal_id` + PID
start time, and its teardown now deletes its own session.

**F7 — Security: launching Herdr from an agent session leaked that session's
inbox credentials into every pane.** The first Herdr server was started from
this Claude Code session. It inherited `CLAUDE_CODE_MESSAGING_SOCKET`,
`CLAUDE_CODE_MESSAGING_TOKEN`, `CLAUDE_CODE_CHILD_SESSION`, and team variables.
Every pane, including Codex panes, could therefore post into the launching
session. The spawned Claude also disabled transcript saving ("inherited
CLAUDE_CODE_CHILD_SESSION marker"). The previous probe session had the same
symptom. The spike now scrubs this context (`Herdr/LaunchEnvironment.cs`, test
covered). In run 2, zero such variables reached the Herdr server, Claude,
Codex app-server, or Codex TUI, and transcripts were saved. **Product
requirement:** the daemon and launcher must start terminal servers from an
explicit environment allowlist, not the environment of whatever client asked.

**F8 — Claude delivery semantics.** The inbox socket gives no acknowledgment. A
write is only "written". The spike treats the `UserPromptSubmit` hook with the
job tag as delivery evidence. The socket and the `auth` line are documented for
scripts; the `{"type":"user","message":{…}}` line format was carried over from
an earlier exploratory probe and observed working, but it is not spelled out
in the reviewed public documentation. That is an adapter risk. The documented receiver behaviour gives
more reasons never to resend blindly: identical messages repeated in a short
window are dropped, and the sender is rate-limited. The receiving Claude treats
the text as a peer message: it cannot approve permission prompts and is told not
to change configuration. That is a useful safety property for machine
follow-ups. A message sent during an active turn would be injected between tool
calls, probably without a `UserPromptSubmit` hook, so the spike refuses sends
while busy.

**F9 — Claude has no native targeted cancel.** No interrupt message exists on
the inbox socket, and Claude runs no `Stop` hook after a user interrupt. Esc sent
by Herdr to the verified pane interrupted the turn visually. The hook log still
showed an open turn, so the session looked busy until an explicit override, and
the interrupted job resolved only to `NeedsReconciliation` when superseded. The
session transcript contains "[Request interrupted by user]" markers, but that is
an undocumented, backend-owned file format and is not used. **This keeps
authoritative Claude cancel as a blocked/no-go gate** for the chosen transport.
Options for a decision: accept keystroke cancel with explicit
"interrupt requested, outcome unknown" semantics; investigate an officially
supported interrupt path; or kill the verified Claude process (whole-agent stop,
not turn cancel).

**F10 — Completion is not goal success.** One Codex job "completed" with a
refusal ("I can't fit all the requested integers…"). Completion means the turn
ended normally, not that the instruction was fulfilled.

**F11 — Codex interrupt versus running tools.** The interrupt landed before the
requested `sleep 45` tool started. No leftover `sleep` process existed, but
killing a running tool's child process was **not** demonstrated.

**F12 — Hook PID binding is fragile.** The hook's parent PID equals the Claude PID
only because the hook command is a single simple command that the shell execs.
The launch step verifies this and fails otherwise.

## 6. Quality gates

| Gate | Result |
| --- | --- |
| `dotnet format AtfSpike.slnx --verify-no-changes` | Pass |
| `dotnet build -c Release -warnaserror` (analysis level latest-recommended, code style enforced, trim/AOT analyzers via `IsAotCompatible`) | Pass, 0 warnings |
| `dotnet test` | Pass, 49/49 (`evidence/gates.log`) |
| Native AOT publish and published-binary smoke | **Unrun.** The ILCompiler package is not in the local NuGet cache, and downloading dependencies was out of scope. Live runs used the framework-dependent Release build via `dotnet`. |
| Markdown lint / link check | **Unrun**; no tool is set up. |

## 7. Human verification needed (T05, T15)

The earlier session from the original run is preserved but is not operated by the repaired
build (its schema-1 state is not adopted). Use the repaired build's own session instead:

1. `scripts/atf-spike.sh status` shows the owned session name; attach with
   `herdr session attach <that name>`.
2. Type a short prompt in the Codex TUI pane (and, optionally, in the Claude tab).
3. `scripts/atf-spike.sh send <agent> <job> ...` must now return `refused_foreign_turn`
   or `claim_refused` (paused), both while the human turn runs and after it completes.
4. After checking the TUI yourself, `scripts/atf-spike.sh reconcile-idle <agent>` verifies
   the binding and an idle, readable history, acknowledges the human turns, and lifts the
   pause. The next `send` proceeds.
5. `scripts/atf-spike.sh teardown` stops and deletes only the proven-owned session.

A Herdr-typed foreign prompt exercised steps 3–4 for Codex (FIXES.md), but that is not
human verification. Until a human does this, T05/T15 remain unrun.

## 8. Previous Python work and process handling

- Replaced and deleted the previous agent's Python harness:
  `atf_spike/__init__.py`, `claude_hook.py`, `codex_rpc.py`, `wsuds.py`, and
  `__pycache__/*.pyc`. Their SHA-256 values are in
  `evidence/previous-python-probe/deleted-python-sources.sha256`.
- Useful discoveries carried over into C#:
  - The Codex app-server's WebSocket-over-Unix-socket transport.
  - Per-launch Claude hooks via `--settings`.
  - `crossSessionInbound: accept`.
  - The inbox socket path from `SessionStart`.
  - `prompt_id` correlation.
  - Never auto-answering server requests.
- Probe data kept as evidence: `.run/probe/` is untouched, and it still
  references the deleted Python hook. Its events and captures are copied to
  `evidence/previous-python-probe/`.
- The previous agent's Herdr session `atf-m0-probe` was stopped. Its provenance
  was verified first:
  - It was created at 11:29:28Z.
  - All panes ran in `.run/probe`.
  - Its Claude session id matched `.run/probe/claude-sid`.
  - Its app-server socket belonged to its own PID.

  Pane contents, process tree, and server log were captured first.
- Left untouched: pre-existing user Herdr sessions and an unrelated Codex
  app-server daemon. Nothing outside the verified probe ownership was killed.
- This spike's own session `atf-m0-spike` was deleted once (after F6) and
  recreated for run 2. It is currently running (§7).

## 9. Remaining risks and next steps

1. **Decide Claude cancel semantics (F9).** This needs a human/product decision;
   the gate is blocked as it stands.
2. Run T05/T15 with a human (§7), then T09 (close a tab, kill the Herdr server)
   and T10 (reconciliation after a daemon restart, using F6).
3. Exercise Codex approvals/questions (L05) without a lead and confirm that the
   TUI user can answer while the harness stays silent. Demonstrate interrupting
   a running tool and verify that no child process remains (F11).
4. Report or track F2/F3 upstream. Pin Codex 0.157.1 behaviour in contract
   fixtures and re-verify on upgrade. Herdr 0.9.1 was not tested.
5. Move from the CLI harness to the M0 vertical slice: SQLite durable acceptance,
   a daemon-owned dispatcher, and a thin MCP bridge with a real lead agent killed
   mid-turn (L03 needs three repetitions with a real lead).
6. Replace the environment denylist with an allowlist in the product launcher
   (F7). Set up the AOT publish toolchain (needs the ILCompiler package; budget
   and approval) and test the published binary.
7. Record ADRs: the Codex remote-TUI + app-server adapter, the Claude
   inbox + hooks adapter and its limits, and Herdr as launch-only (its status is
   advisory).
