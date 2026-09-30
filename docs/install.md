# Install and startup

After a release is published, install the latest Linux x64 release (tested glibc
distributions) with:

```sh
curl -fsSL https://github.com/PRFactory-app/agent-team-forge/releases/latest/download/install.sh | sh
"$HOME/.local/bin/atf" setup
```

To pin a release:

```sh
curl -fsSLO https://github.com/PRFactory-app/agent-team-forge/releases/download/v0.0.1/install.sh
sh install.sh --version 0.0.1
```

For the Windows x64 tester build, run this in Windows PowerShell 5.1 or newer
without administrator rights:

```powershell
irm https://github.com/PRFactory-app/agent-team-forge/releases/latest/download/install.ps1 | iex
atf setup --mode wt
```

If `atf` is not found, open a new terminal after installation. The Windows
installer adds `%USERPROFILE%\.local\share\agentteamforge\bin` to your user
PATH. It keeps state in `%USERPROFILE%\.local\state\agentteamforge`. To pin a
release, download its `install.ps1` asset and run
`powershell -ExecutionPolicy Bypass -File .\install.ps1 -Version 0.0.1`.
Rerun the one-liner to upgrade after active jobs finish.
Windows arm64 is recognized by the installer but requires a matching
`win-arm64` release archive; none is published by the current workflow.

The installer downloads the archive and its checksum from the same tag. This
detects transfer corruption; it is not an independent publisher signature, and
the downloaded installer is trusted before archive verification.

Install and log in to whichever backend CLIs you use: Claude Code, Codex, or Pi.
On Linux, the first interactive setup asks you to choose Herdr for visible agent
windows or headless for background agents. If Herdr cannot run, setup explains
why and recommends headless; it never switches modes silently. For unattended
setup, pass `--mode headless` or `--mode herdr` explicitly. Setup registers each
installed client, including Pi's MCP adapter and wake extension (Pi adapter
installation needs network). On all platforms, including Windows, it sets
Claude's `crossSessionInbound` to `accept`
while preserving other settings. A failed client registration is reported with the
manual command; setup still completes (`atf setup --check` verifies). Reload
installed clients afterward. Run
`"$HOME/.local/bin/atf" doctor` to check the result. The daemon starts on first
use; `atf start` is optional.

The Unix installer does not edit your shell startup files. The absolute command above
works even when `~/.local/bin` is absent from PATH. To use `atf` directly, add
`export PATH="$HOME/.local/bin:$PATH"` to `~/.bashrc` (Bash) or `~/.zshrc`
(Zsh), then open a new shell.
If you intentionally test a temporary binary or state directory, setup requires
`--force` before writing global MCP registrations. For isolated daemon testing,
use `atf start --state-dir DIR` or `atf mcp --state-dir DIR` instead.

Linux arm64 and musl are not supported. macOS arm64 and Windows x64 remain
tester-only until validated on those machines. See [usage](usage.md)
for use, and `"$HOME/.local/bin/atf" uninstall` for removal. See
[Upgrade and uninstall](#upgrade-and-uninstall).

Login autostart is optional and off by default. Run `atf setup --autostart`
to enable it, `atf setup --autostart=off` to remove it, and
`atf doctor` to see its status. Linux uses a systemd user unit; macOS uses a
LaunchAgent. When the Linux unit is installed, lazy start uses `systemctl --user
start agentteamforge.service`; otherwise it uses `setsid`. A unit-started daemon gets systemd's user environment,
not your shell's; put provider keys or config variables agents need in
`~/.config/environment.d/`. Windows uses the current user's Run entry through a hidden
`wscript.exe` launcher in the state directory. The default state directory is
`~/.local/state/agentteamforge` on every OS (`%USERPROFILE%\.local\state\agentteamforge`
on Windows); it is deliberately not under `%LOCALAPPDATA%`, which Windows Terminal
tabs see through MSIX virtualization and cannot read. A lazy Windows start detaches
without inheriting the bridge's handles and writes to `daemon.log`. macOS and
Windows startup still need validation on those machines. `atf start` and `atf stop`
are available for manual control.

## Upgrade and uninstall

Rerun the installer to upgrade. Rerunning the active version verifies its installed
files and makes no changes. An upgrade stops the running daemon, so wait for jobs
to finish first. It preserves the profile, launch mode, state, and stable MCP
executable path; reload agent clients after an upgrade. If setup used a custom
`--state-dir`, pass the same directory to the installer. Back up the state
directory before an upgrade that changes the database schema: switching back
to an older binary does not roll back a migrated database. ATF has no restore
command; restore from a backup is a manual operation with the daemon stopped.

Run `"$HOME/.local/bin/atf" uninstall` (or `atf uninstall` on Windows) to remove
the binaries, ATF-owned client registrations, Pi wake entry, and login
autostart. Shared Pi adapter packages, unrelated client settings, and state
remain. To remove the selected state as well, use `uninstall --purge`; use
`--state-dir DIR` for a custom state directory.
