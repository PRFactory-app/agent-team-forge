# D3 — Claude hook characterization (test-only)

2026-09-26. Writer: Claude Opus 5.5 (Claude Code), branch `feature/char-d3-claude-hook`
from `2d6d0c9`. **Not self-approved; needs opposite-family (GPT/Codex) code review.**

## Provenance

Imported logic is copied from branch `spike/m0-claude-isolation` at
`8a5e385dd11ca4105c145c452e990c823eaf2654`, approved (bounded) in
[m0-safety-lanes-review.md](m0-safety-lanes-review.md), from
`spikes/m0-interactive/src/AtfSpike/`:

- `Claude/ClaudeHookRecord.cs` (`Build`, redacted evidence line) and
  `Delivery/JobTag.cs` (`Extract` only, used by `Build`);
- `Claude/ClaudeHookLog.cs` (`Fold`, prompt-lifecycle parser);
- `Delivery/JobReconciler.cs` (`JobState`, `TurnStatus`, `ObservedTurn`,
  `Reconciliation`, `Reconcile`, `IsTerminal`);
- `ClaudeCommands.cs` `ClaudeCapabilityGate.NonAuthoritative`.

Logic is verbatim apart from nesting in one static class and a test-helper
`HookLine`. **Not imported:** inbox dispatch (`ClaudeInbox`), prompt-tag/hash
correlation (`ClaudeCorrelation`), hook runner file I/O, CLI commands.

## Files

- `tests/AgentTeamForge.Tests/Support/ManagedDemo/ClaudeHookFixture.cs`
- `tests/AgentTeamForge.Tests/Features/Agents/Claude/ClaudeHookCharacterizationTests.cs`

## Tests

- `HookRecord_WrongSessionRejected`: Stop/StopFailure/SessionEnd/SessionStart
  from another session, or with no session id, never close or alter the turn.
- `TruncatedHookLog_IsUnknown`: a torn Stop line is counted malformed; the turn
  stays in progress and the gated outcome is non-terminal `NeedsReconciliation`
  with no result.
- `StopHook_AloneDoesNotProveJobOrigin`: an orphan Stop creates no turn; a
  human typing the exact tagged text produces byte-identical evidence; even an
  optimistically bound Stop-completed turn (raw `Completed`) is gated to
  `NeedsReconciliation`, no result, turn key kept.

Characterization tests pass against the imported code (no red phase for
existing behavior). Sensitivity check: disabling the session filter and the
gate made all three fail; the change was reverted.

## Gates

`DOTNET=.tools/dotnet11/dotnet scripts/verify.sh` (SDK 11.0.100-rc.1.26425.128,
linux-x64): restore, format, Release build `-warnaserror` (0 warnings), tests
64/64, AOT publish, published smoke 19/19 — all passed.

## Limits

No live Claude launch, hook process, Windows/macOS or durability test. These
are negative fixtures for D11 only; they establish no Claude origin,
cancellation or completion support. Replace with production code under D11's
reviewed contract rather than keeping a duplicate runtime.
