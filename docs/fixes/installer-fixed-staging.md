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
