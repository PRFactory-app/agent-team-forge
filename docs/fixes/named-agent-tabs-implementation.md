# Named agent tabs and lead session names — implementation

Implemented Slice A followed by Slice B on `feat/named-agent-tabs`. No push.

Implementation commit: `ba49f193fdf155ba016a961fcfd38de3982ef8ea`
(`feat: name agent tabs and lead sessions`). This report is included in a
separate documentation commit; its commit can be identified with
`git log -1 -- docs/fixes/named-agent-tabs-implementation.md`.

## Changes

- Windows Terminal and macOS receive the shared `TabLabel` (`codex: reviewer-1`,
  or the existing backend/job-id fallback). The random `atf<20 hex>` AgentName,
  recovery filenames, wrapper paths and process ownership keys stay unchanged.
- Added MCP `set_session_name(name)` alongside `session_info`. It uses the
  existing SessionInfo IPC operation with the new SessionName field, returning
  updated session info. Missing/non-string names are rejected by the bridge.
  The endpoint trims names, rejects names over 64 characters and control
  characters, and converts empty names to SQL NULL.
- Schema V29 adds nullable `lead_sessions.display_name`. Rename requires the
  session id, matching workspace and an open session. Session info reads Name.
- Job list persistence and response models expose `lead_name` in column 17.
- The web console uses lead names in group headers, lead cards and the new-agent
  dropdown. The card's title tooltip retains the full session id. Clearing a
  name restores id labels and updates the dropdown cache.
- MCP tool lists in usage, migration and orchestration skill docs mention
  `set_session_name`. The requested plan file is committed unchanged.
- Added six focused test cases across terminal, session, list and bridge tests.
  Updated existing old-schema fixtures to remove V29 before replaying upgrades.

## Files

Production changes:

- `src/AgentTeamForge.Business/Features/Agents/Terminals/WtInteractiveBackend.cs`
- `src/AgentTeamForge.Business/Features/Agents/Terminals/WtTabControl.cs`
- `src/AgentTeamForge.Business/Features/Agents/Terminals/MacTabControl.cs`
- `src/AgentTeamForge.Business/Features/Jobs/ListJobs.cs`
- `src/AgentTeamForge.DAL/Features/Jobs/JobRecords.cs`
- `src/AgentTeamForge.DAL/Features/Jobs/JobStore.cs`
- `src/AgentTeamForge.DAL/Features/Sessions/LeadSessionStore.cs`
- `src/AgentTeamForge.DAL/Migrations/Schema.cs`
- `src/AgentTeamForge.Host/Transport/IpcMessages.cs`
- `src/AgentTeamForge.Host/Features/Jobs/JobsEndpoint.cs`
- `src/AgentTeamForge.Host/Features/Jobs/JobsMcpBridge.cs`
- `src/AgentTeamForge.Host/Features/WebConsole/wwwroot/app.js`

Tests:

- `tests/AgentTeamForge.Tests/Features/Agents/Terminals/WtInteractiveBackendTests.cs`
- `tests/AgentTeamForge.Tests/Features/Sessions/LeadSessionTests.cs`
- `tests/AgentTeamForge.Tests/Features/Jobs/ListJobsTests.cs`
- `tests/AgentTeamForge.Tests/Features/Jobs/JobsMcpBridgeTests.cs`
- `tests/AgentTeamForge.Tests/Features/Jobs/SchemaTests.cs`

Documentation:

- `docs/usage.md`
- `docs/migrating-from-win-agent-teams.md`
- `.claude/skills/agent-orchestration/SKILL.md`
- `docs/fixes/named-agent-tabs-plan.md`
- `docs/fixes/named-agent-tabs-implementation.md`

## Verification

Executed on Windows with SDK `11.0.100-rc.1.26425.128` from the main checkout's
`C:/Projekt/git/agent-team-forge/.tools/dotnet11/dotnet.exe`; linked worktrees
share this SDK, and this worktree has no separate `.tools/dotnet11` directory.
DOTNET_ROOT was set to that SDK directory.

```powershell
dotnet build AgentTeamForge.slnx -c Release --no-restore --nologo -warnaserror
dotnet test tests/AgentTeamForge.Tests/AgentTeamForge.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~Terminals|FullyQualifiedName~LeadSession|FullyQualifiedName~ListJobs|FullyQualifiedName~JobsMcpBridge|FullyQualifiedName~SchemaTests' --logger 'trx;LogFileName=named-tabs-focused.trx'
dotnet test tests/AgentTeamForge.Tests/AgentTeamForge.Tests.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=named-tabs-full.trx'
node --check src/AgentTeamForge.Host/Features/WebConsole/wwwroot/app.js
dotnet publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -c Release --no-restore -p:PublishAot=false -o artifacts/named-tabs-smoke
./artifacts/named-tabs-smoke/atf.exe --version
git diff --check
```

Here `dotnet` denotes the absolute local SDK executable above.

| Check | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| Focused filter, including lead scenarios and schema | 291 | 69 | 5 | 365 |
| Terminals | 248 | 65 | 5 | 318 |
| LeadSessionTests | 7 | 1 | 0 | 8 |
| ListJobsTests | 17 | 2 | 0 | 19 |
| JobsMcpBridgeTests | 11 | 0 | 0 | 11 |
| SchemaTests | 7 | 0 | 0 | 7 |
| LeadSessionScenarios | 1 | 1 | 0 | 2 |
| Full suite (10 minutes 55 seconds) | 849 | 319 | 8 | 1176 |
| Baseline focused filter | 285 | 69 | 5 | 359 |

Final build: succeeded, zero warnings, zero errors (40.93 seconds).
JavaScript syntax and diff whitespace checks passed. Managed publish succeeded;
the published executable returned `atf 0.0.1-dev` with exit code zero.

All six new test cases passed in both focused and full runs: named tab/recovery
identifier, durable workspace-scoped rename/closed-session rejection, endpoint
trim/validation/clear, list lead name/clear, and two bridge mappings (named/empty).

To verify the focused failures, archived original commit
`61348014654de8640f7600fad3581222ad99912b` into the ignored
`artifacts/named-tabs-baseline` directory and built/ran the identical filter.
Comparing failed test names in the two TRX files produced no differences:
all 69 focused failures reproduce on the unchanged baseline. The branch adds
six passes without adding focused failures.

Focused failures include Linux-only libc/Unix file mode/sh operations on
Windows, missing native Pi launcher, Windows path quoting/invalid path
expectations, timezone expectations, wake/session assertions and SQLite
file-lock cleanup. Full-suite failures additionally include git/worktree
cleanup denied on Windows, Unix-only backend operations and platform/runtime
assumptions in other features. A full baseline run was not performed, so the
baseline comparison establishes the focused failure set only.

Local evidence (ignored, not committed): `.named-tabs-build.log`,
`.named-tabs-focused.log`, `.named-tabs-full.log`, `.named-tabs-publish.log`,
`.named-tabs-baseline.log`, both branch TRX files under
`tests/AgentTeamForge.Tests/TestResults/`, and baseline TRX under
`artifacts/named-tabs-baseline/tests/AgentTeamForge.Tests/TestResults/`.

## Plan deviations and limits

- No feature-scope deviations. The optional bridge mapping test was implemented
  by routing this tool through Map, allowing direct verification of SessionName.
- Added the necessary old-schema fixture adjustment beyond the listed files;
  all seven schema tests pass.
- Corrected two issues introduced during implementation: a local-variable name
  collision and reflection-based JSON serialization in a test that triggered
  AOT analyzer errors. The final warnings-as-errors build is clean.
- Published-binary smoke used a managed Windows publish (`PublishAot=false`),
  not Native AOT. Native AOT and Linux/macOS runtime behavior were not tested.
- Live interactive WT/Codex tab-title and browser smoke steps were not run.
  The new terminal test uses the existing fake tab control as the plan requests.
- No opposite-family review was performed by this implementation worker; review
  and integration remain with the parent team. Existing baseline failures were
  reported rather than expanding scope to unrelated platform fixes.

## Additional fix: Claude inbound setup on Windows

Commit `0921fe1a987614e0e5ee22d3a68216a07b173bd3`:
`Enable Claude crossSessionInbound in setup on Windows`.

Removed the Windows guard in ClientSetup so installed Claude clients get
`crossSessionInbound: "accept"` in apply mode and validation in check/doctor
mode on every platform. The existing exit-code-127 not-installed check remains.
The existing apply regression test already runs on Windows; no relevant test
skip needed removal. Added one read-only check-mode regression test, and made
the all-platform behavior explicit in install, usage and native-wake ADR docs.

Windows verification with the same local .NET 11 SDK:

- Solution Release build with `--no-restore --nologo -warnaserror`: passed,
  zero warnings/errors, 53.30 seconds.
- Setup tests (`--filter 'FullyQualifiedName~Features.Setup'`, Release,
  `--no-build --no-restore`): **40 passed, 12 failed, 0 skipped, 52 total**,
  30 seconds. All 12 remaining failed test names also failed in the earlier
  full-suite TRX. The existing apply and malformed-settings tests now pass;
  the new check-mode test also passes. Remaining failures concern Unix file
  modes, Windows symlink privileges, platform-specific command/autostart
  expectations and daemon-log cleanup locks. No new failing test names.
- `git diff --check`: passed. No push and no real user settings modified.

Evidence: `.named-tabs-setup-build.log`, `.named-tabs-setup-tests.log` and
`tests/AgentTeamForge.Tests/TestResults/named-tabs-setup.trx` (ignored).
This section is committed separately so it can record the fix's exact SHA.

## Review fix: UTF-8 BOM in Claude settings

Commit `62bc1516f5df5dbd1e808bd2072c23c74531de16`:
`Accept UTF-8 BOM in Claude settings during setup`.

Resolved Finding 2 (major) with one shared `ReadClaudeSettings` helper that
strips a leading `EF BB BF` before parsing. Both the current-settings check
and inbound-settings rewrite use it, retaining existing trailing-comma/comment
handling. The rewrite still writes UTF-8 without a BOM. Added one regression
test covering a BOM file with `theme: dark`: apply preserves the theme, sets
inbound to accept and removes the BOM; check accepts a BOM file already set to
accept and leaves its bytes unchanged. The supplied review file
`docs/fixes/named-agent-tabs-review.md` is committed as-is.

Windows verification with the same local SDK:

- Solution Release build (`--no-restore --nologo -warnaserror`): passed,
  zero warnings/errors, 29.03 seconds.
- Setup tests (`--filter 'FullyQualifiedName~Features.Setup'`, Release,
  `--no-build --no-restore`): **41 passed, 12 failed, 0 skipped, 53 total**,
  14 seconds. Comparing failed test names with the previous Setup TRX showed
  identical sets. The new BOM test and existing apply, read-only check and
  malformed-settings tests all passed. The 12 failures remain the previously
  documented platform/environment issues; no new failures.
- `git diff --check`: passed. No push; no real user settings modified.

Evidence: `.named-tabs-bom-build.log`, `.named-tabs-bom-setup.log` and
`tests/AgentTeamForge.Tests/TestResults/named-tabs-bom-setup.trx` (ignored).
