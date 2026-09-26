# Independent review: P2 private-file read port

2026-09-26. Independent Codex review of Claude-authored `a7fbea1`, based on
`2d6d0c9` (`integration/canonical-wave`). Scope: the P2 port into the canonical
root layout, its callers and tests, and the gates below. The approved legacy
source is `fdf2402`; its earlier review is
[m0-private-file-review.md](m0-private-file-review.md).

**Verdict: approve P2 for Linux x64, with 0 blocking port findings.** The four
ported C# files are byte-identical Git blobs to the reviewed files at both
`fdf2402` and `fb53199`. No path or namespace adaptation was needed. The branch
contains one commit after its exact canonical base; neither legacy commit is
an ancestor. This is source promotion, not a new implementation or a claim of
broader platform support.

## Findings and security check

- **No new port finding.** `ReadPrivateFile` opens once with `O_RDONLY |
  O_NONBLOCK | O_NOCTTY | O_CLOEXEC | O_NOFOLLOW`. `statx(fd, "",
  AT_EMPTY_PATH, ...)` requires type, mode, UID and size metadata on that open
  descriptor. It requires a regular file owned by the effective UID, rejects
  group/other and special mode bits, checks the reported 16 KiB size limit,
  then reads at most 16 KiB plus one byte through the same `SafeFileHandle`.
  There is no path reopen after validation. Unknown architectures fail closed
  when the `O_NOFOLLOW` constant is unavailable.
- **Two non-blocking source limitations remain**, as recorded in the original
  review: a `RandomAccess.Read` I/O exception is not mapped to
  `private_file_unsafe`/exit 78, despite the broad wording in `port-p2-report.md`;
  and `O_NONBLOCK` does not guarantee prompt completion for every device or
  stalled remote filesystem. Neither weakens the descriptor, type, owner, mode,
  or byte-bound checks. Scope future error and liveness claims accordingly.
- The tests are meaningful regression coverage: eight focused reads cover an
  exact-limit regular file, oversize, missing, permissive mode, final symlink,
  directory, writerless FIFO and `/dev/zero`; three process scenarios cover
  writerless FIFO at daemon/client credential and daemon profile reads. The
  source report's red run found three failures against the old implementation;
  I did not independently repeat that red run. Foreign-owner files, concurrent
  mutation and arm64 execution are not covered by these tests.

## Provenance and independent gates

The four canonical/source blob IDs match exactly: `Native.cs` `4f40c08a`,
`StateDirectory.cs` `18593102`, `PrivateFileReadTests.cs` `18e392ef`, and
`PrivateFileScenarios.cs` `60a818a`. `git merge-base` is `2d6d0c9`, and
`git rev-list --count 2d6d0c9..a7fbea1` is 1. Both `fdf2402` and `fb53199`
fail the ancestor check. `git diff --check 2d6d0c9..a7fbea1` passed.

On Linux x64 (`Linux 7.2.5-3-omarchy x86_64`), using the pinned
`11.0.100-rc.1.26425.128` SDK via
`DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`:

| Gate | Independent result |
| --- | --- |
| Root restore and `dotnet format --verify-no-changes` | Passed |
| Release build with `-warnaserror` | Passed, 0 warnings/errors |
| Full root tests | Passed, 72/72 |
| Linux x64 Native AOT publish, warnings as errors | Passed; `atf` 9,792,752 bytes |
| Published native process scenarios | Passed, 22/22 (`binary_kind=native`) |
| Published-binary fake-core demo | Passed, 1/1 selected scenario |

Commands: `DOTNET=... ./scripts/verify.sh`, then `DOTNET=...
./scripts/published-smoke.sh <published-atf>` and `DOTNET=...
ATF_DEMO_BIN=<published-atf> ./scripts/demo.sh`. A terminal-server crash cut off
the first published-scenario invocation after AOT publish, so I reran that stage
to completion against the published binary. Its SHA-256 was
`7599117a51bc7c0a7562c6d9a488b27b2b4b6527b1010e93d0a6929338710b2b`.
The manifest and demo logs remain in ignored `evidence/` and `.run/` directories.

Only Linux x64 and the fake-core checkpoint are qualified here. Arm64,
Windows, macOS, real agents, interactive terminals, native wake, and
power-loss behavior were not tested by this review.
