# Web token counter — frontend (slice 3)

Implements §2b/2d/3 of `web-token-counter-plan.md`, coded against the contract
(`jobs[].session_tokens`, response `lead_tokens`). Missing fields are treated as unknown.

## Changes (`src/AgentTeamForge.Host/Features/WebConsole/wwwroot/`)
- `app.js`: helpers `fmtTokens` (`999`, `12.3k`, `1.29M`), `tokenTip` (input · output · cache read · cache write),
  `usageSum` (dedupes by `session_id`, ignores null), `addUsage`, `tokenSpan`.
  - Agent node: `.tokens` in `.card-side` before `elapsed`: `12.3k tok`; `—` when `session_id` exists and
    `session_tokens` is null; nothing without a session or when the field is absent (old backend).
    Tooltip adds "session total (shared by N jobs)" when N>1 jobs on the page share the session.
  - Lead card: `lead 45k tok` / `lead —` (from `lead_tokens[lead]`, only if the key is present; not for
    PRFactory / No lead session) as a `.tokens` span in the `.node-meta` line. The `Lead <id8>` label,
    `span.session-id` and the lead dropdown are untouched.
  - Team header: `.team-tokens` `Σ 57.3k` inside `.team-toggle` (lead own + deduped member sessions); hidden
    when nothing is known. `team-toggle`/`team-content` classes unchanged.
  - Top: `#token-total` = deduped job sessions + all non-null `lead_tokens`, tooltip with breakdown.
- `index.html`: 4th stat `TOKENS · SHOWN` (`#token-total`).
- `app.css`: `.tokens, .team-tokens` (mono 11px, tabular-nums, muted, nowrap).

## Verification
- `node --check app.js` OK.
- Headless Edge against a small mock API (hand-made JSON: shared session between 2 jobs, null usage, queued
  job without session, null lead, 1.2M session) at 1400px (master-detail) and 390px: counters render, no
  overflow from the new elements; Σ = 45k + 12.3k (shared session counted once) = 57.3k; top total 1.29M.
  Poll state preservation code untouched (spans are built inside the existing render pass).

## Gaps
- Not checked in dark theme, nor against the real backend (being built in parallel).
- Tooltip is one line (separator ` — ` for the extra note) rather than multi-line.
- Edge headless clamps narrow windows, so the 390px shot was wider than 390; the 680px CSS rules were not modified.
