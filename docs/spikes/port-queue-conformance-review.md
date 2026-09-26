# Independent review: canonical Codex queue conformance port

**Verdict: APPROVED.** Reviewed Claude-authored `8b85cfc` against canonical
base `2d6d0c9`, and compared it with legacy `52648dd` and its independent
review `f64d480`. No blocking findings. Approval covers this offline test
slice only; it does not qualify a native Codex adapter or strict human pause.

## Findings

No blocking or non-blocking code findings. The earlier review's sole
non-blocking condition is resolved for the model: interrupted status remains
after `EndTurn(Interrupted)`, so later `Add` and `Wake` cannot drain a row.
`Resume` separately represents a real thread resume and allows the pending row
to dispatch. The new focused test checks both the hold and subsequent resume.

This matches the pinned Codex source's relevant branches: the external-change
watcher stops for `AgentStatus::Interrupted` ([service.rs 218-225](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/ext/queue/src/service.rs#L218-L225)),
`wake_if_loaded` does not emit an idle lifecycle for an interrupted thread
([472-482](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/ext/queue/src/service.rs#L472-L482)),
and an interrupted idle lifecycle does not dispatch while the resume path is
separate ([538-564](https://github.com/openai/codex/blob/36650394c5b38c2990ccf2a3457165ca3e9d9726/codex-rs/ext/queue/src/service.rs#L538-L564)).
`Resume` is a deterministic model abstraction of an eligible resumed idle
lifecycle, not a claim about native timing or visible TUI state.

## Port and scope evidence

- The commit has one parent, `2d6d0c9`. Neither legacy `52648dd` nor its base
  `81a11b2` is an ancestor. Both merge at older common ancestor `e83ed461`.
  The legacy branch's ancestry was not imported.
- The two C# files were compared byte-for-byte with their legacy review-worktree
  counterparts. Differences are confined to the interrupted-state fix, the
  one regression test, and three restart call sites using `Resume` instead of
  the former combined `Wake`. The report changes provenance, scope language,
  and the condition/gate account. No `src/` or other production file changed.
- The new test would fail under the legacy model: after `EndTurn(Interrupted)`,
  its next `Add` calls `Drain` while `_active` is null, presenting `c1` before
  the hold assertion. The author's historical red-phase execution was not
  independently reconstructed; the committed test and green gates were checked.
- The core cases still drive `DispatchJob`, `JobStore`, and startup recovery
  through the existing fake backend. The model-only cases are clearly scoped
  as source-derived counterexamples, not native execution evidence.

## Independent gates

All commands ran in this review worktree on Linux x64 using
`/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`, SDK
`11.0.100-rc.1.26425.128`.

| Gate | Result |
| --- | --- |
| `DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet ./scripts/verify.sh` | Restore and format passed; Release `-warnaserror` build: 0 warnings, 0 errors; unit suite: **77 passed**, 0 failed/skipped; Linux x64 Native AOT publish passed; published-binary scenarios: **19 passed**, 0 failed/skipped. |
| Release test filter `FullyQualifiedName~CodexQueue` | **16 passed**, 0 failed/skipped. |
| Release test filter `FullyQualifiedName!~CodexQueue` | **61 passed**, 0 failed/skipped. |
| `git diff --check 2d6d0c9..8b85cfc` | Passed. |

No live Codex, Windows/macOS, real-model, power-loss, or native multi-process
qualification was run. The published scenarios exercise the existing host and
fake backend, not native Codex queueing.
