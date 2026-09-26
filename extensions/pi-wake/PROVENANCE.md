# Pi wake extension provenance

Copied from `agentic-coder-teams-mcp/pi-extensions/win-agent-teams-wake` at
commit `c2868be1ca5c84bf6dc0b0b222be7d935671e7ef` (owner repository,
read-only). The original lifecycle, state-machine, generation, and test files
are retained. `index.ts` adapts the injection path to the AgentTeamForge
daemon's committed JSONL doorbells. Set `ATF_STATE_DIR` in the Pi host to the
same private state directory used by `atf mcp`; the bridge identifies the
nearest Pi host PID and the extension reads only that PID's spool.
