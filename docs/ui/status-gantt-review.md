# Independent status Gantt review

2026-09-26. Reviewed Claude-authored `0ef8af0` (`docs/project-status.html` and
`docs/ui/status-gantt-report.md`) against the recorded milestone evidence.

**Verdict: changes required before HTML promotion.** Two small presentation
defects conflict with the requested Gantt and status language. No HTML or
runtime code was changed by this reviewer.

## Blocking findings

**B1 — Epic labels disappear after horizontal chart scroll.** In isolated
headless Chromium at a 390 × 1000 viewport, open the offline page, scroll the
`#gantt` section into view, then set the chart region's `scrollLeft` to 450 px.
The sticky first column remains at the left edge, but the P01–P04 row labels and
their status text are no longer painted. The labels are still present in the DOM
and hit testing returns their elements. The chart region is 307 px wide with a
900 px table; the sticky row header stays at x=34 px before and after scroll.
This makes later waves unreadable alongside their epic names on a narrow screen.
See the sticky row-header CSS at `docs/project-status.html:45` and the narrow
rule at line 76. Have the Claude author fix and independently recheck the actual
render at both initial and nonzero horizontal scroll positions.

**B2 — Green marks a mixed-status group.** The green `Built today` badge at
`docs/project-status.html:94` uses the same `.ok` treatment as the approved
bounded checkpoint. Its list also contains job inspection with a failed full
gate, exploratory spikes, and design documents. The change report says green
means a bounded reviewed checkpoint only. Give this grouping a neutral label
style and retain green solely for the bounded checkpoint key and row.

## Status and evidence checks

- The page uses ordered W0–W9 waves, with no displayed dates, durations or
  completion percentages. Its headline says no phase is complete. The eight
  epic rows distinguish P01 planning/feasibility in progress, P02's bounded
  fake experiment, P03 exploratory spikes, and P04–P08 not started. The green
  Gantt bar is confined to the separate bounded M0 checkpoint track. F27 is
  explicitly design plus static mockup, with no runtime web console.
- The source boundary is correctly distinguished: `81a11b2` has the earlier
  reviewed Linux fake-core checkpoint (61/61 tests, 19/19 published scenarios,
  one published demo). The later `765fe1d` integration record reports a failed
  full suite twice (73 passed, one failed, zero skipped), while its AOT and
  published process checks passed separately. Main promotion remains held.
  These facts were compared with `docs/spikes/m0-fake-core-integration.md` and
  the integration branch's `docs/spikes/m0-promotion-check.md`; they do not
  establish a real agent, product phase or Windows/macOS support.
- A Node read-only structure check found 114 unique IDs, 65 work-package IDs,
  27 feature IDs, eight epic IDs, and no broken local `href` targets. The page
  has no script or network asset. The table has a caption, column and row
  headers, text status labels and a labelled, keyboard-focusable scroll region.
  Native section links and details targets resolve; this is a basic semantic
  check, not a formal assistive-technology audit.
- Isolated Chromium screenshots at 1400 px and 390 px show a readable opening
  status summary. The wide chart renders clearly. The mobile chart is readable
  at its initial horizontal position, then fails as described in B1.
  `git diff --check main...0ef8af0` passed. No runtime gates were rerun for
  this documentation-only review.

After B1 and B2 are fixed, re-review the exact HTML diff and repeat the narrow
horizontal-scroll render. Approval here would cover only this documentary page,
not source promotion of the fake-core runtime to `main`.
