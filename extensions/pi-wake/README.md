# AgentTeamForge Pi wake extension

This package is adapted from the owner's `win-agent-teams-wake` extension. See
[PROVENANCE.md](PROVENANCE.md). Load it in the Pi host process with `pi -e
/path/to/agent-team-forge/extensions/pi-wake`, and set `ATF_STATE_DIR` to the
same private state directory used by the daemon and `atf mcp`.

The MCP bridge identifies the nearest Pi host PID. After a job finishes, the
daemon appends a notice-only JSONL line to `pi-wake-<PID>.jsonl` in that state
directory. This extension reads its own PID's file and uses Pi's `sendMessage`
with `triggerTurn: true` and `deliverAs: "steer"`. The lead then calls
`job_list` and `job_get`; the wake carries no job result and is not a delivery
acknowledgement. The extension is a no-op without `ATF_STATE_DIR`.
