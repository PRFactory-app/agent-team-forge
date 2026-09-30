# Named agent tabs + lead session names — plan

Branch `feat/named-agent-tabs`. Two small, independent slices.

## 1. Windows Terminal tab titles show `atf<20 hex>`

### Root cause

- The job name is already on the backend request. `submit_job.name` → `TargetAgent`,
  with the default `"<backend>-<8 hex>"` (`AcceptJob.cs:87`, from 91a72ba). Then
  `DispatchJob.cs:701` sets `BackendRequest.DisplayName = claim.Job.TargetAgent`.
- Herdr was fixed in 04dbbcc. It sets `TabLabel = TabLabel(kind, request.DisplayName, jobId)`
  (`HerdrInteractiveBackend.cs:69`, helper at `:131`, giving `"codex: reviewer-1"`), and
  `HerdrAgentControl.cs:42` opens the tab with `launch.TabLabel ?? launch.AgentName`.
- **WT never got that fix.** `WtInteractiveBackend.cs:60-63` builds the launch with only
  the random `agentName = "atf" + hex`, and doesn't set `TabLabel`. `WtTabControl.cs:67`
  passes `--title launch.AgentName`. The shared backend also drives macOS, and
  `MacTabControl.cs:69` has the same problem.
- Title override: `--suppressApplicationTitle` is **already** passed (`WtTabControl.cs:67`).
  Claude and Codex can't overwrite the WT tab title with OSC/SetConsoleTitle.
  Only the conhost fallback (`StartConsoleAsync`, `WtTabControl.cs:99`, when `wt.exe` is
  missing or `%` makes the args unsafe) has no fixed title, and the TUI sets it there.
  That's out of scope, because it's a fallback path.
- `AgentName` must stay the random value. It keys `_tabs`, the `atf*.launch.job` recovery
  files (`WtInteractiveBackend.cs:98`) and wrapper paths. Change only the display title.

### Change (Slice A)

- `WtInteractiveBackend.cs:62`: add `TabLabel = HerdrInteractiveBackend.TabLabel(_kind, request.DisplayName, request.JobId)`
  to the initializer. It already produces `codex: <name>` and falls back to `codex: <jobId[..8]>`.
  If the helper should be shared, move it to `InteractiveLaunch` or a small static class.
  Otherwise just call it.
- `WtTabControl.cs:67`: `"--title", launch.TabLabel ?? launch.AgentName`. `WtCommandLine.Arguments`
  already escapes `;` in options, so user-supplied names are safe.
- `MacTabControl.cs:69`: pass `launch.TabLabel ?? launch.AgentName` as the title.
- No change for resume/follow-up. A new tab on resume takes the same `TargetAgent` through
  `DisplayName`.

### Test

- One test in `WtInteractiveBackendTests.cs`: start with `DisplayName = "reviewer-1"` using
  the existing fake `IWtTabControl`, then assert the captured `launch.TabLabel == "codex: reviewer-1"`.
  Don't add argv-formatting tests. The existing `WtCommandLine` tests already cover escaping.

## 2. Lead can name its own session (web console shows it)

### Current state

- The `lead_sessions` table has no name column (`Schema.cs:125`, V7).
  `LeadSessionStore.cs` / `LeadSessionInfo` (`:7`) carry only id and workspace.
- The web console builds lead groups **from the job list**. `app.js:924` groups by `j.lead_session_id`,
  and `lead_workspace` comes from a sub-select in `JobStore.cs:1093` → `ListJobs.cs:58/94`.
  The id shows at `app.js:963` (`'Lead ' + lead.slice(0,8)`), at `app.js:981` (`span.session-id`) and
  in the new-agent lead dropdown at `app.js:334`.
- MCP `session_info` → `IpcProtocol.SessionInfo` (`JobsMcpBridge.cs:232,294`; `JobsEndpoint.cs:114`).

### Design (smallest)

This adds a new MCP tool `set_session_name(name)` that **reuses the `SessionInfo` IPC op**.
It sends `Name`, and the endpoint renames the session before returning info. That means
no new IPC op, no new auth path, and it returns the updated `session_info`.

### Change (Slice B, Codex backend; the JS tweak goes in the same slice)

1. **DAL**
   - `Schema.cs`: `V29 = "ALTER TABLE lead_sessions ADD COLUMN display_name TEXT;"`, bump
     `CurrentVersion` to 29 and add it to `Migrations`.
   - `LeadSessionStore.cs`: add `bool Rename(string id, string workspace, string? name)`, which runs
     `UPDATE lead_sessions SET display_name=$n WHERE session_id=$id AND workspace=$ws AND closed_at IS NULL`.
     Read `display_name` in `Sessions(...)` (`:165`) and add `public string? Name { get; init; }` to
     `LeadSessionInfo`, set in `Info`.
   - `JobStore.cs:1093`: add a sub-select for `s.display_name` next to the workspace one, as a new
     column 17. Add `LeadName` to `JobSummaryRecord` (`JobRecords.cs:118`) and to `ListJobs.cs`
     (`:58`, `:94`), which serializes as `lead_name`.
2. **Host**
   - `IpcMessages.cs` `IpcRequest`: add `public string? SessionName { get; init; }`. Don't reuse
     `TargetAgent`.
   - `JobsEndpoint.cs:114`: if `request.SessionName is not null`, call `sessions.Rename(...)` first
     (`false` → `NotFound`).
   - `JobsMcpBridge.cs`: register `set_session_name` next to `session_info` (`:232`), with schema
     `{name: string}` and the description "Set a display name for this lead session (shown in the
     web console). Empty clears it." Dispatch at `:294` like `session_info`, plus `SessionName = name`.
     Validate in the bridge or endpoint: trim, ≤ 64 chars, no control chars, `""` → null.
     Update `AGENTS`/skill docs only if a tool list is maintained there (grep `session_info`).
3. **Web console (`wwwroot/app.js`)**
   - In the loop at `:924`, fill a `leadNames` map (`id → j.lead_name`) when present.
   - `:963`: `'Lead ' + (leadNames.get(lead) || lead.slice(0, 8))`.
   - `:981`: show `leadNames.get(lead) || lead` in `span.session-id`, and put the full id in `title`.
   - `:334`: use the name in the dropdown label when known.

### Tests

- `LeadSessionTests.cs`: after `Start` and `Rename(id, ws, "planner")`, `Info(...).Name == "planner"`;
  `Rename` on a closed session returns false.
- `ListJobsTests.cs`: a job under a named lead returns `LeadName`.
- One `JobsMcpBridgeTests` case checking that `set_session_name` maps to `SessionInfo` with `SessionName`.
  That's optional. Skip it if the Map helper doesn't cover session tools.

## Slices

| Slice | Scope | Files |
|---|---|---|
| A | WT/mac tab title = `kind: name` | `WtInteractiveBackend.cs`, `WtTabControl.cs`, `MacTabControl.cs`, `WtInteractiveBackendTests.cs` |
| B | Lead session name via `set_session_name`, surfaced as `lead_name`, shown in web console | `Schema.cs`, `LeadSessionStore.cs`, `JobStore.cs`, `JobRecords.cs`, `ListJobs.cs`, `IpcMessages.cs`, `JobsEndpoint.cs`, `JobsMcpBridge.cs`, `app.js`, `LeadSessionTests.cs`, `ListJobsTests.cs` |

A and B don't touch the same files, so they can run in parallel.

## Manual smoke test (Windows, `launch_mode: wt`)

1. Build or publish, then restart the daemon (`--state-dir` outside `%LOCALAPPDATA%`).
2. From a lead, run `submit_job(backend="codex", name="reviewer-1", instruction="say hi")`. The WT tab
   in window `wt-atf` should read `codex: reviewer-1` and stay that way after the Codex TUI starts.
   Repeat without `name`: the tab should read `codex: codex-<hash8>`, the default `TargetAgent`.
3. Run `follow_up` on a finished job that reopens a tab. It should get the same title.
4. Run `set_session_name("planner")`, then `session_info`. It should report `name: planner`. Open the ATF
   web console: the lead group should show "Lead planner", the card should show `planner`
   (hover shows the id), and the new-agent dropdown should list `planner`.
5. Run `set_session_name("")`. The console falls back to the id.
