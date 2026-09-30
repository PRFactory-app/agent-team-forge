# Named agent tabs + lead session names — code review

Reviewed commit: `ba49f19` (feat: name agent tabs and lead sessions), checked against
`docs/fixes/named-agent-tabs-plan.md` and `docs/fixes/named-agent-tabs-implementation.md` (`cb523a7`).
Reviewer: Claude (read-only). Uncommitted working-tree changes were not reviewed.

## Verdict: **APPROVE**

Findings: 0 blocker, 0 major, 1 minor.

## Slice A — tab titles

Verified:

- `WtInteractiveBackend.cs:64` sets `TabLabel = HerdrInteractiveBackend.TabLabel(_kind, request.DisplayName, request.JobId)`.
  `AgentName` is still `"atf" + 20 hex`. That value still keys `_tabs`, the wrapper/sidecar paths and the
  `atf*.launch.job` recovery files. The new test asserts both values.
- `WtTabControl.cs:67` passes `--title` `TabLabel ?? AgentName`. The value goes through `WtCommandLine.Arguments`,
  so `EscapeDelimiter` escapes `;`. That means WT can't split it into a new subcommand. It is also always prefixed
  with `"<kind>: "`, so it can never start with `-` and be read as a WT flag.
- `MacTabControl.cs:69`: kitty receives the title as one `ArgumentList` entry, so it is not shell-parsed.
  Terminal.app doesn't use the title at all. No injection path.
- Name source: `DisplayName = claim.Job.TargetAgent` (`DispatchJob.cs:701`). `TargetAgent` is `NOT NULL`. It is
  either a validated name (`AcceptJob.ValidAgentName`: ASCII letters/digits, `-`, `_`, ≤64; the web console
  applies the same check) or the default `<backend>-<hash8>`. So labels contain no metacharacters.
- Resume/follow-up: `FollowUpJob.cs:172` copies `parent.TargetAgent` to the child job. The follow-up dispatch then
  builds a new `BackendRequest` with the same `DisplayName`, so a reopened tab gets the same title. The live-reuse
  path doesn't open a new tab. `FindRecoveredLaunch` rebuilds a launch without a `TabLabel`, but it is only
  used for stop/liveness, never to open a tab. That's fine.

## Slice B — lead session name

Verified:

- **Migration V29** (`Schema.cs:363`): `ALTER TABLE lead_sessions ADD COLUMN display_name TEXT;` adds a
  nullable column with no default, so existing rows get NULL. It runs inside the single migration transaction in
  `Schema.Apply`, together with the `schema_migrations` insert. No other code copies `lead_sessions` with
  `SELECT *` or rebuilds the table. The only `INSERT` (`LeadSessionStore.Start`) names its columns, so the new
  column doesn't break it. `SchemaTests` downgrades v29→v28 and upgrades again; that passes.
- **`LeadSessionStore.Rename`**: filters `WHERE session_id=$id AND workspace=$ws AND closed_at IS NULL`, and
  returns `true` only when exactly one row matched. SQLite counts matched rows, so renaming to the same value
  still returns `true`. A closed session or another workspace returns `false`, which becomes `not_found`.
  `Start`'s upsert (`ON CONFLICT … DO UPDATE SET binding_key, updated_at`) and `Resume` don't touch
  `display_name`, so a name survives bridge restarts and re-adoption.
- **Cross-session rename**: in the bridge, `set_session_name` always sends the bridge's own
  `sessionId`/`workspace` (`JobsMcpBridge.cs:302`). An MCP lead can't choose a target. Raw IPC is authorized by
  `session_id + workspace`, the same trust level as the existing `SessionClose`, which is more destructive.
  So this adds no new privilege. External-member bridges (`externalOnly`) only dispatch tools from their own
  filtered list.
- **Validation** (`JobsEndpoint.cs:120-131`): trims, rejects more than 64 chars or any `char.IsControl`, and
  treats empty/whitespace as clear (NULL). Tests cover this. A trailing `\n` is rejected instead of trimmed
  (the check runs on the untrimmed input). That is stricter, which is acceptable.
- **Surfacing**: `session_info` returns `Name` (serialized as `name`). `list_jobs` / the web console get
  `lead_name` from the snake_case source-generated context. The sub-select filters `closed_at IS NULL`, the
  same way as `lead_workspace`.
- **Web console escaping** (`app.js`): the name only reaches the DOM through `element()`, which sets
  `textContent`, and through `title` (an attribute property) and `<option>` text. The file has no `innerHTML`,
  so there is no XSS. `leadNames` is cleared on reset, and a name is removed when a job reports no `lead_name`,
  so clearing the name falls back to the id. `syncLeadOptions` includes `leadNames` in its cache key, so a
  rename refreshes the dropdown.

### Finding 1 — minor

- **File**: `src/AgentTeamForge.Host/Features/WebConsole/wwwroot/app.js:928-940`
- **Problem**: `leadNames` is filled only from `jobs`. A lead group that comes only from `external_members`
  (a lead that has invited an external member but has no job on the current page) shows its raw id, even
  though the lead has named itself. This also happens when a lead's jobs have scrolled off the first page.
- **Scenario**: a lead calls `set_session_name("planner")`, then invites an external member without submitting
  a job. The console shows "Lead 1a2b3c4d" instead of "Lead planner". Nothing breaks and the name shows as soon
  as a job appears.
- **Suggested fix** (optional): also return `lead_name` on the external-member rows, or on a small
  sessions list, and fill `leadNames` from it. Or accept this as the documented scope ("built from the job
  list", plan §2).

### Observations (not bugs, no action required)

- `RecoverableLeadSession` in `session_info` doesn't carry the name. A restarted lead picking a session to
  `resume_session` still sees only ids. This could be a follow-up.
- `char.IsControl` doesn't reject Unicode format characters such as bidi overrides (U+202E) or zero-width
  characters. The output is `textContent`, so this is display spoofing by the same local user at most, not
  injection.

## Tests

Tests ran in a separate detached worktree of `ba49f19` in the scratch directory. It has been removed. The
dotnet used was `C:\Projekt\git\agent-team-forge\.tools\dotnet11\dotnet.exe`.

- New and changed tests pass: `Named_job_uses_display_label_and_keeps_recovery_identifier`,
  `Set_session_name_maps_to_session_info` (both cases), `Named_lead_is_exposed_in_job_list`,
  `Rename_is_durable_workspace_scoped_and_rejects_closed_sessions`,
  `Session_info_renames_validates_and_clears_name`, and all of `SchemaTests`: 13/13 passed.
- For the affected classes (`LeadSessionTests|ListJobsTests|SchemaTests|JobsMcpBridgeTests|WtInteractiveBackendTests`),
  8 tests fail on this machine. The **identical 8 tests also fail on the parent commit `6134801`**. The
  failure sets were diffed and are identical, so there is no regression. The causes are environmental:
  Pi launcher unavailable, `jobs.db` file locks during temp-dir cleanup, and path/wrapper assertions under the
  long temp path.
- A full suite run from the temp path gave 316 failed / 852 passed. That was not compared line by line and is
  dominated by the same environmental causes. It is not evidence either way. The integrator's normal full run
  in a regular worktree is the reference.

---

# Addendum — commit `0921fe1` (Claude `crossSessionInbound` on Windows)

Reviewed commit: `0921fe1` (Enable Claude crossSessionInbound in setup on Windows). It drops the
`!OperatingSystem.IsWindows()` guard at `ClientSetup.cs:121`, adds `CheckRequiresClaudeInboundWithoutChangingSettings`,
and updates the docs. Read-only review; uncommitted working-tree changes were not reviewed.

## Verified

- **Other keys are kept**: `SetupCommand.EnableClaudeInbound` (`SetupCommand.cs:769`) copies every top-level
  property except `crossSessionInbound` with `JsonElement.WriteTo`, then appends `"crossSessionInbound":"accept"`.
  It writes to a temp file and moves it into place, and does nothing if the value is already `accept`.
  The new test and `ApplyReconcilesAndPreservesOtherEntries` cover this.
- **Output encoding**: `Utf8JsonWriter` on a `FileStream` writes UTF-8 **without** a BOM. Good.
- **`claude --version` → 127 on Windows**: `SetupCommand.RunCommand` (`SetupCommand.cs:905-911`) runs
  PowerShell `Get-Command -CommandType Application,ExternalScript`. `Application` resolves `.exe`/`.cmd`/`.bat`
  through PATHEXT and `ExternalScript` resolves `.ps1` shims. If neither is found it exits 127, and a
  `Win32Exception` also maps to 127. The Claude MCP registration step (`ClientSetup.cs:65`) already used this
  same check on Windows, so nothing new can go wrong here.
- **Users without Claude**: 127 skips the whole block. There is no settings read, no hard failure, and no
  `crossSessionInbound missing` in `--check`.
- **Check/doctor**: `apply: false` only reads, leaves the file unchanged (the test asserts this), and reports
  `claude: crossSessionInbound missing`. This is an intended change: after upgrading, existing Windows installs
  will fail `atf setup --check` until `atf setup` is rerun. The docs say so.

## Finding 2 — major

- **Files**: `src/AgentTeamForge.Host/Features/Setup/ClientSetup.cs:389` (`ClaudeInboundCurrent`) and
  `src/AgentTeamForge.Host/Features/Setup/SetupCommand.cs:773` (`EnableClaudeInbound`)
- **Problem**: both call `JsonDocument.Parse(File.ReadAllBytes(path), …)`. That overload does **not** skip a
  UTF-8 BOM. I checked this against the repo's dotnet 11, and it fails with
  `JsonReaderException: '0xEF' is an invalid start of a value`. `JsonReaderException` is a `JsonException`, so
  `ClientSetup.cs:140` catches it and reports `claude: failed (settings: …)` with `hardFailure = true`. Because of
  that, `atf setup` returns exit 1 even with `registrationFailureNonFatal: true` (`SetupCommand.cs:171-175`), and
  login autostart is never applied.
- **Why it matters now**: before this commit, Windows never reached this code. On Windows, a
  `settings.json` with a BOM is a realistic file. For example, Windows PowerShell 5.1
  `Set-Content -Encoding UTF8` / `Out-File -Encoding utf8`, or an editor that saves "UTF-8 with BOM", both
  produce one. `MalformedClaudeSettingsStillFailSetup` shows that ATF treats such a file like corrupt JSON.
  (This machine's own `~/.claude/settings.json` has no BOM, so the bug stays hidden locally.)
- **Scenario**: a Windows user's `%USERPROFILE%\.claude\settings.json` starts with `EF BB BF`. `atf setup`
  prints `claude: failed (settings: '0xEF' is an invalid start of a value…)` and exits 1. Rerunning gives the
  same error until the user re-saves the file by hand. `atf setup --check` also fails with this error instead of
  reporting the real state.
- **Suggested fix**: read with a helper that strips a leading UTF-8 BOM before parsing, such as
  `bytes.AsSpan().StartsWith((byte[])[0xEF,0xBB,0xBF]) ? bytes[3..] : bytes`. Use it in both
  `ClaudeInboundCurrent` and `EnableClaudeInbound`. The rewrite already drops the BOM, so the file ends up as
  UTF-8 without a BOM. Add one test that writes `"﻿{\"theme\":\"dark\"}"` with a BOM, runs apply, and
  asserts that `theme` is kept, `crossSessionInbound` is `accept`, and the file no longer starts with `EF BB BF`.

## Observations (no action required)

- Comments in `settings.json` are parsed with `CommentHandling.Skip` and are lost when the file is rewritten.
  This is pre-existing behaviour on Unix, and Claude's own settings file normally has no comments.
- On Windows, `File.Move(..., overwrite: true)` can briefly fail with a sharing violation if another process
  has `settings.json` open without delete sharing. That becomes a hard `claude: failed (settings: …)`, and
  rerunning setup fixes it. I did not observe it, so this is not a finding.

## Tests (0921fe1)

I ran `SetupCommandTests` in separate detached worktrees of `0921fe1` and of its parent `cb523a7`, both since
removed.

- `0921fe1`: 34 passed, 5 failed. `cb523a7`: 31 passed, 7 failed.
- The 5 remaining failures are the same on both commits: `PartialRegistrationFailureCanBeRerun`,
  `LoginAutostartWritesAndRemovesLinuxUnitAndMacPlistInTempHome`,
  `ConcurrentBridgesStartOneDaemonThatSurvivesBridgeExit`, `StableBinaryFollowsCurrentReleaseLink` and
  `CurrentRelease_MapsOldReleaseImageToCurrent`. They come from the environment (Linux/mac-only paths,
  symlinks), so they are not a regression.
- The commit fixes 2 tests that previously failed on Windows (`ApplyReconcilesAndPreservesOtherEntries`,
  `MalformedClaudeSettingsStillFailSetup`), and the new test passes.

## Overall verdict for branch `feat/named-agent-tabs` (ba49f19 + 0921fe1): **CHANGES_REQUESTED**

Findings: 0 blocker, 1 major (Finding 2: a UTF-8 BOM in Claude's `settings.json` makes `atf setup` fail on
Windows), 1 minor (Finding 1: the console name is missing for leads that have only external members). Once
Finding 2 is fixed with the BOM-tolerant read and one test, the branch can be approved. Finding 1 is optional.
