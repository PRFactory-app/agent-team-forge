# Web console session layout — frontend implementation

Branch `feat/web-session-layout`. Files: `wwwroot/index.html`, `app.js`, `app.css` only (no backend `.cs` touched).

## Implemented
- **Slice 1 – collapse controls:** `Collapse all` / `Expand all` toolbar buttons (`setAllTeams`, writes `savedTeams` once, then `loadJobs()`). `saveTeams()` keeps at most 200 keys (oldest dropped; a rewritten key moves to the end). Finished jobs (`light==='grey'` and not queued/running/parked/needs_reconciliation) fold into `<details class="settled-jobs">` ("N finished"), state stored as `fold:<lead>` in `savedTeams`, default closed, forced open when the selected job is inside. `team-toggle` / `team-content` classes unchanged.
- **Slice 2 – master-detail:** `.overview-layout` wrapper + `#detail-pane` in `index.html`; `@media (min-width:1100px)` block in `app.css` (matches `wide = matchMedia(...)` in `app.js`, both commented). `cardPanel` sets `panel.ownerCard`; `placeDetail()` moves the selected panel into the pane (header + Close) or back to its card; called from `toggleCard`, end of `loadJobs` and the breakpoint `change` event. Wide: clicking the selected card keeps it selected, Close/Escape deselect (`toggleCard(key, true)`); `aria-current` replaces `aria-expanded`, `aria-controls="detail-pane"` (`syncSelectionAria`). Selection is cleared only if no panel with that key exists (a collapsed team does not clear it). Narrow path unchanged (inline accordion).
- **Slice 3 – scroll:** `captureInnerScroll`/`restoreInnerScroll` around the rebuild in `loadJobs` (pane + `.activity-entries`, `.card-result pre`, `.card-logs pre`; a scroller at the bottom follows the tail). `keepScroll()` also wraps `renderActivity`, result text and raw-log updates. Scrollers without overflow are not treated as "at bottom".
- **Slice 5 – lead inbox target:** for a real lead with a workspace the composer gets a first/default option `Lead session (inbox)` (`@lead`); members sit in an optgroup "Message member". Works with zero member jobs. Interrupt/Stop disabled, placeholder "Message the lead (lands in read_messages)…". `sendInline` posts `POST /api/leads/{id}/messages {text, workspace, idempotency_key}`; success shows "Delivered to lead inbox" (no `deliveryJobId`); retry/discard flow unchanged (the lead id/workspace are stored in the pending attempt). Panel details fall back to `targets[0]`.
- I did not touch the lines that the `lead_name` branch edits (team-name, `span.session-id`, new-agent lead dropdown).

## Verification
- `node --check app.js`: OK.
- **Solution build not possible here:** `global.json` needs .NET SDK 11.0.100-rc.1; only 10.0.x is installed. WebConsole tests (Linux+chromium) not run. The static files changed only by additions; `team-toggle`/`team-content` literals are untouched.
- Real browser (headless Edge via CDP, 1440/900/390 px) against a small mock API (8 leads, 32 jobs) serving `wwwroot`: wide pane shown, no h-scroll; selecting a job/lead fills the pane and leaves no inline panel; re-click keeps selection, Escape clears it; activity scroll (100px) survived a poll, bottom-parked list stayed at bottom; composer draft survived polls; Collapse all keeps the pane populated, Expand all works, state persisted in localStorage; lead picker defaults to `@lead` with Interrupt/Stop disabled and the POST returned "Delivered to lead inbox"; wide→900px moves the panel inline (draft kept, accordion works, pane hidden); back to wide restores the pane; 390px has no horizontal scroll.

## Known gaps
- Finished-jobs fold "forced open when selected" and its persistence were not exercised in the browser (the selected test jobs were running); logic is straightforward.
- Backend `POST /api/leads/{id}/messages` (slice 4) not available here; the mock accepted the POST only. Real `invalid_session` shows as `Failed: invalid_session`.
- Light theme and the real daemon were not checked.
