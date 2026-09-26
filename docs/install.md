# Install and startup

On Linux, install the release bundle with `install.sh`, then run
`atf setup --mode headless --apply` once. Use `--mode herdr` for interactive Herdr agents.
The daemon starts on first agent use or CLI client call.

Login autostart is optional and off by default. Run `atf setup --autostart --apply`
to enable it, `atf setup --autostart=off --apply` to remove it, and
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
