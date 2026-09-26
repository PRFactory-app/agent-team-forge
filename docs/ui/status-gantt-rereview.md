# Independent status Gantt fix rereview

2026-09-26. Reviewed Claude-authored `4cc8726` against `0ef8af0`, the original
review `docs/ui/status-gantt-review.md` at `8a99605`, and the author's
`docs/ui/status-gantt-fixes.md`. The reviewed diff changes only
`docs/project-status.html` and the fix note.

**Verdict: approved for promotion of the documentary HTML page.** B1 and B2
are resolved in this pinned diff. This is a presentation and source-snapshot
approval, not approval of runtime changes or promotion of the fake-core
integration branch to `main`.

## Findings and checks

- **B1 resolved.** At 390 × 1000 in Chromium, light and dark, the Gantt renders
  as stacked cards. Each epic/track keeps its text status next to its bars; every
  nonempty bar has a visible wave label from `data-w`, including later waves
  W3–W9 and F27's W6 runtime plan. The chart's scroll/client widths were
  309/309 px. After setting `scrollLeft = 450`, it remained 0, with row headers
  in normal (`static`) flow. Screenshots of the actual render showed the labels
  and bars together. At the 760 px breakpoint, the same stacked, nonoverflowing
  layout applies. At 1400 px, light and dark, the table still renders with
  W0–W9 columns, row headers, and Gantt bars.
- **B2 resolved.** The grouping badge reads `Built today (mixed status)` and
  has the neutral style. The only `.ok` elements are the bounded-checkpoint
  legend key and the M0 fake-core checkpoint bar.
- Text remains explicit: no phase is marked complete; P01 is in progress,
  P02 has only the bounded fake experiment, P03 has exploratory spikes, and
  P04–P08 are not started. F27 remains design and static mockup only. The
  source snapshot still distinguishes reviewed `81a11b2` from `765fe1d`'s
  failed full gate (73/74) and held main promotion. Later runtime repairs in
  progress do not alter this pinned documentary snapshot.
- Read-only structure check: 114 unique IDs, 65 work-package IDs, 27 feature
  IDs, eight epic IDs, no broken internal or local links, no script tags, and
  no remote asset references. `git diff --check 0ef8af0..4cc8726` passed.

## Limits

The render check used one Linux headless Chromium build and local-file loading;
Firefox, Safari and physical mobile devices were not tested. No formal
assistive-technology audit was promised or performed. No .NET runtime, AOT,
or platform gates were rerun for this HTML-only rereview.
