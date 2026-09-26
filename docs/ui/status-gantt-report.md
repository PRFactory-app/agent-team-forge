# Status Gantt page: change report

2026-09-26. Replaces the overview and epic visuals of
[`docs/project-status.html`](../project-status.html) with a relative-wave Gantt
page. Only this report and that page changed; no runtime or other docs.

## What changed

- The first screen now shows the title "no phase is complete", a one-line
  built/not-built headline and two lists: **Built today** and **Not built yet**.
- The Gantt table has rows P01–P08 across ordered waves W0–W9. It shows no
  dates, durations or percentages. The first column stays fixed, every row states
  its status in text and the current wave (W0) is highlighted.
- Visual encoding: hatched gray = planned/not started; solid blue = in
  progress; outlined blue = partial bounded experiment; dashed amber =
  exploratory spike only; green = a bounded reviewed checkpoint only, used only
  in the separate early-checkpoint track; ⚑ = named blocker.
- Plan readiness (all phase plans are drafts) is shown apart from
  implementation status.
- A separate section lists entry gates and specific blockers for every epic.
  Future epics have no vague blanket "requires review" labels.
- The 65 work packages (IDs `P0n-Wnn` kept as anchors) and 27 features are in
  collapsed `<details>` blocks, so they stay available without crowding the page.
- Status facts: no phase complete; P01 is in progress and not accepted. P02
  has only the bounded Linux fake-core experiment (`81a11b2`), and inspection
  was merged at `765fe1d`, where the full gate failed 73/74, so main promotion is
  held. P03 has only exploratory spikes. P04–P08 have not started. F27 has a
  design and static mockup only. The superseded E01–E08 pending review states
  were removed and replaced by a short evidence list.

## Checks run and limits

- A Python structure script checked tag nesting, unique IDs (114), local
  `href` targets (all resolve), 65 package anchors, 27 features, 8 epics and 10
  wave columns in every Gantt row. It also checked that the page has no scripts,
  network URLs or absolute home paths.
- Headless Chromium with an isolated scratch profile took screenshots at 1400 px
  and 390 px (dark scheme). The wide layout was verified visually. On narrow
  screens the chart scrolls sideways with a fixed epic column.
- Not done: light-scheme screenshot, formal accessibility audit, HTML linting
  (no tool exists) or rerunning runtime gates. The page is a documentary
  snapshot, not live telemetry. It needs an independent Codex review before
  promotion to `main`.
