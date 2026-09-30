# Installer fixed staging

Branch: `fix/installer-fixed-staging`.

## Changes and decisions

- Windows extraction now uses `releases\<version>.staging`, removes leftover
  staging before extraction, verifies `atf.exe --version` there, and moves the
  verified directory to `releases\<version>` as before.
- Replacement uses `bin.staging` and `bin.previous`, cleaning leftovers before
  replacement. Stage initialization is deferred until after the already-installed
  early return so that verification-only reruns do not remove leftover bin staging.
- Existing daemon stop, checksum verification, version mismatch checks, backup
  restoration when moving stage into bin fails, and warning/retention when the
  old backup cannot be deleted remain in place.
- A failure to start the extracted executable now reports its exact path,
  mentions Defender ASR / Smart App Control, and instructs the user to allow that
  path in the Windows Security notification and rerun at the same path.
- The Windows section of `docs/install.md` explains the allow-once retry.
- `install.sh` does execute from random staging, but is unchanged: Linux/macOS
  have no Windows ASR requirement, and changing Unix staging is unnecessary scope.
- Existing tests do not assert random names. No new test was added: the existing
  infrastructure launches `install.sh` with Unix executable fixtures, and
  `install.ps1` has no isolated install-root option. No test-only root override
  or installer parameter was introduced.

## Validation on Windows

Command (SDK supplied by the owner):

```powershell
& 'C:\Projekt\git\agent-team-forge\.tools\dotnet11\dotnet.exe' test tests/AgentTeamForge.Tests/AgentTeamForge.Tests.csproj --filter FullyQualifiedName~InstallScriptTests --logger 'console;verbosity=normal'
```

Build succeeded for DAL, Business, Host and Tests. Test command exited 1:
9 tests total, 2 passed, 7 failed.

Passed:

- `WindowsReleaseIncludesInstallerAndChecksumsIt`
- `TeardownRemovesOnlyOwnedClientEntries`

Six tests failed in the unchanged `Bundle` helper at `File.SetUnixFileMode`
(line 248), with `PlatformNotSupportedException` on Windows, before installer
execution:

- `SameVersionRerunVerifiesFilesAndDoesNotStopDaemon`
- `CompletionPrintsQuotedAbsoluteSetupCommand` (known Windows failure noted by owner)
- `FailedDaemonStopKeepsCurrentReleaseAndState`
- `ReleaseDownloadsUseOneResolvedTagAndReportFailures`
- `ChecksumMismatchInstallsNothing`
- `UpgradeStopsDaemonAndUninstallKeepsUnownedFiles`

`TeardownRemovesOnlyOwnedAutostart` failed its unchanged `Assert.False` at line
237 because the autostart file remained. These failures do not exercise the
modified PowerShell flow; tests were not changed to mask them.

The final script parsed successfully using the actual Windows PowerShell
5.1.26100.9444 parser. `git diff --check` passed. No runtime execution of the
modified installer is claimed. Opposite-family review is left to the parent
before integration.

## End-to-end result

SKIPPED as explicitly instructed when no custom install root is supported.
The script accepts `-StateDir`, but computes its install root directly from
`[Environment]::GetFolderPath('UserProfile')` and exposes no install-root override.
Changing `-StateDir` does not isolate binaries or user PATH changes.
The supplied real rc1 archive was therefore not installed, and
`C:\Users\mikael.liljedahl\.local\share\agentteamforge` was not touched.

Changes and this report are committed on the requested branch; nothing is pushed.

## Review fixes

Read `docs/fixes/installer-fixed-staging-review.md` in full and addressed M1,
M2, L1, L2 and uninstall leftovers (L3).

- M1: under the installer lock, recover `bin.previous` into missing `bin` only
  when both `atf.exe` and `.atf-version` are present. This runs before install
  and uninstall, enabling verification, daemon stop and teardown on the recovered
  install. An incomplete sole backup is preserved and installation fails clearly;
  uninstall can still remove that incomplete leftover.
- M2: all executable calls (three verification paths, daemon stops and teardown)
  use one `Invoke-Atf` helper. It preserves arguments, native output and exit code.
  Launch failures name the executable and tell Defender ASR users to allow the
  install folder `%USERPROFILE%\.local\share\agentteamforge\` in Windows Security.
  Smart App Control guidance explicitly says it has no path allow and must be
  off or the binary signed; retries alone will not fix a SAC block. Updated
  `docs/install.md` accordingly. This supersedes the original path-allow advice.
- L1: stale `bin.previous` is deleted only when `bin` exists, inside try/catch.
  Chose the review's permitted clear-failure option: if removal fails, tell the
  user to close processes using that folder and rerun. The active install remains.
  The existing post-upgrade delete still warns and retains the old copy on failure.
- L2: hold a `FileStream` with `FileShare.None` on `$root\install.lock` for the
  entire install/uninstall, including recovery and cleanup. Contention fails
  fast with "another installer is running". An outer finally disposes the lock
  on success, early return and errors. Keep the lock file to avoid deletion races.
- L3: uninstall removes `bin.staging` and `bin.previous`; existing recursive
  removal of `releases` also removes every `releases\*.staging` directory.

Validation after review fixes:

- Real Windows PowerShell 5.1.26100.9444 parser: final script passed.
- Executed the actual helper extracted from the script's AST in PowerShell 5.1:
  argument forwarding and native exit code 7 passed; a nonexistent executable
  produced the shared ASR/SAC guidance.
- Executed the actual lock/recovery blocks extracted from the final script's AST
  against a temporary directory inside this repository: second lock acquisition
  failed fast, acquisition after disposal succeeded, complete backup recovery
  passed, incomplete backup was preserved/refused for install, and incomplete
  backup did not prevent uninstall cleanup. Temporary fixtures were removed.
- Re-ran the same `InstallScriptTests` filter with `--no-restore` and minimal
  console logging. Build passed; tests again exited 1 with 2 passed / 7 failed:
  the same six `SetUnixFileMode` Windows fixture failures and unchanged autostart
  assertion failure documented above. No tests were modified or suppressed.
- `git diff --check` passed. The real rc1 end-to-end install remains skipped
  because no custom install root is available; the real user installation was
  untouched. Unix installer remains unchanged.

Review fixes and this appended report are committed on
`fix/installer-fixed-staging`; nothing is pushed.
