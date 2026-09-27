# 0007. One team per lead; parity with win-agent-teams

Status: accepted

## Context

The original plan included nested teams, delegated budgets and multi-team
scopes. The owner's working model is simpler: one lead session hands jobs to
workers, and existing skills are written against win-agent-teams.

## Decision

- A lead MCP session is the unit of scope. It sees its own jobs by default;
  `list_jobs(all_workspace=true)` shows other leads in the same folder.
- No team hierarchy, nested delegation or per-team budgets.
- Follow the reference's behavior and names where practical. External-member
  tools (`create_join_ticket`, `join_team`, `external_send`, `external_read`,
  `external_set_wake`, `leave_team`, `send_message`, `read_messages`) keep the
  reference names and arguments.
- Managed-agent tools use job names (`submit_job`, `follow_up`, `stop_job`,
  `get_job`, `list_jobs`) because ATF tracks durable jobs, not named processes.

## Consequences

- Skills written for the reference need small changes for managed agents. The
  [migration guide](../migrating-from-win-agent-teams.md) maps each tool.
- Multiple leads may share a daemon and a folder without seeing each other's
  jobs by default.
