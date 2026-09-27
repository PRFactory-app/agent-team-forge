# 0006. Agents run with permissions bypassed

Status: accepted

## Context

Agent teams run unattended. An approval prompt in a worker blocks the job
until a human finds it. The reference tool runs workers with permissions
bypassed and this works in practice. The original plan included relaying
approval requests to humans through the daemon.

## Decision

- Managed agents launch with their backend's bypass flags
  (Claude `--dangerously-skip-permissions`, Codex
  `--dangerously-bypass-approvals-and-sandbox`, Pi `--approve`).
- Interactive Codex jobs trust their checkout for that invocation only; the
  daemon does not write trust into `config.toml`.
- There is no approval relay, approval queue or human-approval ceremony in
  ATF. Human confirmation is reserved for destructive operator actions (for
  example stopping an agent from the web console).

## Consequences

- Agents can do anything their OS user can do in their working directory. Use
  per-job worktrees (`worktree=true`) to keep parallel changes apart.
- The operator is responsible for which repositories and prompts are given to
  agents.
- Approval relay is recorded as dropped in [ADR 0012](0012-dropped-scope.md).
