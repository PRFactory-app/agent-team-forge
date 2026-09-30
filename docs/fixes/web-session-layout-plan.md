# Web console: collapsible session tree + master-detail layout — plan

Branch `feat/web-session-layout`. Slices 1–3 (tree and layout) are frontend only (`src/AgentTeamForge.Host/Features/WebConsole/wwwroot/`).
Messaging the lead (§2c, slices 4–5) needs one small **backend** addition. No new frameworks or build steps.

## 1. Current structure

Paths below are relative to `wwwroot/`.

- **Page shell**: `index.html:35-68`. `#overview-view` holds the toolbar (`:37-45`), the new-agent form, `#jobs` (`:66`) and the pager (`:67`).
  The `body` width is capped at `min(1160px, …)` (`app.css:53`). The only breakpoint is `@media (max-width: 680px)` (`app.css:239-267`).
- **Tree**: everything is rebuilt by `loadJobs()` (`app.js:887-1087`), which runs on a 5 s `setInterval` (`app.js:881`) and after actions.
  It calls `overview.replaceChildren()` (`:908`) and rebuilds the tree from scratch:
  - `section.lead-group` has a `button.team-toggle` and a `div.team-content` (`:949-973`).
  - `div.lead-card` has a `button.lead-toggle` and a `div.card-expanded` panel (`:975-1003`).
  - `div.agent-tree` holds one `article.agent-node` per job, each with a `button.card-main`, `.card-side` and `.card-expanded` (`:1004-1062`). External members are added as plain `.agent-node.external-node` (`:1063-1068`).
- **Team collapse (already exists)**: `savedTeams` is stored in `localStorage['atf.web.teams']` (`app.js:11-16`). `teamOpen()` defaults a team to open when it needs attention or is running/waiting (`:856-858`). `toggleTeam()` flips `hidden` in place and persists the choice (`:860-869`).
  **Missing**: collapse-all/expand-all, and any folding inside a team. A team with many completed jobs stays long.
- **Selection and details**: there is a single `expandedKey` (`:29`), either `lead:<id>` or `job:<id>`. Every card always renders its `cardPanel()` (`:815-837`), holding the composer, join ticket, result, activity and raw logs. The panel is `hidden` unless it is selected.
  - `toggleCard()` (`:839-854`) flips `hidden`, sets `.selected` and `aria-expanded` on `panel.parentElement`, and calls `panel.openCard()` to lazy-load the details.
  - Escape closes the selection (`:1189-1191`).
- **State that already survives a poll**: `expandedKey`, team open state, composer drafts (`composers`), log state (`cardLogs`), activity entries (`activities`), window scroll (`:900,1084`) and focus/caret (`:901-907,1075-1083`).
  - `:1073-1074` clears `expandedKey` when the selected panel is missing or sits inside a collapsed team.
- **State that is lost on each poll or render (existing bug)**:
  - The inner scroll position of `.activity-entries` (`max-height:340px`, `app.css:214`) and of the result/log `pre`. The panel is rebuilt on every poll.
  - `renderActivity()` (`app.js:698-715`) also resets the list scroll: its `list.replaceChildren()` clamps `scrollTop` to 0.
- **Test constraint**: `tests/.../WebConsole/WebConsoleScenarios.cs:120-121` (Linux + chromium) asserts the literal strings `class="team-toggle"` and `class="team-content"`. Do not add extra classes to those two elements; use data attributes or a wrapper instead.

## 2. Proposed changes

### 2a. Collapsible tree (request 1)

- **Toolbar** (`index.html:37`): add `<button id="collapse-all">Collapse all</button>` and `<button id="expand-all">Expand all</button>`.
  - The handler loops over the rendered `[data-toggle-key^="team:"]` buttons, sets `savedTeams[key] = false/true`, persists once, then calls `loadJobs()`.
  - Do not re-implement DOM toggling here; the re-render already honours `savedTeams`.
- **Fold finished jobs inside a team** (can be cut if time is short):
  - Split `groupJobs` into active jobs (`queued`, `running`, `parked`, `needs_reconciliation`, or `light !== 'grey'`) and settled jobs (`light === 'grey'`).
  - Render the active jobs as today. Put the settled jobs after them inside `<details class="settled-jobs" data-fold-key="fold:<lead>"><summary>N finished</summary>…</details>`.
  - Store the open state in the same `savedTeams` map under key `fold:<lead>`, defaulting to closed. Save it on the `toggle` event.
  - If the selected job is in the fold, force `open` so the selection is not hidden in narrow mode.
- **Limit stored state**: when writing `savedTeams`, keep at most ~200 keys and drop the oldest insertion-order keys. Without a limit the map grows forever.
- **Tighten the team header**: make the collapsed `.team-toggle` a single compact line. It mostly is already; only check padding and wrapping at the new column width.

### 2b. Master-detail on wide viewports (request 2)

- **Breakpoint**: use `(min-width: 1100px)`. Below it, keep today's inline accordion unchanged, including the 680 px mobile rules.
  - Define it once in JS as `const wide = matchMedia('(min-width: 1100px)')` and repeat it in the CSS `@media`.
  - Add a comment in both places saying they must match.
- **DOM** (`index.html:66`): wrap the tree and add a detail pane.
  ```html
  <div class="overview-layout">
    <div id="jobs"></div>
    <aside id="detail-pane" aria-label="Selected session details">
      <p class="note detail-empty">Select a lead session or agent to see details.</p>
    </aside>
  </div>
  ```
- **CSS (`app.css`)**:
  - Default rule: `#detail-pane { display: none; }`.
  - Inside `@media (min-width: 1100px)`:
    - `body { width: min(1680px, calc(100% - 48px)); }`
    - `.overview-layout { display: grid; grid-template-columns: minmax(340px, 440px) minmax(0, 1fr); gap: 20px; align-items: start; }`
    - `#detail-pane { display: block; position: sticky; top: 16px; max-height: calc(100vh - 32px); overflow: auto; padding: 16px; border: 1px solid var(--border); border-radius: var(--radius-md); background: var(--raised); }`
    - `#detail-pane .card-expanded { margin: 0; padding: 0; border: 0; background: transparent; }`
    - Raise `#detail-pane .activity-entries, #detail-pane pre` to a taller `max-height`, e.g. `50vh`.
    - Make the narrow tree column compact by reusing the ≤680 px rules scoped to `.overview-layout`: `.agent-node { grid-template-columns: minmax(0,1fr) }`, a flex `.card-side`, and `.lead-toggle` in 2 columns.
    - Hide the ▸/▾ chevrons for cards (`.card-identity::after`, `.lead-count::after`), because selection no longer expands anything.
- **JS (`app.js`)**. Keep `cardPanel()` building the panel inside its card, as it does today, so the narrow path is untouched. In wide mode, move the selected panel node into the pane.
  - In `cardPanel()`, set `panel.ownerCard = card`. In `toggleCard()`, replace `panel.parentElement` (`:845-846`) with `panel.ownerCard`, so `.selected` and `aria-*` still work after the panel moves.
  - Add `placeDetail()`, and call it at the end of `toggleCard()`, at the end of `loadJobs()` (before `openCard`), and from `wide.addEventListener('change', placeDetail)`. It does this:
    1. If the pane holds a panel and its `ownerCard.isConnected`, move it back with `ownerCard.append(panel)` and set `panel.hidden = panel.dataset.expandKey !== expandedKey`. Otherwise drop it; it is a stale node from the previous render.
    2. In narrow mode, or with no selection, show only the empty hint in the pane and return.
    3. Otherwise, find `#jobs .card-expanded[data-expand-key="<CSS.escape(expandedKey)>"]` and run `pane.replaceChildren(header, panel)` with `panel.hidden = false`. The `header` shows `toggleLabel` plus a "Close" button that calls `toggleCard(expandedKey)`.
  - **Click behaviour in wide mode**: clicking a card selects it. Clicking the selected card again leaves it selected, as in Claude Desktop. Close and Escape still deselect. In narrow mode the toggle stays as it is today.
  - **ARIA**: in wide mode set `aria-current="true"` on the selected toggle and remove `aria-expanded`. Point `aria-controls` at `detail-pane`.
  - **Change the "clear selection" rule** at `:1073-1074`:
    - Wide mode: clear `expandedKey` only if no panel with that key was rendered, for example after a page or filter change. A collapsed team must not clear the pane.
    - Narrow mode: keep today's rule.
    - `openPanel` is then the panel found by key, and is the same element `placeDetail()` moves.
- **Keep scroll across polls and renders**. This fixes both modes.
  - In `loadJobs()`, before `replaceChildren` (`:908`), capture:
    - `sel = expandedKey`
    - `paneTop = pane.scrollTop`
    - for each scroller in the open panel (`.activity-entries`, `.card-result pre`, `.card-logs pre`), keyed by class: `{top, atBottom: top + clientHeight >= scrollHeight - 4}`
  - After `placeDetail()`, restore them if `expandedKey === sel`. A scroller that was at the bottom snaps to the new bottom, so the tail follows.
  - Keep `window.scrollTo(0, scroll)`.
  - Apply the same save and restore in `renderActivity()` around `list.replaceChildren()`, so a background `loadActivity()` page does not jump the list.
- **Focus restore** (`:1075-1083`) already queries `document`, so composer focus and caret keep working inside the pane. Nothing to change.

### 2c. Message the lead session (additional request)

**How "Message member" works today.** It never touches an inbox; it is a job follow-up.
- The lead panel's composer (`app.js:401-495`, created from `cardPanel(leadCard, …, lead=true)` at `:1000`) offers a picker of the member **jobs** in the group that have a `session_id` (`leadTargets`, `:998`).
- Send posts `POST /api/jobs/{id}/follow-up` (`app.js:508`). `WebConsoleServer.cs:215,280-300` maps that to `IpcProtocol.JobFollowUp`, which `JobsEndpoint.cs:~240` passes to `followUp.Execute(...)`. The result is a new turn for that agent.
- External (joined) members are only displayed (`app.js:1063-1068`). They cannot be messaged from the web.

**How a lead's inbox works** (what `read_messages` reads and what wakes the lead):
- A lead-inbox message is a row in `external_messages` with `recipient='lead'`, `team_id = lead session id`, and `wake_key` copied from `external_teams`. See `ExternalMemberStore.SendFromMember` (`ExternalMemberStore.cs:303-331`), which members use through `external_send` (`JobsEndpoint.cs:89`).
- The team row for an MCP lead is created or checked by `EnsureMcpTeam(sessionId, workspace)` (`ExternalMemberStore.cs:41-66`). It requires an open `lead_sessions` row with a matching workspace.
- `read_messages` goes to `IpcProtocol.ExternalLeadRead`, then `ExternalTeam.ReadLead` (`ExternalTeam.cs:162-183`), then `ReadLeadCompat`. That reads **every** sender to `'lead'`, so a new sender needs no change on the read side.
- **Wake:** `WakeCoordinator` polls every second (`WakeCoordinator.cs:47`). `WakeStore.cs:150` finds unread `external_messages` whose `wake_key` points at an active target and sends the notice-only wake ("N external message(s) await reading…", `WakeCoordinator.cs:95`). So a committed row with the team's `wake_key` wakes the lead natively. No wake code is needed.
- The web console can already call a lead-scoped external op: the join ticket (`POST /api/leads/{id}/join-ticket`, `WebConsoleServer.cs:218,361-378`, then `ExternalTicket`, then `ExternalTeam.CreateTicket`, which also uses `EnsureMcpTeam`).

**Smallest change: a new "operator to lead" send that inserts one lead-inbox row.**

*Backend (Codex), about 5 files:*
- `src/AgentTeamForge.DAL/Features/External/ExternalMemberStore.cs`: add `SendToLead(string teamId, string sender, string text, DateTimeOffset now, string? commandId)`.
  - Use the same transaction and `INSERT … recipient='lead' … wake_key FROM external_teams WHERE team_id=$team AND closed_at IS NULL` as `SendFromMember`.
  - Deduplicate with `external_delivery_keys`, exactly as `SendToMember` does (`:339-347`), so a browser retry with the same key does not insert twice.
  - DRY: extract the shared insert into a private helper that `SendFromMember` also calls.
- `src/AgentTeamForge.Business/Features/External/ExternalTeam.cs`: add `SendFromOperator(string? sessionId, string? workspace, string? text, string? commandId)`.
  - Validate: text is 1–65536 characters, and `commandId` is 1–128 characters when given.
  - Call `EnsureMcpTeam`; if it fails, return `invalid_session`. Otherwise call `members.SendToLead(sessionId, "operator", text, now(), commandId)`.
  - Reserve the sender name: make `CreateTicketForTeam` reject a member named `operator`, so a member cannot impersonate the operator in `read_messages`.
- `src/AgentTeamForge.Host/Transport/IpcMessages.cs`: add `IpcProtocol.ExternalOperatorSend = "external_operator_send"`. It reuses the existing `LeadSessionId`, `Workspace`, `Text` and `IdempotencyKey` request fields.
- `src/AgentTeamForge.Host/Features/Jobs/JobsEndpoint.cs`: next to `ExternalLeadSend` (`:217`), add `case IpcProtocol.ExternalOperatorSend: return external is null ? … : MapExternal(external.SendFromOperator(request.LeadSessionId, request.Workspace, request.Text, request.IdempotencyKey));`.
- `src/AgentTeamForge.Host/Features/WebConsole/WebConsoleServer.cs`: add the route and its body parser.
  - Add `record WebLeadMessageBody(string? Text, string? Workspace, string? IdempotencyKey)` and register it with `[JsonSerializable]`.
  - Add the route `("POST", ["leads", var id, "messages"]) when Guid.TryParseExact(id, "D", out _) => await ReadLeadMessageAsync(ctx, id)`.
  - Validate the same way as `ReadJoinTicketAsync`: workspace is fully qualified, text is 1–65536 characters, and the key is present.
- Tests: one focused scenario in `tests/AgentTeamForge.Tests/Scenarios/ExternalJoinScenarios.cs`, or in `WebConsoleScenarios`.
  - Web POST, then lead `read_messages` returns `from: "operator"` with the text.
  - The same idempotency key sent twice gives one message.
  - An unknown or closed lead returns `invalid_session`.
  - The wake path is already covered by the existing external-message wake tests.

*Frontend (Sonnet), in `app.js` only:*
- In the lead composer, add a pseudo-target `'@lead'` as the **first and default** picker option, labelled "Lead session (inbox)". The member jobs follow under "Message member".
  - When `leadTargets` is empty, the composer is still enabled, targeting the lead. Today it says "No member agent session to message yet".
  - Only when the lead card has a `leadSession` (a real lead with a workspace), not for `PRFactory` or "No lead session".
- `composer()`:
  - At `:403`, keep `'@lead'` as a valid `targetJobId`.
  - With `'@lead'` chosen, disable Interrupt and Stop and use the placeholder "Message the lead (lands in read_messages)…".
  - Pass `leadSession` into `composer()`: `cardPanel()` already has it.
- `sendInline()` (`:497-531`): if `attempt.jobId === '@lead'`, `POST /api/leads/{id}/messages` with `{ text, workspace, idempotency_key: attempt.key }`. The retry and discard flow stays as it is.
  - On `ok`: result "Delivered to lead inbox". Do **not** set `deliveryJobId`, because there is no job to track.
- `cardPanel()` (`:823`): when the target is `'@lead'`, show the detail, activity and logs of `targets[0]` if there is one, so the panel does not go empty.

**Needs backend: yes.** There is no existing IPC or HTTP path that inserts a lead-inbox row from the operator. `ExternalLeadSend` goes lead to member, and `external_send` needs a member token.

## 3. State summary

| State | Where | Survives poll | Survives reload |
|---|---|---|---|
| Team open or closed | `savedTeams` → `localStorage['atf.web.teams']` | yes | yes |
| Finished-jobs fold | same map, key `fold:<lead>` | yes | yes |
| Selection | `expandedKey` (memory) | yes (wide: even when its team is collapsed) | no (unchanged) |
| Pane and inner scroll | captured and restored in `loadJobs`/`renderActivity` | yes | no |
| Drafts, logs, activity | existing maps | yes | no |

## 4. Slices (one Sonnet implementer, frontend only)

1. **Collapse controls**: Collapse all / Expand all, the limit on `savedTeams`, and the optional finished-jobs `<details>` fold. Keep `class="team-toggle"` and `class="team-content"` exactly as they are.
2. **Master-detail layout**:
   - `index.html` wrapper and pane.
   - The wide `@media` block in `app.css`.
   - In `app.js`: `wide`, `ownerCard`, `placeDetail()`, the wide click, ARIA and clear-selection rules, and the breakpoint change listener.
3. **Scroll preservation**: save and restore the pane and inner scrollers in `loadJobs()`, plus the fix in `renderActivity()`.

4. **BACKEND (Codex): operator to lead-inbox send.**
   - Files: `ExternalMemberStore.cs`, `ExternalTeam.cs`, `IpcMessages.cs`, `JobsEndpoint.cs`, `WebConsoleServer.cs`, plus one scenario test (§2c).
   - Independent of slices 1–3, so it can run in parallel.
5. **FRONTEND (Sonnet): "Lead session (inbox)" composer target** (§2c).
   - Depends on slice 4's endpoint. It can be written in parallel against the agreed contract: `POST /api/leads/{id}/messages {text, workspace, idempotency_key}`, returning the normal `IpcResponse`.

Slices 2 and 3 touch the same `loadJobs()` tail and can ship as one commit. Existing `WebConsoleScenarios` must stay green; run them on Linux. Slices 1–3 and 5 get no new tests: they are layout and UI, and AGENTS.md says not to assert on formatting. Slice 4 gets the focused test from §2c. Each slice gets one opposite-family review: Claude reviews the Codex backend, and Codex reviews the Sonnet frontend.

## 5. Manual smoke test

Start `atf web` with at least 8 lead sessions and 20+ jobs, a mix of running, completed and failed. The fake backend is fine. Check in both themes.

**Wide (≥1100 px, e.g. 1440 px):**
- [ ] The tree is on the left and the pane on the right, showing the "Select…" hint. No horizontal scroll.
- [ ] Clicking a job shows its composer, result, activity and logs in the pane. The card shows `.selected`, and no inline panel opens in the tree.
- [ ] Clicking a lead card shows the lead panel (member picker and join ticket) in the pane.
- [ ] Clicking the selected card again keeps it selected. Close and Escape clear the pane.
- [ ] Wait through at least 3 polls (15 s):
  - [ ] The selection, the pane scroll, the activity-list scroll and the open raw-logs scroll all stay put.
  - [ ] A scroller parked at the bottom follows new entries.
  - [ ] A composer draft and the caret survive.
- [ ] Collapse the team that holds the selected job. The pane stays populated, and the next poll does not clear it.
- [ ] Collapse all, then Expand all. Both persist after a page reload.
- [ ] The finished-jobs fold opens and closes and persists. It is forced open when the selected job is inside it.
- [ ] The pane stays sticky while the long tree scrolls.
- [ ] Change the page or status filter so the selected job disappears. The pane returns to the hint.

**Narrow (<1100 px, e.g. 900 px and 390 px):**
- [ ] Behaviour matches today: inline accordion, one open at a time, and no pane visible.
- [ ] Collapse all, Expand all and the fold still work. The 680 px mobile rules are unchanged.
- [ ] The inline activity and log scroll now survive polls.

**Resize across 1100 px with a selection:**
- [ ] Wide to narrow: the panel returns inline under its card, still open.
- [ ] Narrow to wide: the panel moves into the pane.
- [ ] The draft is kept in both directions.

**Message the lead (slices 4 and 5):**
- [ ] Set-up: a real lead session (Claude Code or Codex) with a registered native wake.
- [ ] In the lead panel, the picker defaults to "Lead session (inbox)", and Interrupt and Stop are disabled.
- [ ] Send "hello from web". The lead gets the native wake notice within about 2 s, and its `read_messages` shows `from: "operator"` with the text.
- [ ] Simulate a lost response (DevTools offline, then retry the same message). The lead sees exactly one message.
- [ ] Switching the picker to a member job still does the normal follow-up, and delivery tracking still works.
- [ ] A lead with no member jobs can still be messaged.
- [ ] A closed lead session shows an `invalid_session` error in the composer.

**Regression:**
- [ ] New agent, Stop, follow-up send/retry/discard and Settings/back all work.
- [ ] `WebConsoleScenarios` passes.
