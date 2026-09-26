# Native AOT release bundles

The first release version is `0.0.1` (tag `v0.0.1`). Both jobs derive the
package and `atf --version` value from the tag. The bundles include the native
executable and its published native libraries, so the target machine does not
need a .NET runtime.

`scripts/release-build.sh VERSION [OUTPUT_DIR]` uses the pinned .NET 11 SDK to
publish `linux-x64` Native AOT, creates `atf-VERSION-linux-x64.tar.gz` and
`SHA256SUMS`, and runs the extracted binary through `--version` and the existing
published scenario smoke. The tag workflow runs the repository gates first and
uploads these files plus `install.sh` as a CI artifact. A separate `win-x64`
job on `windows-latest` publishes Native AOT on Windows and uploads
`atf-VERSION-win-x64.zip` with `SHA256SUMS-win-x64` and separate debug symbols.
The workflow does not publish a GitHub release. The owner can publish the
artifacts after review and Windows VM validation.

The workflow builds on Ubuntu 24.04 (glibc 2.39). On the local Linux build,
`ldd atf` reported `libc.so.6`, `libm.so.6`, and the x86-64 glibc loader;
the archive also bundles the published `libe_sqlite3.so`. The CI binary's
dependencies should be checked on its Ubuntu 24.04 runner before publication.

The Windows job checks that the built `atf.exe --version` matches the tag. The
owner's Windows VM remains the runtime validation gate; a CI build alone does
not establish Windows product support. Native AOT needs a Windows build host
for `win-x64` and cannot cross-compile from Linux.

Install from downloaded files with `sh install.sh --archive
atf-VERSION-linux-x64.tar.gz --checksum SHA256SUMS`, or from a release with
`sh install.sh --version VERSION`. Without `--version`, the installer resolves
the latest GitHub release tag. It verifies the archive checksum before
extracting. The stable executable is `$HOME/.local/bin/atf`; payloads live in
`$HOME/.local/share/agentteamforge/releases/VERSION`.

For an upgrade, finish active work, then run the installer for the new version
(`--state-dir DIR` if setup used a custom state path).
It stops the current daemon before switching `current` and keeps older payloads.
Rerun `atf setup --mode headless|herdr --apply` with the previously selected
mode, run `atf start`, and restart clients. Back up the state directory before
an upgrade that changes the database schema; switching binaries back does not
roll back the database.

`atf uninstall` removes unchanged installer-owned payload files and links.
It leaves state in `${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge`.
`atf uninstall --purge` also deletes that state after checking for an ATF
profile and key. Client registrations are handled by setup and should be
removed separately until ownership tracking for registrations is implemented.
