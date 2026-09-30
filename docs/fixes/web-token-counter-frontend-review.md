# Frontend token counter review

Reviewed commit `2363e38b8e1b4ca73893e2da4757ca7cf7b0089b` against slice 3 and sections 2b, 2d and 3 of `web-token-counter-plan.md`, and `web-token-counter-frontend.md`.

## Findings

None. No concrete correctness or regression bugs found in the reviewed changes.

## Review evidence

- `usageSum` counts each non-null native `session_id` once, including follow-up jobs sharing their parent's session. Unknown usage does not reserve the session in the dedupe set, so a later known entry can contribute. Team sums add the lead's own usage once; the top total adds each lead-map entry once to globally deduped job sessions, as required by the API contract.
- Missing response fields and null usage do not throw or manufacture known zero totals. Explicit null job usage renders `—`; absent fields from an older backend omit the counter. Null lead entries render `lead —`; missing lead entries omit it, consistent with the report. Synthetic groups do not display lead usage.
- Counter text goes through `element`, which assigns `textContent`; tooltips use the DOM `title` property. No new `innerHTML` path exists.
- The exact `team-toggle` and `team-content` class values remain unchanged. The 5 s interval, request sequence guard, saved expansion/draft state, focus restoration and scroll restoration remain intact.
- Layout inspection found the existing wide-tree and <=680px rules already wrap `.team-toggle` and `.card-side`; the lead metadata also wraps. The added compact counter does not introduce a fixed width or prevent those containers from wrapping.

## Validation and limits

- `node --check src/AgentTeamForge.Host/Features/WebConsole/wwwroot/app.js` passed.
- Read-only Node checks of the actual helper source passed for shared-session deduplication, lead addition, missing/null usage, unknown and known token spans, and compact formatting.
- Layout and poll preservation were reviewed statically. No live browser, real-backend smoke test, or .NET suite was run for this review. The frontend report's claimed 390px browser check is limited by its documented Edge window clamping; it is not evidence of a true 390px viewport test.
- No production code or tests were edited, and no commit was made. Only this requested review artifact was written.

Verdict: APPROVE
