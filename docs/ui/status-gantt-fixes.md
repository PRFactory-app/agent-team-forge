# Status Gantt review fixes

2026-09-26. Claude-authored fix to `docs/project-status.html` (base `0ef8af0`)
for the two blocking findings in the independent review
`docs/ui/status-gantt-review.md` at `8a99605`. Documentation page only; no
scope facts changed. Needs independent Codex re-review before promotion.

## Changes

- **B1 (narrow sticky labels).** At widths of 760 px or less, the Gantt table no
  longer relies on a sticky first column inside a horizontal scroll region. Each
  epic or track row is stacked as a card: epic label and status text, then only
  its bars, each preceded by its wave label (`W0–W2`, `W3`, …) from a new
  `data-w` attribute. Empty gap cells, the column header row and the scroll
  container's overflow are dropped on narrow screens. Wide layouts are
  unchanged. The hint text now describes the stacked layout.
- **B2 (green mixed group).** The `Built today` group badge uses a new neutral
  outline style and reads `Built today (mixed status)`. Green (`.ok`) now
  appears only on the legend key and the M0 fake-core checkpoint row.

## Checks run

- Isolated headless Chromium 152 (fresh temporary profile, driven over CDP
  from a scratch Node script, not committed), 1400 × 1000 and 390 × 1000, light
  and dark: opening view, Gantt in view, and after setting the chart region's
  `scrollLeft` to 450 px. Screenshots were inspected visually.
- Before the fix, this Chromium still painted the P01–P04 labels after the
  450 px scroll, so the exact B1 paint failure did **not** reproduce here. The
  scrolled view did lose the column headers and caption context. The fix
  removes the sticky/scroll dependency on narrow screens instead of tuning it.
- After the fix at 390 px, the region has no horizontal overflow (scroll
  width 324 = client width), so `scrollLeft` stays 0; every epic label and later
  wave (W3–W9, including P08 and F27 runtime at W6) is visible in the page flow
  in light and dark. The 1400 px chart renders as before.
- Node read-only structure check: 114 unique IDs, 65 work-package IDs, 27
  feature IDs, 8 epic IDs, no broken local `#` links, no script or external
  asset. The failed 73/74 inspection snapshot text is unchanged.
- `git diff --check` passed.

## Limits

- One Chromium build on Linux only; no Firefox, Safari or real mobile device.
- Stacked rows use `display:block` on table elements, which some screen
  readers may no longer announce as a table on narrow screens; the Epics
  section still holds the full text. No formal assistive-technology audit.
- Screenshots are evidence of this render, not a CSS-based paint guarantee.
