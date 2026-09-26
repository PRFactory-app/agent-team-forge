# Independent review: M0 bounded private-file read

2026-09-26. Independent Codex/GPT review of Claude-authored source commit
`fdf24027cf385c54f2d7c278ec2bc0eb576b7032`, based on
`81a11b27fb67d2ec600ace67586c092829927f8f`, on `review/m0-hardening`.
Reviewed the complete source and test diff, private-file callers, relevant prior
reviews, and `spikes/m0-durable-core/PRIVATE-FILE-BOUNDS-REPORT.md`. This is a
focused review of that slice, not a review of the acceptance change or a
combined tree. No production or test source was edited.

**Verdict: approve this bounded Linux x64 private-file fix, with 0 blocking and
2 non-blocking findings below.** The former path check plus unbounded
`File.ReadAllBytes` is replaced by one `O_NOFOLLOW | O_NONBLOCK` descriptor,
descriptor-bound metadata checks, and a read capped at 16 KiB plus one byte.
The observed FIFO failure is prompt and the published native scenarios pass.
The earlier approved fake-core demo checkpoint does not depend on this
hardening; these findings do not revoke that checkpoint.

## Findings

1. **Non-blocking — read failures do not use the stated private-file error
   classification.** `StateDirectory.cs:108` allows `RandomAccess.Read` I/O
   exceptions to escape. `Program.cs` maps only `StateDirectoryException` to
   exit 78. An I/O failure after a successful `open` and `statx` therefore does
   not return `private_file_unsafe`, contrary to the report's broad "every other
   refusal" wording. This does not bypass the size, type, owner, or mode checks
   and is not a regression of the tested FIFO case. Wrap nonfatal read errors in
   the stable private-file code if that error contract is required; add a
   targeted fault test when a deterministic read failure can be injected.
2. **Non-blocking — device and remote-filesystem liveness is narrower than the
   XML summary.** `open` precedes `statx`, so file-type rejection cannot prevent
   an unusual device driver from blocking during `open` even with `O_NONBLOCK`.
   `O_NONBLOCK` also does not bound regular-file reads on a stalled FUSE or
   network filesystem. The author's report already qualifies the latter. The
   tested guarantee is prompt refusal of the local FIFO and `/dev/zero`, not
   universal nonblocking for every device or filesystem. Keep that scope in
   future claims.

## Reviewed behavior

- `Native.cs:11-20,28-55` uses Linux UAPI values for x64 and arm64: x64
  `O_NOFOLLOW=0x20000`, arm64 `O_NOFOLLOW=0x8000`, and common
  `O_NONBLOCK=0x800`, `O_NOCTTY=0x100`, `O_CLOEXEC=0x80000`. The arm64 override
  is present in the [Linux arm64 UAPI header](https://github.com/torvalds/linux/blob/master/arch/arm64/include/uapi/asm/fcntl.h);
  the remaining common flags are in the
  [generic UAPI header](https://github.com/torvalds/linux/blob/master/include/uapi/asm-generic/fcntl.h).
  This review checked constants against headers, not an arm64 run.
- `statx(fd, "", AT_EMPTY_PATH, required, ...)` inspects the opened object;
  the explicit structure offsets match the
  [Linux statx layout](https://github.com/torvalds/linux/blob/master/include/uapi/linux/stat.h).
  Missing type, mode, UID, or size mask bits fail closed. Type must be regular,
  UID equals the effective UID, group/other and special mode bits are rejected,
  and reported size is capped. A final-component symlink fails `open` with
  `O_NOFOLLOW`; an intermediate path component is outside this check.
- The `SafeFileHandle` owns the descriptor from immediately after successful
  `open` through `statx` and all positional reads, including exceptional exits.
  The read loop cannot copy more than 16 KiB plus one byte into its buffer. A
  file that grows past the bound is rejected; a file changed during the read
  can still produce a mixed snapshot, which this slice does not promise to
  prevent.
- Tests cover exact-limit content, oversize, group-readable, missing,
  final symlink, directory, FIFO without writer, and `/dev/zero`. The device
  test checks fail-closed behavior on that device; owner and mode also reject
  it. Foreign-owned regular files, concurrent mutation, arm64 execution, and
  unusual device-driver blocking were not exercised.

## Independent gates and limits

From this exact clean source tree, with command-scoped main-checkout SDK
`11.0.100-rc.1.26425.128`, ran:

```bash
main_checkout="$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)"
DOTNET="$main_checkout/.tools/dotnet11/dotnet" ./scripts/verify.sh
```

Inside `spikes/m0-durable-core`, restore and format verification passed;
Release `-warnaserror` build had 0 warnings and 0 errors; **72/72** tests
passed; Linux x64 Native AOT publish passed with warnings as errors; **22/22**
scenarios passed against the published native binary. The tested `atf` SHA-256
was `ab9ac37b8f376ebecf18bf41b36d5edced1df659393e557120b1db121f7537e8`.
`git diff --check 81a11b2..fdf2402` passed. The hash identifies this build;
byte reproducibility was not tested. The existing request-serialization bound
from `m0-client-deadlines-review.md` remains open and is outside this diff.

Only Linux x64 was run. No arm64, Windows, macOS, Pi, real agent, Herdr,
service, wake, or power-loss behavior is approved here. The tests use private
temporary state and fake processes; no live-agent state was touched.
