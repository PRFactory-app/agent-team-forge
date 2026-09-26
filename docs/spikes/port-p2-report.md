# Port P2: descriptor-bound private file reads

Implementation checkpoint for slice **P2** of
the wave 2 plan `docs/spikes/wave2-slices.md` (plan-wave2 worktree)
(port map rows P2). Claude-authored; independent Codex review is **pending** and
this report is not review approval.

## Provenance

- Base: `integration/canonical-wave` @ `2d6d0c9`. Branch `feature/port-p2-private-reads`.
- Source: reviewed commit `fdf2402` as carried unchanged on `integration/m0-e2e`
  @ `fb53199` (the four blobs are byte-identical at `fdf2402`, `cd60824` and `fb53199`).
- Canonical `Native.cs` and `StateDirectory.cs` at `2d6d0c9` were byte-identical
  to the legacy parents of `fdf2402`, so the import is a whole-blob replacement
  with no adaptation.
- Imported with `git show fb53199:spikes/m0-durable-core/<path> > <path>`; no
  merge or cherry-pick. `fdf2402` and `fb53199` are not ancestors of the result.

| Canonical path | Blob |
| --- | --- |
| `src/AgentTeamForge.Host/Hosting/Native.cs` | `4f40c08a` |
| `src/AgentTeamForge.Host/Hosting/StateDirectory.cs` | `18593102` |
| `tests/AgentTeamForge.Tests/Hosting/PrivateFileReadTests.cs` | `18e392ef` |
| `tests/AgentTeamForge.Tests/Scenarios/PrivateFileScenarios.cs` | `60a818a6` |

The legacy `PRIVATE-FILE-BOUNDS-REPORT.md` belongs to P3 and is not ported here.

## Behaviour preserved

`StateDirectory.ReadPrivateFile` opens the path once with
`O_RDONLY|O_NONBLOCK|O_NOCTTY|O_CLOEXEC|O_NOFOLLOW`, then checks type (regular),
owner (`geteuid`), mode (no group/other bits) and size (≤ 16 KiB) via `statx`
with `AT_EMPTY_PATH` on that descriptor, and reads at most 16 KiB + 1 bytes from
the same descriptor (growth after `statx` is rejected). There is no
validate-then-reopen. Everything unsafe fails closed as `private_file_unsafe`;
only `ENOENT` maps to `private_file_missing`. Unknown architectures (no known
`O_NOFOLLOW`) fail closed.

## Red → green

Red: the new `PrivateFileReadTests` against the previous implementation (only
`MaxPrivateFileBytes` added so it compiled): **3 failed / 5 passed of 8** —
oversize accepted, FIFO read timed out after the 5 s bound (would hang), and a
directory reported `private_file_missing`. Symlink, group-readable and
`/dev/zero` were already rejected by the old path-based checks and remain
regression coverage.

Green: all 8 pass with the ported implementation.

## Gates (Linux x64, SDK 11.0.100-rc.1.26425.128)

```
DOTNET=<repo>/.tools/dotnet11/dotnet scripts/verify.sh
DOTNET=<repo>/.tools/dotnet11/dotnet ATF_DEMO_BIN=<publish-dir>/atf scripts/demo.sh
```

| Gate | Result |
| --- | --- |
| restore, format `--verify-no-changes`, Release build `-warnaserror` | Passed |
| Full tests | Passed, **72/72** (base 61 + 8 unit + 3 scenario) |
| Native AOT publish (linux-x64) | Passed; `atf` 9,792,752 bytes, sha256 `de95ad49…6fac3` |
| Published process scenarios | Passed, **22/22** (base 19 + 3 `PrivateFileScenarios`), `binary_kind=native` |
| Published-binary demo | Passed, 1/1 selected scenario |

Raw logs, TRX files and manifests stayed in gitignored `artifacts/`, `evidence/`
and `.run/` directories.

## Limits

- Linux x64 evidence only. The `O_NOFOLLOW` Arm64 constant and the `statx`
  layout are unexercised on other architectures; Windows/macOS have no
  implementation of this read path and are unqualified.
- The directory's own owner UID is still not read (0700 check + socket peer
  UID stand in), unchanged from the source.
- `/dev/zero` coverage is fail-closed only (not owner-private); the file-type
  check is exercised by the FIFO and directory cases.
- Not run: independent Codex review, combined gates after P1/P3 integration.
