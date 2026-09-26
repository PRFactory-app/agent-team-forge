# D7 — owned Linux Herdr launch (production)

2026-09-26. Claude implementation worker; **not reviewed** — needs opposite-family
(Codex) code review. Branch `feature/d7-herdr-terminal` from `2d6d0c9`, with the
D2 fixture commit `fc5e37f` cherry-picked (still under Codex review).
Linux x64, herdr 0.8.2. Linux-first: no Windows/macOS claim.

## Files

Production (`src/AgentTeamForge.Business/Features/Agents/Terminals/`):

- `HerdrTerminal.cs` — public provider: `StartSessionAsync`, `OpenAgentTabAsync`,
  `VerifyBindingAsync`, `StopOwnedSessionAsync`; options, handle/binding records, two exceptions.
- `HerdrOwnership.cs` — fresh names/labels, absent-name creation rule, fail-closed teardown decision (internal).
- `LaunchEnvironment.cs` — reviewed allowlist and unforwardable session/Herdr context (internal).
- `HerdrCommands.cs` — argv/`ProcessStartInfo` builders, fixed server script (internal).
- `HerdrProcessRunner.cs` — `IHerdrProcessRunner` seam; real bounded capture
  (`8f7b634` `BoundedProcess` logic, async), detached launch, `/proc` identity,
  parent and environment reads (internal).

Tests: `tests/.../Terminals/HerdrTerminalTests.cs` (new);
`tests/.../Support/ManagedDemo/HerdrLaunchFixture.cs` now **delegates** to the
production types, so the unchanged D2 characterization tests pin production code.

## Behaviour

- **Owned session:** fresh `atf-<hex12>` name (prefix configurable, `atf-test-` in
  tests) refused if listed running or stopped; `setsid -f sh -c <fixed script> $0`
  launch; bounded poll until listed running with a rooted `socket_path` **and exactly
  one** `herdr --session NAME server` process; PID + start ticks recorded; workspace
  created with a random owner label.
- **Retained handle:** `OwnedHerdrSession(name, socket, server pid/start, label,
  workspace)`; `HerdrTabBinding(session, tab, pane, terminal_id, shell pid/start)`.
- **Bootstrap proof:** each tab is created with `--env ATF_BOOTSTRAP_FILE=<path>`
  (path only, never content). The binding is returned only when the pane's
  `shell_pid` is a live child of the recorded server and `/proc/<pid>/environ`
  carries exactly that path. Observed on real Herdr: the shell's parent is the
  server and the variable is present.
- **Binding invalid when replaced:** server identity changed, `pane get` fails,
  `terminal_id`/`tab_id` changed, or shell PID/start changed → problem string.
  Server identity is checked before any socket command.
- **No visible provider → no headless fallback:** no `WAYLAND_DISPLAY`/`DISPLAY`
  in the seed, or `herdr --version` unusable → `InteractiveTerminalUnavailableException`;
  no launcher runs. The class has no headless path at all.
- **No foreign cleanup:** every failure (launcher error, server not running,
  ambiguous/foreign server processes, workspace/tab failure, proof failure) throws
  `HerdrLaunchException` without stop/delete/close/kill. Teardown stops and deletes
  only after `HerdrOwnership.Teardown` proves record + running + identity + label.
- **Bounds:** every CLI call has a whole-operation deadline (default 30 s), stdout
  cap 4 MiB (truncation refuses the reply), stderr cap 64 KiB; startup/proof polls
  bounded (default 15 s). Environment built only from the injected seed.

## Tests (red → green)

Tests written first against a throwing stub: 16 failed, 1 skipped (opt-in).
Green after implementation.

| Test | Pins |
| --- | --- |
| `OwnedLaunch_RetainsHandleAndBootstrapProof` | handle/binding facts; D2 vectors through the real launch path: exact argv, fixed server script, sentinel-free env, owned socket only on session commands |
| `SpawnFailure_NoForeignCleanup` ×4 | launcher fails / never running / two server processes / workspace fails → no stop/delete/close/kill |
| `ExistingSession_RefusesReuseWithoutTouchingIt` | listed name → no launcher, no teardown |
| `TabReplaced_BindingInvalid` ×4 | new terminal, pane gone, server restarted, shell replaced |
| `BootstrapNotProven_RefusesBinding` ×2 | foreign parent; wrong bootstrap path; tab not closed |
| `NoVisibleProvider_NoHeadlessFallback` ×2 | no display; herdr unusable → only `--version` probed |
| `Teardown_StopsOnlyProvenOwnedSession` | replaced server refused; proven session stop then delete |
| `Command_OutputAndTimeAreBounded` | flood truncated, pipe-holding descendant cannot extend deadline, truncated reply refused |
| `RealHerdr_OwnedLaunchAndReplacement` | **opt-in** `ATF_HERDR_INTEGRATION=1`; see below |

D2 characterization tests (4) pass unchanged against production via the fixture.

## Real Herdr run (Q2, opt-in)

`ATF_HERDR_INTEGRATION=1 AgentTeamForge.Tests -method "*RealHerdr_OwnedLaunchAndReplacement"`:
passed (0.86 s). It creates its own `atf-test-<hex>` server, opens a tab, verifies
the binding, closes the tab, opens a replacement, asserts the old binding invalid
and the new one valid, then tears down with ownership proof. `herdr session list`
names were identical before and after; no `atf-test-*` server remained. The default
and other sessions were only listed, never targeted. An earlier manual probe
session (`atf-test-`, created by this worker to observe JSON shapes) was stopped and
deleted after verifying its PID and owner label.

## Gates

`DOTNET=.tools/dotnet11/dotnet scripts/verify.sh` (SDK `11.0.100-rc.1.26425.128`):
restore, format `--verify-no-changes`, Release build `-warnaserror`, tests 81 passed /
1 skipped (opt-in) of 82, AOT publish, published smoke 19/19 — all passed.
Limit: Host does not reference `HerdrTerminal` yet, so the AOT binary does not
exercise it; AOT compatibility is only analyzer-checked (`IsAotCompatible`).

## Limits and follow-ups

- **Visibility:** the owned server is detached; a human sees it only after
  `herdr session attach <name>` in a terminal. The display check is a guard, not
  proof of a visible client. Visible association stays gated on Q2/D10.
- **Agent TUI start** in the pane (`herdr agent start` vs `pane run`) is left to
  backend slices (D10/D12/D16); `pane run` types text into the shell and must be
  weighed against the no-keystroke-injection rule.
- **Bootstrap file** creation/nonce exchange is not here (D5/D14 contract). The path
  appears in `herdr` argv (not a secret).
- S1 (replacement between ownership check and stop/delete, or between identity
  check and a socket command) and concurrent-create races remain open; no socket
  listener-inode proof (`SocketOwnership` spike code not promoted).
- Explicit env opt-in still forwards named non-session credentials (as reviewed in D2).
- Durable persistence of the handle/binding (restart rebind) is not in this slice.

## What D0/D5 must adapt

- Map `OwnedHerdrSession`/`HerdrTabBinding` onto D5 `OwnedTerminalBinding` and
  implement `IInteractiveTerminal` over `HerdrTerminal` (or replace these records).
- Replace `HerdrLaunchException`/`InteractiveTerminalUnavailableException` and the
  `string?` verification result with D0's error/result conventions.
- Host composition: construct `HerdrTerminalOptions` from the explicit launch-mode
  setup and the daemon's production-context environment seed.
