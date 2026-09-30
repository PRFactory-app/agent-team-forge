# Review: installer fixed staging (commit 43d30f2)

Reviewer: Claude (read-only). Scope: `install.ps1` as of 43d30f2, plus
`docs/fixes/installer-fixed-staging.md`. Only real bugs are listed.

## Items checked and found OK

- **PowerShell 5.1 compatibility.** `Remove-Item -LiteralPath`, `try { $x = & $exe } catch`,
  `$stage = $null` with `if ($stage -and (Test-Path $stage))` all work in 5.1 and 7.x.
- **Does the new `catch` catch a blocked launch?** Yes. When `CreateProcess` fails
  (ASR / SAC / Access is denied), PowerShell throws
  `System.Management.Automation.ApplicationFailedException`
  ("Program 'atf.exe' failed to run: …"). That is a terminating exception
  whatever `$ErrorActionPreference` is set to, so `try/catch` catches it in 5.1
  and 7.x. `$_.Exception.Message` contains the Win32 text. Native stderr is not
  redirected, so it cannot become an error record under `Stop`. Behavior is correct.
- **Retry at the same path.** A failed verify throws, `finally` removes
  `releases\<ver>.staging`, and the next run extracts to exactly the same path.
  The promise "the path will be the same next time" holds **for that path**
  (but see M2).
- **Rollback when `Move-Item $stage $bin` fails.** A directory move on the same
  volume is a rename, so it either succeeds completely or does nothing. The catch
  moves `bin.previous` back and rethrows. `finally` then removes `bin.staging`.
  This is correct if the restore move succeeds (see M1 for when it does not).
- **Already-installed early return.** This happens before `$stage` is assigned, so
  `finally` skips stage cleanup. Leftover `bin.staging`/`bin.previous` stay
  untouched. That is harmless, and `bin` is valid on this path.
- **Leftover `releases\<ver>.staging` / `bin.staging` from an aborted run.** These are
  removed before reuse (lines 141 and 162), so the next run recovers.

## Findings

### M1 — Medium — line 163: a leftover `bin.previous` is deleted even when it is the only good copy

State traces:

| Earlier run ended… | State on disk | Next run |
|---|---|---|
| Interrupted (Ctrl+C, crash, reboot, killed console) between line 171 and line 174 | no `bin`, `bin.previous` = old good install | line 152 skipped (no bin), line 163 **deletes bin.previous** |
| Line 174 failed **and** the restore `Move-Item $backup $bin` at line 176 also threw (the same lock or AV scan that broke the first move) | no `bin`, `bin.previous` = old good install | same as above |

After the deletion, if `Move-Item $stage $bin` fails again on this run (for example
an AV scanner holding a handle, or the same transient cause as before), the catch
finds no backup to restore. The user ends up with **no `bin` at all**, and the old
copy is gone permanently. Before this commit the backup had a random name, so a
later run never touched it. Deleting it by a fixed name is new behavior. In this
state the uninstall path (line 47) also skips daemon stop and client teardown,
because `bin\atf.exe` is missing.

**Fix:** before line 152, recover instead of delete:

```powershell
if (-not (Test-Path $bin) -and (Test-Path $backup)) { Move-Item $backup $bin }
```

Then keep line 163 for the case where `bin` exists, where a stale
`bin.previous` really is garbage. This recovery also makes the
"already installed" check and the daemon stop work on the restored copy.

### M2 — Medium — lines 136, 150, 155, 164/174: the ASR allow covers only the staging path; other executed paths still differ

The fix pins the path the installer **first** executes
(`releases\<ver>.staging\atf.exe`). The same binary is then executed from other
paths:

- line 150 moves it to `releases\<ver>\atf.exe`. On any rerun after a partially
  successful run (extract+move succeeded, then daemon stop or bin swap failed),
  line 136 executes **`releases\<ver>\atf.exe`**. There is no friendly catch
  there, so a path-based ASR block shows up as a raw
  `ApplicationFailedException`, at a new path the user has not allowed.
- line 155 (same-version rerun) and every normal use afterwards execute
  **`bin\atf.exe`**, which is a third path.

If the Defender "allow" for the ASR rule really is a per-path exclusion (that is
the premise of this fix), the user is blocked again at `releases\<ver>` and/or at
`bin` on first real use. The goal "allow sticks across reruns" is met only for the
first verify step.

**Fix, pick one:**
- (a) Wrap lines 136 and 155 with the same try/catch and message, so that every
  path produces the actionable message.
- (b) Better: tell the user to allow the **folder**
  `%USERPROFILE%\.local\share\agentteamforge\` (it covers staging, releases and bin).
  Say so in the Fail message and in `docs/install.md`.

Also note that Smart App Control has no per-path or per-file allow at all. The
message "Allow that path … and rerun" is wrong for SAC. SAC can only be turned off,
or the binary must be signed. Adjust the wording so SAC users are not sent into a
retry loop.

### L1 — Low — line 163: a locked/partly deleted leftover `bin.previous` now blocks every future install

Line 180 deliberately tolerates a failed `Remove-Item $backup` (warning, copy kept).
On the next run, line 163 runs `Remove-Item` on the same fixed path without a
try/catch, and it throws on the same lock. Upgrades are then impossible until the
lock goes away. Before this commit a random name meant a stuck backup never
blocked a later run. A partial recursive delete (some files removed, one locked)
also leaves a broken `bin.previous`, which M1's recovery must not restore blindly.

**Fix:** wrap line 163 in try/catch. On failure, rename the leftover to a random
name (`Rename-Item $backup ("bin.previous." + [IO.Path]::GetRandomFileName())`) or
stop with a clear "close processes using `$backup`" message. For M1, restore only
when `bin.previous\atf.exe` and `.atf-version` are both present.

### L2 — Low — lines 140–150 and 161–177: concurrent runs now collide on the fixed names

With random names, two concurrent installers (for example the user double-clicks,
or an upgrade triggered from two terminals) had separate staging directories.
Now:

- Run B's line 141 deletes run A's `releases\<ver>.staging` during A's verify, or
  B's `Expand-Archive` fails on existing files. Either run's `finally` (line 190)
  can remove the other run's extraction.
- If A has already moved staging to `releases\<ver>`, B's `Move-Item $extract $target`
  moves the staging directory **into** the existing target. The result is a nested
  `releases\<ver>\<ver>.staging`.
- B's line 163 can delete `bin.previous` right after A moved `bin` there. Combined
  with a failing A move, this gives the M1 outcome.

This is unusual, so it is Low. **Fix:** take an exclusive lock file for the
duration of the install, for example
`[IO.File]::Open("$root\install.lock", 'OpenOrCreate', 'ReadWrite', 'None')` in
the try and dispose it in finally, and fail fast with "another installer is running".

### L3 — Low — line 67: uninstall leaves `bin.previous` / `bin.staging`

Uninstall removes `bin` and `releases` but not `$root\bin.previous` or
`$root\bin.staging`. After an upgrade whose backup delete failed (line 181), an old
`atf.exe` survives uninstall. **Fix:** also remove both fixed paths in the uninstall
branch. Removing the now-empty `$root` would be nice but is optional.

## Verdict

**CHANGES_REQUESTED.** 5 findings: 2 Medium (M1, M2) and 3 Low (L1–L3).

M1 is a small, data-loss-adjacent regression introduced by the fixed backup name,
and the one-line recovery fixes it. M2 means the fix only partly delivers its
stated purpose. At minimum, allow the folder instead of the file path and put the
friendly catch on lines 136 and 155. L1–L3 can go in the same patch or be deferred.
