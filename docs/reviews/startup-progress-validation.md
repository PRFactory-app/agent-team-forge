# Startup progress slice validation

Implements downstream-delivery-gap slice 3 on `feat/startup-progress`.

Each run exposes `startup` in get/list (including MCP and console cards):
`phase`, `started_at`, `ready_at`, `submitted_at`, `acknowledged_at`,
`elapsed_seconds`, and an optional hedged `hint`. Null milestones remain absent
from JSON. Submission marks the one delivery attempt, not confirmed receipt;
only correlated native evidence acknowledges it. Elapsed startup time stops at
acknowledgement or terminal failure. Legacy runs retain their existing start/ack
evidence without fabricated milestone timestamps.

Herdr readiness recognizes specific Claude theme/login/fullscreen/trust screens
and Codex sign-in/trust screens before delivery. Successful owned-session cleanup
produces `failed` with `agent_first_run_required`, `agent_login_required`, or
`agent_workspace_trust_required`, without fencing the session. Unknown screens
retain the existing bounded reconciliation behavior. A ready editor's historical
setup text is ignored, except for Claude's explicit logged-out status. Trust
bypass arguments are unchanged. Unsupported backends gain no screen inference.

Validation on Linux with the pinned .NET 11 SDK:

- `scripts/verify.sh` with `DOTNET_PROCESSOR_COUNT=2`: 619 tests passed, seven
  credential/desktop opt-in tests skipped; published AOT scenarios 40/40 passed.
- Focused coverage: delayed readiness, all recognized blocker families, unknown
  screen bounded reconciliation, retained/resumed callback ownership, evidence
  during Start, get/list projection, slow-start hints, stale/late writes and
  schema migration.
- Published AOT binary with an empty HOME, isolated agent config/state, clean
  environment without credentials, real Claude 2.1.283 and Herdr: failed in five to six
  seconds with `agent_first_run_required`; no ready/submitted/acknowledged
  timestamps, `session_fenced=0`, owned Herdr session removed. The following
  fake-backend job completed on the same daemon. The isolated daemon was stopped.

No setup was run against the real HOME. No owner credentials, agent configs,
Herdr sessions or ATF daemon were changed. No push or merge was performed.
