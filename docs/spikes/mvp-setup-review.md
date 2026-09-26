# MVP setup review (e04f1fa)

Reviewer: Claude (opposite family). Verdict: **APPROVED with one fix**.

## Fix
- `atf start` probed the daemon lock with `TryAcquire` in its readiness loop.
  Holding the lock even briefly could make the just-launched daemon lose the
  flock race and exit 75, turning start into a spurious failure. The loop now
  waits until the lock file records the launched PID (setsid + `sh exec` keep
  it) and the socket exists, without touching the lock.

## Checked, OK
- Registration argv goes through `ArgumentList` (no shell); printed commands
  are single-quoted where needed.
- Default state dir: `$XDG_STATE_HOME/agentteamforge`, else
  `~/.local/state/agentteamforge`.
- Published binary: setup (no `--apply`) → start → start (same PID) → stop
  prints `kill -TERM <pid>`; after kill, stop reports not running. Daemon runs
  in its own session with no tty; fds are /dev/null, daemon.log, lock,
  runtime pipes/epoll, socket only.
- Build and 115 tests pass.

## Noted, not fixed
- `--apply` treats an existing `agentteamforge` registration as done even if
  it points at another binary/state dir; rerun needs a manual `mcp remove`.
