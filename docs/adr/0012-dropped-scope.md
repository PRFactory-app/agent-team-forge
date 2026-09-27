# 0012. Scope dropped from the original full-product plan

Status: accepted

## Context

The original full-product plan (features F01–F27, phases P01–P08) was written
before any code and was never a gate. Several of its items were invented
during planning rather than requested by the owner. Leaving them in planning
documents made them look like open debt.

## Decision

The following are **dropped**. They are not planned, and their absence is not
a defect. Reopening one needs a new ADR.

| Item | Original ref | Why dropped |
| --- | --- | --- |
| Multiple teams, nested delegation with budgets | F10 | One team per lead is enough ([ADR 0007](0007-single-team-reference-parity.md)). |
| Spending caps and usage reporting | F13 | Backends report their own usage; ATF does not meter models. |
| Approval requests relayed to humans | F05/F13 | Agents run with permissions bypassed ([ADR 0006](0006-bypass-permissions.md)). |
| Restore command and migration rollback | F18/F22 | Startup backups exist; restore is a manual file copy. |
| Threat model / security qualification document | F23 | Security rules live in the code and in the docs of each surface. |
| Diagnostic export bundle | F14/F15 | `atf doctor`, `daemon.log` and per-job logs are enough. |
| Performance evidence and runbooks | F26 | Measure when a real problem appears. |
| Attaching to arbitrary already-running Desktop sessions | — | Only ATF-launched agents and explicit external members (join ticket) are supported. |

**Open owner decision (not dropped):** license. The repository has no
`LICENSE` file yet, and release bundles copy one only if present. Third-party
notices follow once the license is chosen.

## Consequences

- The remaining open work is tracked as issues, not planning documents:
  Windows re-validation, Claude wake on Windows/macOS, Claude Desktop member
  wake, `install.ps1`, and the win-agent-teams parity gaps listed in the
  [migration guide](../migrating-from-win-agent-teams.md).
