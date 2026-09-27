# Architecture decision records

Each record states the context, the decision and its consequences. Records are
short; git history keeps the research and reviews behind them. Change a
decision by adding a new record that supersedes the old one.

| ADR | Decision |
| --- | --- |
| [0001](0001-dotnet-11-native-aot.md) | .NET 11 with Native AOT, measured rather than assumed |
| [0002](0002-three-projects-vertical-slices.md) | Three projects, Host → Business → DAL, organized by feature |
| [0003](0003-daemon-owns-jobs-sqlite.md) | A separate daemon owns accepted jobs; SQLite holds state |
| [0004](0004-explicit-launch-mode.md) | Launch mode is an explicit setup choice; no silent headless fallback |
| [0005](0005-native-wake.md) | Native session wake: commit first, then a notice-only wake |
| [0006](0006-bypass-permissions.md) | Agents run with permissions bypassed; no approval relay |
| [0007](0007-single-team-reference-parity.md) | One team per lead; parity with win-agent-teams |
| [0008](0008-never-replay-uncertain-prompts.md) | Never replay an uncertain prompt; use `needs_reconciliation` |
| [0009](0009-restart-quarantines-live-tuis.md) | Daemon restart quarantines live TUIs instead of killing them |
| [0010](0010-minimal-loopback-web-console.md) | Minimal text web console on authenticated loopback |
| [0011](0011-prfactory-outbound-polling.md) | PRFactory connector: opt-in, outbound HTTPS polling |
| [0012](0012-dropped-scope.md) | Scope dropped from the original full-product plan |
