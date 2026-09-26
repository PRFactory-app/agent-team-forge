# Managed-child routing — slice 2

Implemented against `cd5a2d2` on `feat/child-routing`.

- Dispatch creates a private `managed-children/<root-job>/context.json`, using
  the job's existing lead-session relation and external ticket/join mechanism.
  Follow-ups reuse that membership and the independent nested-lead binding.
- Per-launch MCP configuration pins the published executable and state directory.
  Claude/Codex use the registered `agentteamforge` server; Pi uses its adapter's
  `agentteamforge_` proxy names. Pi's exclusive config mode prevents project
  configuration from overriding the injected endpoint (see the
  [adapter configuration loader](https://github.com/nicobailon/pi-mcp-adapter/blob/main/config.ts)).
- Only `send_message(to="team-lead")` uses parent membership. Other names remain
  scoped to the child's own lead session and unknown recipients still fail.
  `read_messages` reads that nested lead's child reports. Managed downstream
  work still uses `follow_up`, with explicit interrupt for busy turns.
- Headless launches clear inherited messaging/native identity. Linux Herdr
  launch arguments use the same private configuration; retention and transcript
  readers are unchanged. Windows/macOS interactive launch integration is not
  claimed by this slice.

Validation: `scripts/verify.sh` with the requested .NET 11 SDK; focused spawn,
resume, credential revocation, private-file and inherited-identity checks;
published-process scenario covering two independent nested trees, parent reports,
unknown recipients, and resumed binding. One verification attempt hit the existing
`Interrupt_racing_completion_always_accepts_the_follow_up` race (null claim before
launch); its focused rerun passed. No change was made to that test or lifecycle.

The live Linux check uses `LiveManagedChildRouting` with a fresh HOME/state,
headless published ATF and the installed Claude 2.1.283 executable directly.
Claude loaded the injected qualified ATF tools, then returned
`Not logged in · Please run /login`; no parent report was delivered. Live delivery
is **not verified** without an approved isolated credential. The check is opt-in
via `ATF_LIVE_CHILD_CLAUDE=1`, `ATF_HOST_BINARY`, and `ATF_LIVE_CLAUDE_BIN` (the
directory containing the real Claude executable); credentials can be supplied
through the standard Claude authentication environment. It does not copy or use
the owner's client configuration directories.
