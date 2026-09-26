# Operator-console plan: focused independent rereview

**Reviewed snapshot:** `32fc8ccb6f7f18c845b44e2d1fd7efa09a944332` (`docs/ui/operator-console-plan.md`). The [original review](operator-console-plan-review.md) assessed `0dbf44b`; this rereview assesses its B1/B2 corrections, not the mockup's visual design.

**Verdict:** B1 and B2 are resolved **as proposed design contracts**. No new blocking design finding. This closes the original review's two plan corrections; it does not establish working authentication, native control, platform support, or production readiness. The [contributor gates](../../AGENTS.md), [architecture](../architecture.md), [cross-phase contracts](../planning/full-product/contracts.md), prerequisite P02/P03/P04 capabilities, and user mockup feedback still govern any web runtime work.

## B1 — delegated authority and browser isolation

The revision removes the port-shared auth cookie. A memory-only bearer travels in a custom header to one exact numeric loopback origin; API requests require exact non-null Origin, and unprotected static GETs carry no authority. Pairing starts with a short-lived, single-use code issued only to an OS-authenticated launcher. Its daemon grant is limited by the launch principal, team/actions, web instance, origin, daemon generation and expiry. The daemon validates the delegation and current permission on **each** IPC read or mutation, including output and receipt reads; the web process's IPC identity alone grants no operator power. This gives an implementable answer to B1 without a separate auth service or database.

Logout/revocation/expiry invalidate the bearer and grant; a web restart loses bearer mappings and instance grants, and a daemon restart invalidates prior-generation grants. Reload requires fresh pairing. Unknown revocation or restart state fails closed. U02 proposes the needed cross-port, forged/revoked grant, Origin/Host, replay and restart negatives. These are specified tests, not results.

## B2 — original-intent recovery and effects

Before submission, the page retains the original key and non-secret correlation metadata in `sessionStorage`, then can pair again as the same stable principal and query a **durable, principal-bound daemon receipt** by original key, operation and target generation. Follow-up acceptance is atomic with its key/fingerprint and queued intent. Interrupt and stop likewise commit a target/session/run-bound receipt before their native effect and an attempt marker before dispatch. Equal-key retries return that receipt; changed payload conflicts. A committed attempt with uncertain effect is reconciled, never blindly repeated or retargeted to a replacement generation. Current permission and native ownership are rechecked before effects. These contracts address lost HTTP/IPC responses, reload, concurrent retries and web/daemon restart without asserting exactly-once external effects.

Recovery is conditional: `sessionStorage` is tab- and origin-scoped, so a changed port, lost tab/record, denied grant or expired/missing receipt can leave acceptance unresolved. The plan correctly forbids automatic resend or a new key presented as safe recovery. Any deliberate new intent must disclose possible duplication. UI-0 should freeze receipt/key retention and the canonical fingerprint representation; stored metadata must not expose guessable prompt or credential content. This is a contract detail, not a reason to add a browser-side durable queue or another scheduler.

## Evidence and boundary

U02/U03/U05 are future adversarial tests. This rereview inspected documents only: no runtime, browser interaction, published binary, AOT or native-platform test was run. The [static mockup](operator-console-mockup.html) remains illustrative, and user feedback on it is outstanding. This design verdict does **not** approve backend capability or authorize web runtime implementation ahead of prerequisite contract and review gates.
