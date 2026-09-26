# AgentTeamForge Pi wake extension

This package is adapted from the owner's `win-agent-teams-wake` extension. See
[PROVENANCE.md](PROVENANCE.md). `atf setup --apply` installs this package in the
Pi host and writes `~/.pi/agent/agentteamforge.json` with the chosen state
directory. `ATF_STATE_DIR` overrides that setting when present.

The MCP bridge identifies the nearest Pi host PID. After a job finishes, the
daemon appends a notice-only JSONL line to `pi-wake-<PID>.jsonl` in that state
directory. This extension reads its own PID's file and uses Pi's `sendMessage`
with `triggerTurn: true` and `deliverAs: "steer"`. The lead then calls
`job_list` and `job_get`; the wake carries no job result and is not a delivery
acknowledgement. The extension is a no-op until a state directory is configured.
