# Lead inbox backend — slice 4

Implemented and committed; no push and no wwwroot changes.

Branch: atf/job-job_01a0f23fffb377ae92ff17b1d7dce9dc
Worktree: C:\Users\mikael.liljedahl\.local\state\agentteamforge\worktrees\job_01a0f23fffb377ae92ff17b1d7dce9dc
Commit: 1952f8c4b94a97848500647c64acda34fed8ff0c
Commit message: Add idempotent operator messages to lead inbox
Working tree clean after commit.

## Changes and files

- src/AgentTeamForge.DAL/Features/External/ExternalMemberStore.cs: SendToLead atomically deduplicates through external_delivery_keys and inserts a lead-inbox message. Private InsertLeadMessage is shared with SendFromMember and copies the team's wake_key; sender sequence allocation rolls back on failed insertion.
- src/AgentTeamForge.Business/Features/External/ExternalTeam.cs: SendFromOperator validates text (1–65536 characters) and optional command ID (1–128), calls EnsureMcpTeam, inserts from operator, and returns invalid_session for unavailable leads. CreateTicketForTeam reserves the exact member name operator.
- src/AgentTeamForge.Host/Transport/IpcMessages.cs: ExternalOperatorSend protocol operation.
- src/AgentTeamForge.Host/Features/Jobs/JobsEndpoint.cs: new operation mapping; its session validation runs in ExternalTeam so unknown/closed leads return invalid_session rather than the generic invalid_request guard.
- src/AgentTeamForge.Host/Features/WebConsole/WebConsoleServer.cs: authenticated POST /api/leads/{id}/messages with {text, workspace, idempotency_key}; required bounded key/text, fully qualified bounded workspace, JsonSerializable WebLeadMessageBody.
- tests/AgentTeamForge.Tests/Scenarios/ExternalJoinScenarios.cs: one Windows-compatible HTTP -> real JobsEndpoint -> SQLite -> lead-read scenario. Checks operator sender/text, duplicate retry produces one row, reserved member name, malformed bodies, unknown lead, and closed lead (including retry of a previously accepted key).

Existing inbox read and native wake paths are reused without changes. Opposite-family review remains for the parent/integrator.

## Verification on Windows

SDK: C:\Projekt\git\agent-team-forge\.tools\dotnet11\dotnet.exe (pinned .NET 11).

- Final solution build: dotnet build AgentTeamForge.slnx -c Release --no-restore --nologo -warnaserror — PASS, 0 warnings, 0 errors.
- Selected Windows-compatible Features.External, Features.WebConsole, PRFactory.ExternalJoinTests and new scenario: PASS, 82 passed, 0 failed, 0 skipped. Results: artifacts/test-results/backend-windows-final.trx.
- Separate focused new scenario: PASS, 1 passed, 0 failed, 0 skipped. Results: artifacts/test-results/operator-focused.trx. It uses an in-process endpoint and does not launch the apphost.
- Managed publish: dotnet publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -c Release --no-restore -p:PublishAot=false -o artifacts/backend-publish --nologo — PASS. Published atf.exe --version exited 0, output atf 0.0.1-dev. Native AOT was not attempted.
- dotnet format whitespace on the changed parser/scenario completed successfully; git diff --check passed.

The regression test run sets DOTNET_ROOT to the local SDK and ATF_HOST_BINARY to the built Windows atf.exe for process scenarios.

Final regression filter:
(FullyQualifiedName~Features.External.|FullyQualifiedName~Features.WebConsole.|FullyQualifiedName~Web_operator_message|FullyQualifiedName~PRFactory.ExternalJoinTests)&FullyQualifiedName!~Two_lead_sessions_render&FullyQualifiedName!~Web_command_uses_default_state&FullyQualifiedName!~External_member_ticket_prompt_reply_restart_and_kill_are_durable&FullyQualifiedName!~Expired_unjoined_ticket_is_reissued

## Linux/Unix exclusions and unrelated failures

Linux-only ExternalJoinScenarios not run:
- Ticket_and_join_responses_explain_qualified_external_tool_routing
- External_read_pages_large_messages_without_losing_unread_work
- Separate_member_bridge_joins_and_exchanges_messages_with_lead

WebConsoleScenarios.Two_lead_sessions_render_as_live_teams_in_an_isolated_browser requires Linux + Chromium. Web_command_uses_default_state_directory_for_all_forms requires Unix (explicitly returns on Windows). Neither was counted as a passing Windows test.

The initial broader substring filter selected 92 tests: 88 passed, 4 failed, 0 skipped. One failure was the new scenario finding the generic IPC guard's wrong error code; fixed and verified by both final runs. Three failures were unrelated Windows limitations and were not changed:
- PRFactory.ExternalJoinTests.External_member_ticket_prompt_reply_restart_and_kill_are_durable: state_dir_acl_failed from WindowsPrivatePaths.ValidateDirectory.
- PRFactory.ExternalJoinTests.Expired_unjoined_ticket_is_reissued_and_the_old_one_stops_working: same state_dir_acl_failed.
- PRFactory.PublicationChainTests.External_members_leaving_cannot_publish_while_the_managed_lead_is_still_running: UnauthorizedAccessException during TempStateDir cleanup. This unrelated test was selected accidentally by the initial broad External substring filter.

Initial evidence: artifacts/test-results/backend-windows.trx. No claim of full-suite or Linux validation.