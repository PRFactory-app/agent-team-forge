# 0004. Launch mode is an explicit setup choice

Status: accepted

## Context

The owner wants to watch and type into agents, not only receive their
results. A log tail in a terminal is not an agent a human can use. Silently
running headless when a terminal provider fails hides the problem.

## Decision

- `atf setup` requires an explicit launch mode: `herdr` (Linux), `wt`
  (Windows Terminal), `terminal` (macOS Terminal.app or kitty) or `headless`.
  Non-interactive setup must pass `--mode`.
- Interactive means the backend's real TUI in a visible tab or pane, still
  controlled by the daemon (launch, follow-up, result, interrupt, stop).
- If the selected provider is missing or fails, setup or the job fails with a
  clear error. There is no fallback to headless or another terminal.
- The chosen mode applies to new agents. Existing agents keep their binding.

## Consequences

- Each platform needs its own terminal adapter and its own validation; Linux
  results do not prove Windows or macOS (see
  [platform status](../platform-status.md)).
- Interactive completion needs an authoritative signal from the backend's
  native session transcript, correlated to the run. Terminal text or an idle
  pane is not evidence of completion.
- Details: [launch modes](../terminal-modes.md).
