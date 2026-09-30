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
