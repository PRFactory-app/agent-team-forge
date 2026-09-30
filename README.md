# AgentTeamForge

**Let your lead coding agent hand work to a team of Claude Code, Codex, Pi,
Cursor and Droid agents — and keep that work alive when things crash.**

AgentTeamForge (`atf`) is a small local daemon. Your lead agent gives it jobs
over MCP; it runs the agents, remembers every job, and tells the lead when
results are ready. Close the lead, lose the terminal or restart your
session — the team keeps working. Watch the agents live in Herdr or terminal
tabs, or in a small web console.

**Free forever.** Need tickets, shared workflows and teamwork? [PRFactory](https://app.prfactory.dev) is the optional paid companion.

![AgentTeamForge web console showing one lead session and its agents](docs/images/web-console.png)

## Quickstart

**1. Install** the [latest release](https://github.com/PRFactory-app/agent-team-forge/releases):

```sh
curl -fsSL https://github.com/PRFactory-app/agent-team-forge/releases/latest/download/install.sh | sh
```

Windows PowerShell 5.1 or newer (Windows x64 tester build):

```powershell
irm https://github.com/PRFactory-app/agent-team-forge/releases/latest/download/install.ps1 | iex
```

**2. Set up.** Choose visible agents (Herdr) or background agents (headless):

```sh
~/.local/bin/atf setup --mode herdr      # or: --mode headless
```

On Windows, open a new PowerShell window and run `atf setup --mode wt`.

This connects `atf` to the Claude Code, Codex and Pi clients you have installed.
Reload them afterwards. `atf doctor` checks everything.

**3. Hand out work.** In your lead session, just ask — for example
*"use agentteamforge to have Codex review this branch while Claude writes the
tests"* — or call the tools directly:

```text
submit_job(backend="codex", instruction="Review this branch for bugs", idempotency_key="review-1")
```

The lead is notified when the job finishes. Run `atf web --open` to watch the
team. More in the [usage guide](docs/usage.md).

## How it works

- Your lead talks to `atf` through MCP; one daemon per user runs all jobs.
- Every job is saved to a local SQLite database before it starts, so a crashed
  lead or bridge never loses it.
- Agents run as real CLI sessions: interactive in Herdr or terminal tabs, or
  headless in the background — your choice at setup, never switched silently.
- When a job finishes, the result is stored first, then the lead gets a short
  wake-up notice through its own session.
- Follow-ups continue the same agent conversation; you can interrupt or stop
  any job.
- Everything runs locally; there is no ATF cloud service, and any external
  connection is opt-in.

## Features

- Claude Code, Codex and Pi as team members (interactive or headless), plus
  Cursor CLI and Factory Droid (headless), with model and effort choices
- Parallel jobs, optional git worktree per job, timeouts
- Visible, interactive agents you can type into — or headless
- Web console on localhost: live activity, follow-ups, stop, new agents, model tiers
- Claude Desktop or Codex Desktop can join a team with a one-time ticket
- Checksum-verified install, upgrade and uninstall; optional start at login

## Platform status

- **Linux x64:** tested end to end with real agents.
- **Windows x64:** being tested (Windows Terminal tabs).
- **macOS arm64:** release built, untested — testers welcome.

## PRFactory: tickets, workflows and teamwork

AgentTeamForge is free forever and works fully on its own.
[PRFactory](https://app.prfactory.dev) is the optional paid companion that adds:

- **Ticket management:** tickets your agent team picks up, with results posted
  back to the ticket.
- **Prompt templates** for workflows.
- **Portable agents:** move your agent team and its work between computers.
- **Collaboration** with other team members on projects.

The connector is opt-in and off until you run `atf prfactory connect`.

## Learn more

- [Usage guide](docs/usage.md) and [install, upgrade, uninstall](docs/install.md)
- [Launch modes](docs/terminal-modes.md), [web console and model tiers](docs/web-console.md)
- [Architecture](docs/architecture.md) and [platform status](docs/platform-status.md)
- [Contributing](CONTRIBUTING.md) — building from source, tests and how changes are made
