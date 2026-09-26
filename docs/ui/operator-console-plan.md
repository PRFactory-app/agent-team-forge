# Text-only operator console — plan and mockup

**Status:** revised proposal; production design **not approved**. The independent
[Codex review](operator-console-plan-review.md) withheld approval for B1/B2.
This revision specifies both corrections and the focused refinements; independent
re-review is still required before production implementation. User mockup feedback
remains a separate required gate. No runtime or mockup changes in this revision.
Original user-selected planning lane: Pi / GPT-6 Astra / medium; HTML/CSS/minimal
JavaScript permission applied only to the static mockup.

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
consistent machine-readable errors. Authenticated IPC identifies the web process
but does not grant it blanket operator authority. The daemon issues a delegated
grant for each paired browser: stable operator principal, allowed teams/actions,
web-instance ID, exact browser origin, daemon generation and expiry. Only the
OS-authorized launcher may request issuance under existing daemon identity policy;
the grant cannot exceed that launch principal's permissions. Every IPC read and
mutation carries this delegation; the daemon validates binding, expiry, revocation
and current team/action permission, including receipt lookup and output reads.
Browser-supplied roles, team IDs, agent IDs or `operator: true` confer no authority.
The grant remains server-side; the browser gets only the scoped memory-only bearer
described below. Failure to establish delegation fails pairing closed.

## 4. Operator flows and API semantics

### Inspect

List authorized agents: stable ID, backend, team, OS, interactive/headless mode,
state and age. Detail shows session/run generation, ownership/capability evidence,
current job, bounded plain-text log and last authoritative result. Show unavailable
result separately from empty result. Snapshot includes daemon generation, sequence,
observation timestamp and capability reasons. “Connected” means transport health,
not fresh agent evidence; present observation age separately. Control eligibility
requires observation age below a shared maximum trusted age, frozen with P01/P04
before UI-1; no configured bound means controls stay disabled. Age includes elapsed
monotonic time since receipt plus daemon-reported evidence age, not merely HTTP
round-trip freshness. Apply only increasing sequence/revision within the established
daemon generation; a generation change invalidates controls and requires explicit
resync. Reject delayed replies from prior generations or older sequences/revisions;
they cannot reset age or re-enable controls. The daemon independently rechecks
expected revision, freshness, permissions and ownership at action time. Never infer
completion from log text, a live PID, a model statement or a successful HTTP response.

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
same key + same payload returns the original durable receipt/job, without a second
acceptance or dispatch. Before sending, retain ONLY non-secret intent metadata in
`sessionStorage`: original key, canonical request fingerprint, target identity and
generation (including run when applicable), and operation kind. Never retain prompt,
raw output, credentials or grants there or in other browser storage. The fingerprint
is a correlation digest, not authority; its canonical definition is shared with the
daemon. If this minimal record cannot be retained, block submission rather than
promise reload-safe recovery.

Reload loses the bearer and requires fresh OS-authorized pairing. Web-process loss
also requires fresh pairing to its new instance. Using the same stable principal,
query the original durable daemon receipt by key, with matching fingerprint, target
generation and operation; authorize the lookup against current team/action grants.
Do not use heuristic recent jobs or identical prompt text to identify acceptance.
Receipts survive web/daemon restarts independently of revoked session grants.
If metadata or the authoritative receipt is unavailable, expired, denied, or cannot
establish the outcome, show unresolved acceptance. Never automatically resend or
mint a new key as a purportedly safe recovery. Any deliberate new intent is separate
work, explicitly acknowledged as possibly duplicating the unresolved operation,
not a recovery shortcut. Browser/web process loss never cancels a committed job.
Busy, foreign/human-active,
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

Typing the ID confirms human intent only, not authorization or ownership. The
production UI excludes the demo-only simulated completion action.

Interrupt and stop both use durable idempotent operation receipts. Before any native
effect, the daemon atomically commits the key, canonical fingerprint, authenticated
stable principal, operation, target agent/session/run generation and unattempted
state. Commit an attempt marker before native dispatch. Equal-key/equal-payload
concurrent retries return the same receipt/outcome, not another effect; changed
payload conflicts. Reuse of a key from another principal cannot disclose or reuse
a receipt. Apply the same metadata-only retention, fresh pairing and original-key
receipt lookup as follow-up after reload, lost response or web/daemon restart.
Receipt retention must cover the supported recovery window; missing or ambiguous
receipts mean unresolved, never permission to repeat an uncertain native effect.
After a crash past the attempt marker, reconcile rather than blindly redispatch.
Receipt lookup never retargets an operation to a replacement generation.

Daemon reauthorizes and verifies native ownership immediately before action; a
changed selection/state invalidates confirmation. Unknown ownership, stale binding,
PID reuse, unsupported native control or uncertain identity fails closed. Only a
separately verified owned process boundary may be torn down; never kill a shared
terminal host. Do not silently escalate interrupt into kill. Show stop requested,
confirmed stopped, denied, unsupported or outcome uncertain; a sent signal is not
proof of exit. Losing connection while stopping leaves outcome uncertain until read.

## 5. Local web security and bounds

Required before any live mutation endpoint, including a localhost preview:

- Opt-in numeric loopback listener only, no wildcard/LAN or remote option. Each
  instance selects one canonical origin (scheme, numeric host, explicit port;
  IPv6 only if explicitly configured). Every request, including static assets,
  must match its exact Host/port. Reject aliases, duplicate/foreign Host,
  absolute-form request targets and proxy/forwarded-host tricks; never trust proxy
  headers or resolve a supplied name to decide whether it is loopback.
- Choose **memory-only bearer custom-header authentication, no auth cookie**.
  No endpoint sets or accepts a cookie as authority. Browser API calls use
  `credentials: 'omit'`; redirects are rejected, never followed with credentials.
  A cryptographically random opaque bearer is sent only to the exact configured
  origin in `X-ATF-Session`, never in URLs, argv, referrers, logs or browser storage.
  No broad daemon credential is exposed. Page reload destroys the bearer.
- Only a launcher authenticated as the local OS principal through private IPC may
  request a short-lived single-use pairing code under daemon policy. Bind that
  code to the launch principal, allowed teams/actions, web-instance ID, exact
  origin and daemon generation. Expose it only through the authorized launcher's
  local presentation for manual entry, not an unauthenticated HTTP endpoint.
  Same-origin pairing POST consumes it atomically once, establishes the daemon
  delegation and returns the bearer in a no-store response. Invalid, expired,
  replayed or mismatched codes fail generically under bounded rate limits. Visiting
  the page alone never grants authority; failed delegation yields no session.
- Require exact non-null Origin on **every API request**, including pairing,
  logout, receipt lookup and inspection. Read fetches use same-origin POST with
  bounded JSON so browsers supply Origin; these operations remain read-only. No
  protected GET or fallback accepting absent Origin. Static page/asset GET may
  omit Origin, serves no protected data and never mutates; reject a supplied
  foreign or null Origin. All API calls require supported JSON content type and,
  except pairing with its one-time proof, the bearer custom header. Reject foreign,
  absent or null Origin, cross-site form/fetch and CORS preflights; grant no CORS
  access. Custom headers and exact origin checks replace cookie/CSRF-token state.
- Bind bearer lookup to this web instance/origin and its delegated grant. Enforce
  short idle and absolute expiry; logout, operator revocation or grant expiry
  invalidates bearer and delegation together. Validate delegation on every daemon
  IPC read/mutation, not just pairing; reauthorize again before native effects.
  Web restart discards bearer mappings and invalidates its instance grants; daemon
  restart changes generation and invalidates all old grants. Fail closed while
  revocation/restart status is unknown. Fresh pairing can recover durable receipts
  for the same principal, but cannot revive an old grant. Session expiry/revocation
  alone does not cancel already accepted work; normal daemon execution policy still
  applies. Same-user malicious software remains outside the sandbox guarantee.
- Production page uses restrictive CSP (self assets, no inline executable code),
  `frame-ancestors 'none'`, no-referrer, nosniff and no-store for sensitive responses.
  The local-file demo's inline script is not a production CSP template.
- Treat prompts, logs and results as untrusted text: text nodes only, no Markdown
  HTML, executable links, terminal escape interpretation or ANSI/OSC hyperlink
  handling. Strip/visibly escape control sequences and bidi controls at presentation;
  apply best-effort known-secret filtering without promising perfect redaction of
  arbitrary backend output. Output is sensitive: authorize every bounded read and
  paginate server-side. No prompt bodies, raw output or credentials in URLs, access
  logs, browser storage or third-party requests. Do not log auth headers.
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
| U02 | Before live mutations: exercise read POST and all mutation/pairing paths with absent/null/foreign Origin, wrong Host/port, aliases, absolute-form targets, rebinding, forwarded headers, cross-site forms/fetch and preflights. No protected output/effect. Verify no auth cookie is issued, another loopback port receives no ambient credential, and cookie-only requests fail. Missing/forged bearer, expired/replayed/wrong-instance pairing, forged principal/team/action grant, revoked/expired delegation and old bearer/grant after web or daemon restart fail closed. Every IPC read/mutation validates delegation independently of HTTP checks; valid scoped access succeeds, unauthorized teams/actions fail. Assert no credentials in URLs/storage/logs and no redirect forwarding. |
| U03 | Drop HTTP/IPC response after daemon acceptance commit, then reload or kill web process before response: retain only key/fingerprint/target generation/operation in sessionStorage, freshly pair and query original principal-bound receipt, even across daemon restart. Concurrent equal-key/equal-payload retry yields one accepted job/intent and no duplicate dispatch; changed payload conflicts. Identical prompts from concurrent clients cannot confuse lookup. Missing metadata, unavailable/expired/denied receipt or storage failure cannot trigger automatic/new-key safe resend. Verify no stored prompt/credential and no heuristic recent-job recovery. Crash before/after attempt marker preserves queued versus uncertain distinction; no blind prompt replay. |
| U04 | Race human turn/foreign activity with accepted follow-up and reconnect; daemon blocks unsafe dispatch. Busy, unsupported, denied, quota/full, oversized UTF-8 and stale generation reject correctly. Approval wait cannot be bypassed. CLI/MCP/web share these use-case outcomes. |
| U05 | Stop/interrupt receipts and attempt markers commit before effects. Concurrent equal-key/equal-payload retries return original receipt; changed payload conflicts and cross-principal lookup is denied. Reload/web-process death after commit before response requires fresh pairing and original-key lookup. Lose response, change target generation/reuse PID, restart daemon or lose ownership: no repeated ambiguous effect or signal to replacement/neighbor. Interrupt targets matching run only; stop targets proven agent only. Typed confirmation grants no authority; another team fails daemon authorization. Shared terminal/coordinator survive. Missing receipt remains unresolved; signal/HTTP success never proves exit. |
| U06 | Fake clock/sequence: maximum trusted observation age despite healthy HTTP, lost connection, stale snapshot, delayed older sequence/revision or prior-generation reply, generation reset and retention gap disable unsafe controls and force resync; older replies never refresh age or re-enable controls. Oversized output/prompt, HTML/script/ANSI/OSC/bidi fixtures remain bounded inert text; inspection cannot ack canonical deliveries. |
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

## 8. Review status and evidence boundaries

The unchanged [independent review](operator-console-plan-review.md), imported from
review commit `a113bc53`, evaluated snapshot `0dbf44b`, not this revision. B1 is
addressed here by the no-cookie bearer, OS-authorized pairing and per-IPC delegated
authority contract; B2 by retained non-secret metadata and durable original-key
receipts for follow-up, interrupt and stop. Observation age/monotonic snapshots,
authorized bounded untrusted output and confirmation-not-authorization refinements
are incorporated. These are proposed contract corrections, **not reviewer closure**.
Independent re-review remains pending; production design is not approved and user
mockup feedback is still required. U02/U03/U05 describe future evidence, not passed
tests. No runtime or mockup code changed.

### Historical mockup checks (not rerun for this revision)

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
Windows/macOS run was performed. No runtime build/test/AOT/platform evidence was
supplied. The subsequent independent Codex review withheld production approval;
re-review and user mockup feedback remain separate from author checks. No repository
format/lint suite was run or claimed to exist for this documentation-only scope;
UI-1 proposes reproducible checks. No application server or new package/browser
installation was used; mockup assets are entirely local.

### Bounded checks for this revision

Checked both Markdown documents for relative file-link existence, final newlines,
trailing whitespace, replacement characters and balanced fences; verified the
review import is byte-for-byte unchanged and ran `git diff --check`. These checks
do not validate external links, browser behavior or security/runtime contracts.
No repository lint, runtime build, platform tests or mockup checks were run.
