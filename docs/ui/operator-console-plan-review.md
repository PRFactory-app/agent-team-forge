# Independent operator-console plan and security review

**Reviewed snapshot:** `0dbf44b` (`docs/ui/operator-console-plan.md` and `docs/ui/operator-console-mockup.html`).
**Reviewer:** Codex GPT-6 Sol, high.
**Verdict:** **Production design approval withheld pending two blocking contract corrections.** The separate `Host: web` role is the right bounded architecture choice. This review does not approve runtime implementation, platform support, accessibility, or the mockup's visual design; the user evaluates the mockup separately.

## What is sound

- The proposed browser → loopback `Host: web` → authenticated IPC → daemon path preserves the three-project boundary in [architecture](../architecture.md). The web client has no job database, scheduler, or agent process ownership. Browser and web-process loss do not cancel committed daemon work.
- The plan reuses P02/P04 admission, authorization, idempotency and projections. It distinguishes accepted work from completion, turn interrupt from whole-agent stop, and native ownership evidence from a PID or HTTP success. It blocks unsafe activity and keeps canonical delivery acknowledgments separate from filtered inspection.
- Numeric loopback binding, exact Host and Origin checks, CSRF protection, restrictive CSP, plain-text rendering, limits, stale-state handling, and the U01–U08 negative tests are appropriate for this small local surface. The proposed sizes and intervals remain proposals until the shared policy is frozen.

## Blocking corrections before production implementation

### B1 — Specify a browser-to-daemon authority and pairing contract

The plan says the web principal is bound through IPC and that pairing creates a cookie session, but does not fix how one paired browser session maps to one daemon-recognized operator principal and team/action grant. A generic `Host: web` operator credential would let the HTTP layer become the sole authority for every paired browser. The daemon must receive and validate the effective delegated principal/grant on **every** read, follow-up, interrupt and stop, with revocation and expiry; browser-supplied role, team or agent IDs are never authority. Freeze who may issue the pairing code, the launch identity and scope it represents, the web-instance binding, and what happens on web/daemon restart. Pairing must fail when that delegation cannot be established.

There is also a concrete loopback cookie issue: cookies are scoped to host, not TCP port. Another local service on the same numeric loopback host may receive the cookie. The stated same-user malware limitation does not remove cross-port leakage to an unrelated local process. Do not make a cookie alone sufficient for authenticated reads or mutations. A minimal design is an opaque cookie **plus** a separate, unguessable, page-memory proof required in a custom header on every API request; issue both only after the OS-authorized, one-time pairing exchange. On page reload, require a fresh pairing or a comparably secure rebind; never return the proof to a request authenticated only by the cookie. Scope both credentials to the web instance, exact origin and daemon delegation, and revoke together. An in-memory bearer-header session without a cookie is another small viable choice, if the plan deliberately replaces the cookie proposal. Keep exact numeric Host/port and non-null Origin checks for pairing/mutations, reject DNS-rebound or absolute-form host aliases, and do not trust proxy headers.

**Required evidence:** U02 must exercise another loopback port receiving the cookie, replay of that cookie without the page proof, forged grants, revoked grants, wrong Host/Origin, null Origin and rebinding. Assert that no protected output or daemon effect occurs. These checks belong to the first authenticated inspection slice, before any live mutation endpoint.

### B2 — Make lost-response recovery work across reload and process loss

The plan correctly requires a per-intent key and daemon atomic acceptance, but keeps the pending key only in page memory. After reload, “recent-job lookup” alone may not identify whether a specific lost request committed, especially if there are multiple identical prompts or concurrent clients. Generating a new key can duplicate work. Freeze a recovery path that retains or authoritatively retrieves the original key and fingerprint across reload. A non-secret pending-intent record in browser session storage is a small option; a daemon-side lookup bound to the paired principal, target and intent is another. The record is not an authentication credential. If neither original key nor an authoritative matching operation can be recovered, the UI must show unresolved acceptance and prohibit automatic retry or a purportedly safe resend. Any explicit new intent must be distinguished from recovery of the old one.

Apply the same rule to lost interrupt/stop responses: retain or look up their operation keys and outcomes, and never infer successful exit from an HTTP response or signal. The daemon must atomically retain the stop request/receipt before the native effect, bind it to target agent/session/run generation and authenticated principal, and return the same outcome for an equal-key retry. Native control still rechecks ownership immediately before effect; an ambiguous effect remains uncertain rather than repeated against a possibly changed process.

**Required evidence:** extend U03/U05 with reload and web-process death after daemon commit but before HTTP response, equal-payload concurrent retry, same-key changed-payload conflict, and lost stop response after a target generation change. Show one accepted follow-up and no signal to a replacement or neighboring process.

## Focused clarifications for the contract freeze

1. Define a maximum trusted observation age for enabling controls, separately from HTTP connection health, plus monotonically applied daemon generation/sequence/revision checks. A delayed older response must not re-enable actions. The server rechecks expected revision and all authorization/ownership rules regardless of disabled browser buttons. Keep the two-second refresh and byte limits provisional until P01/P04 policy freezes them.
2. Serve output only to authorized readers, bound and paginated at the server. Render prompts/logs/results with text nodes, normalize or visibly escape ANSI/OSC and bidi controls, and avoid promising complete secret redaction from arbitrary backend output. Do not put prompt bodies, credentials or raw output in URLs, access logs, browser storage or third-party requests. The production CSP needs external same-origin script/style assets; the mockup's inline script is for the local-file demo only.
3. Preserve the stop confirmation's exact ID, team, backend, mode and generation, but treat typing the ID as a human-intent check rather than authorization or proof of ownership. Keep the demo-only “Simulate job completion” control out of production.

## Evidence boundary and decision

The HTML file clearly labels its three rows and lifecycle states as synthetic. Its DOM uses text nodes for dynamic content, and its controls illustrate stale/disconnected blocking and typed stop confirmation. The author's structural checks and synthetic harness, plus the parent's initial Chromium screenshot, establish only a renderable planning artifact. They are not browser interaction, accessibility, HTTP security, published-binary, native adapter, Windows, macOS, Pi or AOT evidence.

**Decision:** approve the narrow architecture direction for continued contract work. Do not start production web endpoints until B1 and B2 are incorporated into the plan and independently re-reviewed. After that, implement through the existing Host/Business/DAL use cases with Claude-written code, opposite-family Codex review and applicable published-binary/platform gates from [AGENTS.md](../../AGENTS.md). No UI visual-owner approval is inferred here.
