# D3 Claude hook characterization — independent code review

2026-09-26. Reviewer: GPT/Codex, separate from the Claude Opus author.
Reviewed immutable commit `77c024df0a3dbfe59307af06e9475dbce1261e67`
against direct parent `2d6d0c900ff436efce0aa81b9069771f58f8913f` and the
D3 contract in `docs/spikes/wave2-slices.md` (plan-wave2 worktree).

**Verdict: approve this bounded test-only characterization. Blockers: 0.**
This approves negative fixtures for D11, not a managed Claude adapter, origin
proof, cancellation, or terminal-result support.

## Review findings

- The three added paths are exactly the D3-owned fixture, characterization tests,
  and report. No production project, inbox, CLI, or other slice file changed.
  `77c024d` is a single commit on `2d6d0c9`; source commit `8a5e385` is not
  an ancestor, so no legacy runtime was merged through ancestry.
- Confirmed source commit
  `8a5e385dd11ca4105c145c452e990c823eaf2654` is the bounded Claude
  isolation snapshot approved in `m0-safety-lanes-review.md`. Compared the
  fixture against its `ClaudeHookRecord.Build`, `ClaudeHookLog.Fold`,
  `JobTag.Extract`, `JobReconciler.Reconcile`/`IsTerminal`, and
  `ClaudeCapabilityGate.NonAuthoritative` implementations. The imported behavior
  matches, apart from nesting, names, omitted unrelated methods, and comments.
  `ClaudeCorrelation`, `ClaudeInbox`, send/cancel commands, and hook file I/O
  were not copied.
- The tests call the imported record builder, fold, reconciler and capability
  gate. They check session rejection, torn-line uncertainty, orphan Stop,
  and downgrading even an optimistically job-bound completed turn. The
  same-input human/harness equality assertion is illustrative; the operative
  protection is the tested non-authoritative gate. No finding blocks D3.

## Checks

- `git diff --check 2d6d0c9 77c024d`: pass.
- `DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet scripts/verify.sh`:
  restore and format pass; Release build with `-warnaserror` succeeds with
  zero warnings; 64/64 tests pass; Linux x64 AOT publish succeeds; native
  published smoke passes 19/19.
- Mutation sensitivity: temporarily inverted the fixture's session filter and
  replaced `NonAuthoritative` with identity. All three D3 tests failed (0/3).
  Restored the exact fixture bytes; targeted D3 tests then passed 3/3, and the
  tracked tree returned clean before this review document.

No live Claude, Windows, macOS, native hook-process, or origin qualification
was run. Those claims remain outside D3.
