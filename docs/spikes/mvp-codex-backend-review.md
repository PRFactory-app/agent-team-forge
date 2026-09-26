# MVP Codex exec backend review

Verdict: **approved with one small fix**. Reviewed `d99e87c` against `d7d24ae` and the read-only win-agent-teams Codex backend. The new and resume argument forms match the installed Codex CLI. Prompt delivery uses stdin, JSONL events produce the expected evidence, and cancellation targets the owned process tree.

## Fix

- An oversized JSONL `agent_message` was silently discarded. A later `turn.completed` could then mark the job successful with an empty or stale result. Oversized lines now produce `backend_line_too_long` and terminate the owned child. A focused regression test covers this case.

## Validation

- `.tools/dotnet11/dotnet build AgentTeamForge.slnx`: passed, 0 warnings/errors.
- `.tools/dotnet11/dotnet test AgentTeamForge.slnx --no-build`: 83 passed, 1 opt-in test skipped.
- `ATF_REAL_CODEX=1` real Codex new-session/resume test: passed (1 test, 9 seconds) before the cap-only fix.
- Runtime environment: Linux x64, .NET SDK 11.0.100-rc.1.26425.128, Codex CLI 0.157.1.

The dispatcher currently ignores `Session` evidence, so native session IDs are exposed by this backend but not yet persisted by job dispatch. That integration is outside this backend review.
