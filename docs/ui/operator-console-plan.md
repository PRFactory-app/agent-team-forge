# Text-only operator console — plan and mockup

**Status:** proposal for independent plan review; no production web implementation.
User-selected planning lane: Pi / GPT-6 Astra / medium. This task explicitly permits
HTML/CSS/minimal JavaScript for a mockup, not runtime implementation.

## 1. First deliverable: open the mockup

Open [operator-console-mockup.html](operator-console-mockup.html) directly as a local
file in a browser. No server, build, installation or network is needed. It contains
only synthetic Claude/Codex/Pi agents and Linux/Windows/macOS fields. Those fields
illustrate layout, **not verified platform or backend capabilities**.

Try selecting an agent, reading its log/result, submitting disposable demo text,
interrupting a synthetic turn, and confirming a whole-agent stop. Switch the
connection scenario to stale/disconnected/uncertain; mutations become unavailable.
Reset clears demo state. All interaction is bounded, in memory and fake. Do not
enter secrets or real work. Reload loses everything. No requests, browser storage,
external assets, telemetry or credentials are used.

## 2. Scope decision and traceability

The new user request overrides the earlier no-dashboard exclusion **only for this
small local text operator console**. Keep real agent TUIs, explicit launch modes,
CLI and MCP; this is not a terminal replacement, keystroke proxy, agent launcher,
approval editor, general dashboard, graph view or remote administration surface.
Initial use cases: inspect agents like `check_agent`, submit a human follow-up, and
stop one agent like `kill_agent`; distinguish turn interrupt visibly.

**F27 (added to the shared scope):** “Authenticated loopback text operator console for bounded
agent inspection, durable human follow-up and safe targeted agent stop through
existing application contracts; browser loss cannot cancel accepted work.” The
parent has added F27 and reconciled the shared scope/exclusions. Mockup approval
and independent design/security review still precede runtime implementation. F27 depends on F01–F05, F08–F09, F13–F19, F23–F24 and F26;
it does not waive F06 interactive TUIs, F12 native wake or F19 Windows managed Pi.

Context: [architecture](../architecture.md), [scope](../product-scope.md),
[roadmap](../roadmap.md), [cross-phase contracts](../planning/full-product/contracts.md),
[M0 integration](../spikes/m0-integration-plan.md), and [handoff](../../HANDOFF.md).
Current M0 fake-core/Linux experiments are not web or multi-platform acceptance.

## 3. Minimal architecture choice

**Recommend an explicitly started `Host: web` client role in the existing Host
executable**, serving a small embedded static page and thin same-origin HTTP
endpoints. It calls the same authenticated private IPC as CLI/MCP. Command names
remain provisional. No fourth production project, framework SPA, model loop,
second scheduler, DB reader or durable web-side command queue.

```text
local browser → loopback HTTP → Host: web → authenticated private IPC
                                                 ↓
                                          Host: daemon
                                          Business → DAL
                                          sole job DB owner
```

| Choice | Trade-off / decision |
| --- | --- |
| Separate Host web role | One extra local process and bounded IPC forwarding; isolates browser/parser/resource failures from daemon lifetime. Reuses client API, can be stopped without stopping agents. Recommended. |
| HTTP listener in daemon role | Fewer hops/processes, but adds browser attack surface, HTTP lifetime and resource pressure directly to work owner. Not worth coupling for this debugging surface. Reconsider only with measured benefit and reviewed isolation. |

Keep `Host → Business → DAL`. Host owns HTTP security, serialization and role
composition; web composition must never initialize DAL. Daemon Business owns
permissions, capabilities, admission, ownership and dispatch; DAL owns existing
atomic operations. Colocate endpoints with their feature rather than inventing a
parallel web business layer. CLI/MCP/web invoke equivalent use cases and return
consistent machine-readable errors. Web principal is bound by authenticated IPC,
not a trusted `operator: true`, team ID or browser-supplied role. Register/revoke
web-session authority through existing daemon identity policy, scoped to the
operator's allowed teams/actions; no broad shared daemon credential in browser.

## 4. Operator flows and API semantics

### Inspect

List authorized agents: stable ID, backend, team, OS, interactive/headless mode,
state and age. Detail shows session/run generation, ownership/capability evidence,
current job, bounded plain-text log and last authoritative result. Show unavailable
result separately from empty result. Snapshot includes daemon generation, sequence,
observation timestamp and capability reasons. “Connected” means transport health,
not fresh agent evidence; present observation age separately. Never infer completion
from log text, a live PID, a model statement or a successful HTTP response.

Start with bounded read-only snapshot requests while the page is visible (proposal:
one in flight per browser session, at most once per 2 seconds, bounded backoff).
This is human inspection, **not agent notification polling**: native session wake
remains unchanged. No SSE/WebSocket subsystem needed initially. If existing event
inspection is used, bound batches, show retention gaps and resnapshot after gaps
or generation changes. Never advance an agent's canonical read/ack cursor through
filtered browser inspection. Pause refresh in hidden tabs; on resume show stale
until resynced. Display permission denied, version mismatch, quota/truncation and
daemon unavailable distinctly; do not turn unknown state into idle.

### Human follow-up

Select an agent with proven reconciled idle and supported native follow-up, enter
text and submit once. Browser carries target identity, expected generation/revision
and a per-intent idempotency key. Daemon binds authenticated human principal and
origin, validates team permission, byte limits, budgets/deadline, foreign/human
activity policy and backend capability; atomically accepts job + key/fingerprint +
unattempted intent before acknowledgement. No arbitrary stdin bytes, keyboard
injection, permission override or bypass around the scheduler. Recheck eligibility
before effects so a human/native turn racing acceptance pauses or rejects dispatch
under the existing policy rather than interleaving prompts.

Show **accepted job ID**, then queued/attempt-started/backend-acknowledged/completed
or needs-reconciliation. Acceptance is not execution/completion. Lost response:
show “acceptance unknown”; query by the same intent key, never mint a fresh key
or resend uncertain work automatically. Same key + different payload conflicts;
same key + same payload returns the original job. Keep pending key only in memory;
after reload, use authorized recent-job lookup and reconcile before a new submission.
Browser/web process loss never cancels a committed job. Busy, foreign/human-active,
unsupported, stopped, stale, unowned or uncertain targets cannot receive follow-up.
Approval-required work remains visibly waiting; this console does not auto-approve.

### Stop versus interrupt

- **Interrupt turn:** request cancellation of the identified current run; agent may
  remain alive. Require supported native interrupt and current run/generation.
- **Stop agent:** terminate one verified-owned agent/session, not just its turn.
  Confirmation shows agent ID, backend, team, mode and generation; operator types
  the exact ID. Submit expected identity/revision plus an idempotent operation key.
- **Stop coordinator/daemon:** absent from this console. Never map agent stop to it,
  shared terminal/window teardown, name matching or PID-only termination.

Daemon reauthorizes and verifies native ownership immediately before action; a
changed selection/state invalidates confirmation. Unknown ownership, stale binding,
PID reuse, unsupported native control or uncertain identity fails closed. Only a
separately verified owned process boundary may be torn down; never kill a shared
terminal host. Do not silently escalate interrupt into kill. Show stop requested,
confirmed stopped, denied, unsupported or outcome uncertain; a sent signal is not
proof of exit. Losing connection while stopping leaves outcome uncertain until read.

## 5. Local web security and bounds

Required before any live mutation endpoint, including a localhost preview:

- Opt-in loopback listener only; bind numeric loopback, not wildcard/LAN. No remote
  option initially. Restrict exact canonical Host and port (and IPv6 only if explicitly
  configured); reject foreign Host/forwarded-host tricks and DNS rebinding. Do not
  trust proxy headers. No CORS access for arbitrary sites.
- Local OS-authorized launcher exposes a short-lived single-use pairing code for
  manual entry into the page. Never put credentials in URLs, argv, referrers or
  logs. Pairing is rate-limited, expires quickly and returns generic failure. It
  must not let an unauthenticated browser acquire authority merely by visiting.
- Pair through same-origin POST with exact Origin/Host checks; set opaque HttpOnly,
  SameSite=Strict, host-only session cookie and short idle/absolute expiry. Use
  Secure when HTTPS is enabled; plain loopback HTTP cannot rely on Secure cookies
  or port-isolated cookies. Enforce origin including port, use an instance-specific
  cookie name, rotate/revoke sessions, and document same-user malicious software
  as outside the sandbox guarantee. No credentials in localStorage/sessionStorage.
- Mutations, including pairing/logout, require exact non-null Origin, supported
  content type, bounded JSON and CSRF token in a custom header (pairing uses its
  one-time proof). Reject cross-site requests; GET never mutates. Token remains
  in page memory. Authenticate read endpoints too; cookie alone is not CSRF defense.
- Production page uses restrictive CSP (self assets, no inline executable code),
  `frame-ancestors 'none'`, no-referrer, nosniff and no-store for sensitive responses.
  The local-file demo's inline script is not a production CSP template.
- Treat prompts, logs and results as untrusted text: text nodes only, no Markdown
  HTML, executable links, terminal escape interpretation or ANSI/OSC hyperlink
  handling. Strip/visibly escape control sequences and bidi controls at presentation;
  redact backend secrets before export. Do not log prompt bodies or auth headers.
- Proposed display limits: 100 agents/page, 64 KiB text excerpt, 256 KiB snapshot,
  100 events/batch, two in-flight requests/session, 10-second request timeout and
  bounded global clients/rate. Show truncation/gaps and an explicit bounded next
  page, not an unbounded tail. Server enforces bytes and quotas, not just DOM caps.
  Freeze numeric policy with P01/P04 before implementation; console may lower but
  never increase shared prompt/frame/retention caps. Demo's 2,000-character input
  and finite samples are illustrative, not those API limits.

## 6. Practical vertical work packages

Independent major-plan review precedes implementation. Staffing remains Claude
runtime writers, independent Codex verification/integration, per the user's
latest role allocation. The user assesses the planning mockup; a separate Codex
session reviews the production design/security boundary. No review approval is
claimed here. Each package has focused red → green tests and a bounded handoff.

| Package | End-to-end deliverable / prerequisite | Exit tests |
| --- | --- | --- |
| UI-0: contract freeze | Parent lands F27 scope update and reviews web threat model, identity/bootstrap and limits. Consume P02 API/identity/durability, P03 ownership/capabilities, P04 human policy/quotas and P05 operator requirements. | Reviewed use-case/error mapping and supported-cell ledger; no unsafe backend assumed available. |
| UI-1: authenticated inspection | Host web role + bounded agent list/detail via existing IPC; synthetic backend first. P02 transport and P03 status; P04 team filtering. Add reusable scripts for doc/HTML/JS checks if absent. | U01, U02, U06; DB-access role test and published-binary smoke. |
| UI-2: durable follow-up | Same-session human job submission/status, lost-response recovery, inline reason states. P02 acceptance/idempotency + P03 native follow-up + P04 permissions/bounds/foreign activity. | U03, U04, U06 with fake barriers first, then native supported cells. |
| UI-3: targeted controls | Separate interrupt and confirmed stop, refreshed identity, fail-closed ownership. P03 controls/recovery + P04 authorization; no terminal-wide kill. | U05, U06; real owned disposable process plus unrelated terminal survivor. |
| UI-4: installed qualification | P05 opt-in launch/doctor/package/uninstall integration; P07 security/resource and P08 concise operator instructions. | U07, U08 across claimed native platforms/RIDs. Retire superseded web scaffolds only after replacement review and regression parity. |

No phase gate bypass: read-only/fake previews may be separately labelled checkpoints,
not complete P02–P05 or F27 acceptance. Cleanup removes duplicate experimental web
servers/helpers/assets, not recovery evidence, live sessions or this useful design
artifact by default. Product runtime stays in the real three-project solution.

## 7. Meaningful acceptance evidence

| ID | Focused test and observable invariant |
| --- | --- |
| U01 | Start web role with daemon unavailable: no SQLite open/create, no spawned daemon/job, explicit unavailable state. Kill browser and web role after commit; fresh CLI/web client still finds accepted job/result. |
| U02 | Unauthenticated reads/mutations, cross-site form/fetch, null Origin, wrong Host/port, rebinding host, expired/replayed pairing, forged principal/team and revoked cookie are denied without effects. Correct scoped request works. Assert status/error codes and absence of daemon effects, not prose. |
| U03 | Drop HTTP/IPC response after acceptance commit, retry same key concurrently: one job/intent/attempt only. Different payload conflicts. Crash before/after attempt marker preserves queued versus uncertain distinction; no blind prompt replay. |
| U04 | Race human turn/foreign activity with accepted follow-up and reconnect; daemon blocks unsafe dispatch. Busy, unsupported, denied, quota/full, oversized UTF-8 and stale generation reject correctly. Approval wait cannot be bypassed. CLI/MCP/web share these use-case outcomes. |
| U05 | Swap generation/reuse PID after stop confirmation, lose native ownership, target another team: no signal/write. Interrupt targets only matching turn; stop targets only proven agent. Shared terminal and coordinator survive; ambiguous stop becomes uncertain, never claimed stopped. |
| U06 | Fake clock/sequence: lost connection, stale snapshot, delayed old reply, generation reset and retention gap disable unsafe controls and force resync. Oversized output/prompt, HTML/script/ANSI/OSC/bidi fixtures remain bounded inert text; inspection cannot ack canonical deliveries. |
| U07 | Keyboard-only selection, labelled inputs, focus/confirmation/cancel flow, screen-reader status, 200% zoom and narrow viewport remain usable. Exercise read → follow-up → result → stop on published application without asserting exact UI sentences or pixel positions. |
| U08 | Publish .NET 11 JIT and Native AOT; run actual HTTP, source-generated JSON, IPC, SQLite owner and native adapter paths. Record warnings, artifact/dependency size, cold startup, idle/active RSS (daemon and web separately), bounded load/slow-client behavior and comparison with console disabled. No assumed AOT saving or automatic JIT fallback. |

Record code snapshot, SDK/backend/browser/provider versions, RID, launch context,
commands, results and limitations. Native Linux, Windows and macOS tests required
for each claimed backend/mode cell; Linux Chromium and cross-builds prove neither
Windows nor macOS execution. **Windows managed Pi launch, same-session follow-up,
results, interrupt/stop and reconnect in actual selected interactive and explicit
headless modes remain mandatory**. Missing access blocks that gate; mock rows and
fake tests never establish it. Browser support matrix needs native browser smoke
at least on each claimed OS; name tested versions rather than “all browsers”.

## 8. Checks for this planning delivery

Passed with available local Node.js (no packages installed): eight relative links,
UTF-8 replacement-character screening, final newlines/trailing whitespace, Markdown
fences, HTML tag nesting/unique IDs/label references, inline JavaScript syntax and
request/storage API screening. A synthetic DOM harness exercised selection,
follow-up acceptance/completion, interrupt, blocked foreign activity, all three
unsafe connection states, exact-ID stop confirmation and reset.

Limits: structural checks are not a standards HTML validator; the synthetic DOM
harness is not a browser. The parent subsequently rendered the local file in
isolated headless Chromium at 1440×1000 and inspected the screenshot; initial
layout rendered successfully. No browser interaction/accessibility or native
Windows/macOS run was performed. No runtime build/test/AOT/platform evidence or
independent review supplied. Review
remains pending before implementation; independent Codex design/security review
and user mockup feedback are separate from author checks. No repository format/lint suite was run for this documentation-only scope;
UI-1 proposes reproducible checks. No application server or new package/browser
installation was used; mockup assets are entirely local.
