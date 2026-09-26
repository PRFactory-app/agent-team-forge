# Simple installation and packaging

Target, not shipping instructions yet. Linux x64 first. Copy the reference
repository's `README.md` and `INSTALL.md`: install before registering, use an
absolute executable path, prefer user-scope client registrations, then restart
clients. No dedicated setup skill exists in the inspected reference checkout;
its `.claude/skills/agent-orchestration/SKILL.md` covers orchestration instead.

## Target `install.md`

Prerequisites: supported glibc Linux, `curl`, `tar`, `sha256sum`, `setsid`, and at
least one installed, authenticated agent CLI. Pi needs its normal Node runtime;
ATF needs neither a .NET runtime nor a source checkout. List measured native
library requirements from the release build, not “any Linux.” Choose headless
below; replace `headless` with `herdr` for interactive terminals after installing
Herdr. Never silently switch modes.

```sh
curl -fsSL https://github.com/PRFactory-app/agent-team-forge/releases/latest/download/install.sh | sh
"$HOME/.local/bin/atf" setup --mode headless --apply
"$HOME/.local/bin/atf" start
```

The guide explains that the first command executes downloaded code and offers
download/inspect/run or manual archive extraction as alternatives. Full paths
make these steps work before `~/.local/bin` is on PATH; print PATH advice rather
than modifying shell startup files. Restart agent clients; verify with Claude's
`/mcp`, Codex's MCP listing, or a Pi tool call. Show daemon log location on failure.

## Release and filesystem

- Tag-triggered GitHub Actions use the pinned .NET 11 SDK and existing
  `scripts/verify.sh` (format, build, tests, AOT publish, published smoke).
  Build on a documented glibc baseline; publish `atf-VERSION-linux-x64.tar.gz`,
  SHA-256 checksums, and `install.sh`. Bundle `atf`, Pi wake sources, and licenses;
  keep debug symbols separate. Smoke-test the extracted archive as well.
- A small POSIX installer is worth it: detect platform, reject unsupported ones,
  resolve one release tag, download matching archive/checksum, verify, stage,
  then switch installation atomically. No sudo, SDK install, service manager,
  or package-manager packages initially. Support `--version VERSION`.
- Store payloads under `~/.local/share/agentteamforge/releases/VERSION`, with a
  `current` symlink and stable `~/.local/bin/atf` entry. Registrations use that
  stable absolute path, not a temporary extraction or version-specific path.
- Keep existing state at `${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge`:
  SQLite, credentials, logs, socket, `profile.json`, and `launch-mode.json`.
  Preserve `--state-dir`; no new ATF config tree or migration just for packaging.
- Build a `win-x64` Native AOT zip and checksum on the Windows CI runner for the
  first release; validate it on the owner's Windows VM before claiming support.
  PowerShell installation and `osx-arm64` remain later work.

## Client setup, upgrade, removal

Extend existing `setup --apply`, not a second configurator. Detect installed
clients; missing optional CLIs do not break setup. Reconcile only ATF entries
using `claude mcp add --scope user` and `codex mcp add`; repair stale registrations
instead of skipping any existing name. Preserve unrelated settings and enable
Claude `crossSessionInbound` as today.

For Pi, reuse `pi install npm:pi-mcp-adapter`, merge an `agentteamforge` entry into
`~/.pi/agent/mcp.json`, and register the bundled wake extension through Pi's
supported local-package installation. Resolve its state directory without
requiring users to export `ATF_STATE_DIR` in every shell: persist setup's chosen
path in an ATF-owned extension setting, with environment override retained.
The extension must load in the Pi host, not merely the MCP child.

Upgrade: finish active work, `atf stop`, rerun the version-selectable installer,
rerun setup with the saved mode, then `atf start`; restart clients too. Never
replace a running daemon or reset state. Retain the previous payload; database
backup is required before schema-changing upgrades, and binary downgrade alone
is not database rollback.

Add `atf uninstall`: stop safely, remove only unchanged ATF-owned registrations,
extension registration, and installation files. Preserve state by default;
state deletion is a separate explicit action. Record prior setup values so
shared Claude settings are restored only if still unchanged. Keep shared Pi
adapter and other client settings.

## Implementation slices

1. **Release archive:** Linux x64 tag workflow, bundled extension/licenses,
   checksums, documented native dependencies, extracted-binary smoke.
2. **Installer:** version selection, checksum verification, staged replacement,
   stable paths, unsupported-platform and fresh-home tests.
3. **Client setup:** optional-client detection, stable-path reconciliation,
   preserved settings, Pi adapter/config/wake installation and state discovery.
4. **Lifecycle:** safe upgrade instructions and ownership-aware uninstall;
   tests prove user settings and state survive.
5. **User guide:** root `install.md`, README link, three-command fresh-install
   exercise, upgrade/removal exercise, brief troubleshooting; no new framework.
