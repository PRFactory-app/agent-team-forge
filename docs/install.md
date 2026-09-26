# Install and startup

After a release is published, install the latest Linux x64 release with:

```sh
curl -fsSL https://github.com/PRFactory-app/agent-team-forge/releases/latest/download/install.sh | sh
"$HOME/.local/bin/atf" setup
```

To pin a release:

```sh
curl -fsSLO https://github.com/PRFactory-app/agent-team-forge/releases/download/v0.0.1/install.sh
sh install.sh --version 0.0.1
```

The installer downloads the archive and its checksum from the same tag. This
detects transfer corruption; it is not an independent publisher signature, and
the downloaded installer is trusted before archive verification.

Setup registers installed clients and asks for a launch mode on first use.
Use `--mode headless` for unattended setup or `--mode herdr` for interactive
Herdr agents. The daemon starts on first agent use or CLI client call.

Login autostart is optional and off by default. Run `atf setup --autostart --apply`
to enable it, `atf setup --autostart=off --apply` to remove it, and
`atf doctor` to see its status. Linux uses a systemd user unit; macOS uses a
LaunchAgent. When the Linux unit is installed, lazy start uses `systemctl --user
start agentteamforge.service`; otherwise it uses `setsid`. A unit-started daemon gets systemd's user environment,
not your shell's; put provider keys or config variables agents need in
`~/.config/environment.d/`. Windows uses the current user's Run entry through a hidden
`wscript.exe` launcher in the state directory. A lazy Windows start detaches
without inheriting the bridge's handles and writes to `daemon.log`. macOS and
Windows startup still need validation on those machines. `atf start` and `atf stop`
are available for manual control.

## Upgrade and uninstall

Rerun the installer to upgrade. Rerunning the active version verifies its installed
files and makes no changes. An upgrade stops the running daemon, so wait for jobs
to finish first. It preserves the profile, launch mode, state, and stable MCP
executable path; reload agent clients after an upgrade. If setup used a custom
`--state-dir`, pass the same directory to the installer.

Run `"$HOME/.local/bin/atf" uninstall` to remove the binaries, ATF-owned client
registrations, Pi wake entry, and login autostart. Shared Pi adapter packages,
unrelated client settings, and state remain. To remove the selected state as well,
use `uninstall --purge`; use `--state-dir DIR` for a custom state directory.
