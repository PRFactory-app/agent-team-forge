# Web token counter backend — slices 1 and 2

Completed on branch `atf/job-job_01a0f278b7047cdf9c0cc0e1118a5c23`.
Commit: `11b17bd12d05311bc8fd4303e42529ad05fafb91` — Add web session and lead token usage from native transcripts.
Private worktree: `C:\Users\mikael.liljedahl\.local\state\agentteamforge\worktrees\job_01a0f278b7047cdf9c0cc0e1118a5c23`.
No push performed. Worktree clean after commit. No wwwroot changes.

## Changes

Slice 1:
- Added singleton `SessionTokenUsage` with `TokenUsage` (input, output, cache_read, cache_write, computed total).
- Extracted shared `InteractiveTranscriptReader.LocateSession`; both usage and the frozen Claude/Codex transcript readers use the same filename patterns, session-header verification and parent/sidechain checks. Usage locator header reads are capped at 64 KiB per candidate/check; ordinary interactive transcript parsing is unchanged.
- Usage cache keys include kind, session ID and home, preventing different native homes from sharing totals. At most 512 entries, least recently used eviction, 30-second negative lookup caching.
- Parses appended complete JSONL records only, at most 8 MiB per read. Shrinking files reset offsets, totals and Claude IDs. Oversized records are skipped in bounded chunks so they cannot stall the cache forever. Normal partial trailing records wait for their newline.
- Claude deduplicates assistant usage by message.id. Codex takes the last cumulative token_count and subtracts cached input from input. Pi assistant message usage is best effort.
- Malformed records are skipped. Read/lookup failures return the last cached usage or null. Unsupported backends and invalid session identifiers return null.
- Only web GET /api/jobs sets IncludeUsage. JobsEndpoint adds session usage to the returned page; ordinary list calls never read transcripts. MCP list_jobs JSON omits usage fields, preserving its existing output.
- Web JSON explicitly includes session_tokens:null for unknown usage. It uses the source-generated IPC serializer plus a small web-only null-field adjustment; this keeps unknown fields out of MCP JSON.
- Daemon composition creates one usage instance for the endpoint, retained across all web polls.

Slice 2:
- Schema V29 adds nullable native_kind/native_session_id/native_home to lead_sessions.
- JobsMcpBridge captures its inherited CLAUDE_CODE_SESSION_ID and CLAUDE_CONFIG_DIR (default ~/.claude), or CODEX_THREAD_ID and CODEX_HOME (default ~/.codex). Relative homes are resolved against the bridge workspace. Sends all three fields on SessionStart and SessionResume.
- LeadSessionStore Start/Resume persists the binding and overwrites it when resumed. NativeBindings(ids) uses stored native columns first, falls back to a Codex wake target, excludes closed leads, and bounds the query to the maximum page size.
- Web list responses contain lead_tokens for distinct lead IDs present on the returned page, with null for unknown usage. Leads outside the page are not read or returned.

## CLAUDE_CODE_SESSION_ID verification

PASS. JobsMcpBridge reads Environment.GetEnvironmentVariable directly in its MCP process; it does not create a sanitized environment before reading it.
A read-only Windows process-environment probe (OpenProcess query/read access, NtQueryInformationProcess, ReadProcessMemory of PEB process parameters) inspected all three live non-managed `atf.exe mcp` bridges whose direct parent was `claude.exe`:

- PID 18352: CLAUDE_CODE_SESSION_ID=ff2854bd-df5c-42c6-b54d-3ebf86b57231
- PID 25124: CLAUDE_CODE_SESSION_ID=14f5a280-9896-4427-ac0b-cdbaec58d0d9
- PID 24944: CLAUDE_CODE_SESSION_ID=130ff2b2-6ef5-413d-864a-603e968c5129

This establishes that the actual Claude-launched MCP bridges receive the variable, rather than inferring it from this managed Codex child's shell. The child shell had only CLAUDE_AGENT_SDK_VERSION and CLAUDE_PROJECT_DIR, consistent with managed identity stripping. Both Claude and Codex lead capture shipped. The probe was temporary PowerShell/Add-Type glue and is not committed.

## Files changed

- src/AgentTeamForge.Business/Features/Usage/SessionTokenUsage.cs
- src/AgentTeamForge.Business/Features/Agents/Terminals/InteractiveTranscriptReader.cs
- src/AgentTeamForge.Business/Features/Jobs/ListJobs.cs
- src/AgentTeamForge.DAL/Features/Sessions/LeadSessionStore.cs
- src/AgentTeamForge.DAL/Migrations/Schema.cs
- src/AgentTeamForge.Host/Features/Jobs/JobsEndpoint.cs
- src/AgentTeamForge.Host/Features/Jobs/JobsMcpBridge.cs
- src/AgentTeamForge.Host/Features/WebConsole/WebConsoleServer.cs
- src/AgentTeamForge.Host/Hosting/DaemonCommand.cs
- src/AgentTeamForge.Host/Transport/IpcMessages.cs
- tests/AgentTeamForge.Tests/Features/Usage/SessionTokenUsageTests.cs
- tests/AgentTeamForge.Tests/Features/WebConsole/WebConsoleServerTests.cs

## Validation

SDK: `C:\Projekt\git\agent-team-forge\.tools\dotnet11\dotnet.exe` (.NET 11 preview).

1. Final solution `dotnet build -c Release -warnaserror`: PASS, 0 warnings, 0 errors.
2. Required filtered test run, Release --no-build: PASS, 92 passed, 0 failed, 0 skipped.
   - Features.Usage: 7 passed.
   - Features.Sessions: 6 passed.
   - Features.Jobs.ListJobs: 18 passed.
   - WebConsole: 61 passed (includes 1 new HTTP null-usage test and usage-flag assertions in an existing route test).
   Filter: FullyQualifiedName~Features.Usage|FullyQualifiedName~Features.Sessions|FullyQualifiedName~Features.Jobs.ListJobs|FullyQualifiedName~WebConsole
   TRX: tests/AgentTeamForge.Tests/TestResults/usage-required.trx (ignored build artifact).
3. Additional run including InteractiveTranscriptReaderTests: 143 total, 139 passed, 4 failed, 0 skipped. All required suites passed; 47 reader tests passed. Failures are in unchanged rate-limit timezone logic on Windows:
   - Claude_limit_reset_times_are_resolved_in_their_zone, Europe/Berlin 3pm case.
   - Claude_limit_reset_times_are_resolved_in_their_zone, America/New_York 12:30am case.
   - Claude_limit_reset_times_are_resolved_in_their_zone, Europe/Berlin DST 3am case.
   - Claude_monthly_spend_limit_maps_to_rate_limit_with_utc_reset.
   Actual rate-limit text was "usage limit reached; reset time unknown". The failing tests use the ordinary transcript Read/Parse path, whose rate-limit/timezone code was not changed.
   TRX: tests/AgentTeamForge.Tests/TestResults/usage-backend.trx.
4. Initial diagnostic test run before the final extra tests: 90 total, 88 passed, 2 failed in existing ListJobs fixture disposal (jobs.db still held on Windows): Paging_over_concurrent_changes_never_duplicates_stable_rows and History_filters_backend_and_since_across_pages. Final runs used the repository's existing ATF_KEEP_TMP=1 plus ATF_TEST_TMP_ROOT set to the system temp directory to avoid deleting pooled SQLite files during fixture disposal. No existing tests were changed to mask these errors. Temporary fixture directories were retained.
5. `dotnet publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -c Release --no-restore -p:PublishAot=false -warnaserror -o artifacts/usage-backend-publish`: PASS.
6. Published `atf.exe --version`, with DOTNET_ROOT set to the supplied SDK: PASS, exit 0, output `atf 0.0.1-dev`. Earlier `--help` printed usage; the CLI does not implement that switch and returned nonzero. Successful smoke used the supported --version switch.
7. git diff --check: PASS after newline normalization.

## Deviations and integration notes

- This base's latest schema was V28. V29 is the next free version here. feat/named-agent-tabs also introduces V29 (display_name); RENUMBER one migration and update CurrentVersion/the migrations array at merge if both are integrated.
- Read accepts an optional cwd argument beyond the planned three arguments so managed relative config roots resolve against the job's launch directory. Cache keys also include home to isolate different config roots.
- Locator header reads are bounded; a transcript whose identity/ancestry header lies beyond 64 KiB cannot be located and returns unknown. Ordinary interactive Read behavior retains its existing full ancestry scan.
- Pi format/path support remains best effort; Pi is not installed on this machine. No real-agent/browser visual smoke was performed. The existing Chromium team-render scenario returns early on Windows, as already implemented in that test.
- Published smoke was non-AOT. No Linux support or AOT measurement is claimed from this Windows run.
- Opposite-family Claude review was requested from team-lead against the worktree/commit; review and integration remain the parent's responsibility before merge. This worker did not merge or push.
- Out of scope remains sidechain transcripts, per-turn deltas, currency cost and usage beyond the page.

## Review fixes

Follow-up commit: `2286fbaddcd92721b10bef1a0caba5f801bc92cd` — Fix token usage locator and lead accounting review findings.
Branch: `atf/job-job_01a0f278b7047cdf9c0cc0e1118a5c23`. Previous implementation commit remains `11b17bd12d05311bc8fd4303e42529ad05fafb91`. No push; worktree clean. Read the complete Claude review before making these fixes.

### Per-finding resolution

1. HIGH, large first records / native receipt regression: removed the 64 KiB byte-window HeaderLines implementation. HeaderId again streams the first 10 complete records with LiveFiles.ReadLines. Usage ancestry lookup examines at most 50 Claude records or 10 Codex/Pi records, without cutting a record at 64 KiB. Static ReadClaudeSession and ReadCodexThread explicitly select receipt mode; Claude receipt ancestry retains the pre-refactor full-line scan. A new regression test uses a 270,553-character first Claude user prompt and verifies both token totals and completed native receipt. A Codex test uses a 100,000-character session_meta record. This supersedes the earlier report's 64 KiB limit and corrects its incomplete statement about native static readers.

2. HIGH, headless/Desktop Codex: usage mode accepts any string source, including cli, exec and vscode; source objects with subagent remain rejected. Receipt mode retains cli-only ancestry. Git blame traces that restriction to `495162c896a9910d940268733621ffc86adcf5f5` (Bind retained interactive turns to verified native transcripts), which introduced verified TUI ownership. The native-receipt path delivers into owned interactive CLI sessions, so this restriction was kept rather than broadened accidentally. The new large-header theory covers cli/exec/vscode usage, checks successful cli receipt completion, and confirms exec/vscode are not accepted as interactive receipts. A separate test verifies positive subagent exclusion.

3. MEDIUM, managed child also acting as lead: chose the smaller null-usage option. NativeBindings excludes lead_sessions whose current binding_key matches `managed-child:%`. JobsEndpoint already initializes every lead on the page to null, so managed-child lead entries stay null and are not added as separate lead usage. Their native usage remains available on the managed job's session_tokens. The endpoint test now includes a nested managed-child lead with the same native Claude session as another binding and asserts its usage is null.

API shape is UNCHANGED:
`lead_tokens: { "<lead_session_id>": { "input": number, "output": number, "cache_read": number, "cache_write": number, "total": number } | null }`.
Every lead represented by page jobs gets an entry; managed-child leads always get null. No native_session_id wrapper or parallel map was added. Existing app.js null handling works as-is, so wwwroot was not modified. A nested lead's own usage is represented through its managed job when that job is on the page, rather than through the nested lead card/group's own lead contribution.

4. LOW, deleted transcript: FileNotFoundException or DirectoryNotFoundException clears Path, Offset, Usage, Seen, SkippingLine and RetryAfter. The current read returns null. The next read can locate a replacement/relocated transcript; if still missing, the normal 30-second negative cache applies. Other transient IO failures retain the prior cached usage. Updated the shrinking/deletion test to expect null after deletion and added a relocation test proving reset Claude dedupe/offsets when the same session appears at a new path.

5. LOW, competing environment IDs: bridge capture selects the native kind using HostSessionWake.CurrentHost(). A Codex host selects CODEX_THREAD_ID/CODEX_HOME, a Claude host selects CLAUDE_CODE_SESSION_ID/CLAUDE_CONFIG_DIR. With no identifiable host, existing Claude-first fallback remains. A recognized host with no matching ID reports unknown rather than adopting an inherited ID from the other host. A small internal selector has focused tests for both hosts and fallback when both IDs exist.

Also corrected this private branch's SchemaTests downgrade fixtures to remove its V29 native columns before replaying old migrations, and made the version assertion use Schema.CurrentVersion. No production migration numbering changed in this follow-up. The parent integration already renumbered native columns to V30 with display_name in V29; when cherry-picking, preserve those integrated V30/V29 downgrade steps instead of replacing them with this private branch's V29-only native downgrade.

### Build, tests and main comparison

- Release solution build, `dotnet build -c Release -warnaserror`: PASS, 0 warnings, 0 errors.
- Full requested coverage plus ListJobs and other Dispatch-matching tests: 226 total, 222 passed, 4 failed, 0 skipped. Final failures are exactly the known Windows timezone failures: Claude_limit_reset_times_are_resolved_in_their_zone (three cases) and Claude_monthly_spend_limit_maps_to_rate_limit_with_utc_reset.
- Suite counts in that run: Usage 16/16; SchemaTests 7/7; Sessions 6/6; InteractiveTranscriptReader 47/51 (four known timezone failures); DispatchJob 16/16; DispatchFault 8/8; DispatchConcurrency 4/4; NativeClaudeDelivery 5/5; NativeCodexDelivery 10/10; WebConsole 61/61; ListJobs 18/18. Another 24 tests whose names match the broader Dispatch filter also passed.
- Built and tested an isolated, unchanged detached main checkout at `6d496da9c839fe95423a407b3ead7353dca33180`, under artifacts/review-main. Same test filter: 212 total, 208 passed, 4 failed, 0 skipped. Compared the failed test-name sets from the two TRX files: identical; no new final failures. Main's Release -warnaserror build also passed cleanly.
- Both branch and main test runs used ATF_KEEP_TMP=1 and ATF_TEST_TMP_ROOT set to the system temp directory, matching the earlier documented workaround for Windows pooled-SQLite fixture disposal. The known ListJobs disposal failures therefore did not occur in either comparison run.
- Two initial parallel branch runs had 221 passed / 5 failed out of 226: the four timezone failures plus Discovery_checks_beyond_200_decoys_and_reports_true_ambiguity. That existing test creates 205 files then supplies a current timestamp, excluding its original file when those writes take over two seconds under load. It passed 1/1 in isolation. The final broad branch run used an ignored, temporary assembly xunit.runner.json with parallelizeTestCollections=false and maxParallelThreads=1; all discovery assertions passed. The runner configuration was removed afterwards. No discovery test assertions or runtime recency logic were changed.
- Final branch TRX: tests/AgentTeamForge.Tests/TestResults/review-fixes-serial.trx. Main TRX: artifacts/review-main/tests/AgentTeamForge.Tests/TestResults/review-main.trx. Initial parallel and isolated retry TRX files are also retained as ignored test artifacts.
- Filter used: FullyQualifiedName~Features.Usage|FullyQualifiedName~SchemaTests|FullyQualifiedName~Features.Sessions|FullyQualifiedName~InteractiveTranscriptReader|FullyQualifiedName~Dispatch|FullyQualifiedName~NativeClaude|FullyQualifiedName~NativeCodex|FullyQualifiedName~WebConsole|FullyQualifiedName~ListJobsTests.
- Release publish with PublishAot=false and -warnaserror: PASS. Ran the freshly published atf.exe --version with DOTNET_ROOT pointing to the supplied SDK: PASS, output atf 0.0.1-dev.
- git diff --check: PASS. No Linux or AOT validation claimed; Windows-only guards in existing tests remain as implemented.
