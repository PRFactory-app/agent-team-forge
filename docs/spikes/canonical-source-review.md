# Independent review: canonical core source lift

**Verdict: bounded approval of the source-only lift; no blocking finding.**
Reviewed Claude commits `a831fb1` and `3f0b0c3` (tip
`3f0b0c390a3ca3b519bbac095739b0b8ca315adf`) against base
`bf4ffc1b5392afe8de878b66b897d3b6521623ae`, pinned source
`81a11b27fb67d2ec600ace67586c092829927f8f`, the full
`canonical-source-report.md`, and the independent plan review at
`e1d77ea1532dafea6ab1d0ccf26dfd2a624ad2b2`. This is opposite-family
code review of the actual two-commit diff. It approves this candidate for
integration consideration, not the separate tooling or documentation lanes,
milestone merge, publication, real agents, or a product phase exit.

## Source and contract checks

- The import contains exactly 58 allowlisted root files. For every file,
  destination path, mode, and blob ID match the mapped entry in
  `81a11b2:spikes/m0-durable-core/` and all 58 rows in the writer's manifest.
  The pinned subtree is `e8fea5170f0239d16b5116e8d8e46927699c1f2c`;
  the root tree after import is `b1a3d00cfd7d8b44fa9b52032e1e5e25b7e1d43d`.
  No production or test blob changed during relocation.
- The two commits add only the 58 source files, the source report, and two
  tracked legacy isolation files. `git merge-base HEAD main` is `bf4ffc1`;
  `81a11b2` is not an ancestor. No legacy runtime files, generated outputs,
  credentials, or raw evidence were imported. `git diff --check` passed.
- Root `AgentTeamForge.slnx` names exactly three production projects (Host,
  Business, DAL) and one test project. Business references DAL; Host references
  Business and DAL for composition/setup. The pinned `net11.0` RC1 SDK,
  disabled roll-forward, central package versions, Linux annotation, and
  public-only NuGet source were retained byte-for-byte. No .NET 10 retarget,
  private feed, or secret reference was introduced by the reviewed diff.
- The temporary Host-to-DAL exception is the `AcceptAfterCommit` test
  checkpoint in `JobsEndpoint`. Its sole endpoint effect is after Business
  returns `accepted`; job reads and writes still go through Business. The
  daemon rejects test fault flags without an explicit test profile. The other
  Host DAL uses are daemon composition and database creation during `init`;
  MCP bridge and client code contain no DAL use. The report names the
  regression and a removal trigger: a separately reviewed Business seam, or
  before a non-fake backend endpoint is added, whichever occurs first.
- The retained interactive tree gains `{}` in its own `global.json` and an
  empty `Directory.Packages.props`, so root SDK and central package discovery
  stop there. From that directory, the installed `dotnet` resolves `10.0.401`
  and NuGet lists only public nuget.org. The writer's disposable-copy check
  records `net10.0`, unchanged direct package versions and analyzer/AOT
  properties, plus passing legacy restore/format/build. Root NuGet feed
  narrowing remains an explicit, documented exception. The legacy runtime
  source is untracked and absent from this review worktree, so I did not
  independently rerun its project checks or its live-session tests.

## Fresh isolated verification

I archived tip `3f0b0c3` into a disposable `/tmp` checkout and copied the
three unchanged, executable (`100755`) scripts from pinned `81a11b2` into
that checkout. Root scripts belong to a separate tooling lane and are absent
from this source branch. With the repository-local
`<main-checkout>/.tools/dotnet11/dotnet`
(`11.0.100-rc.1.26425.128`) on Linux x64:

| Check | Result |
| --- | --- |
| `bash -n scripts/*.sh`; restore; `dotnet format --verify-no-changes` | Passed |
| Release solution build with `-warnaserror` | Passed, 0 warnings and 0 errors |
| Full solution tests from clean outputs | 61 passed, 0 failed, 0 skipped |
| Linux x64 Native AOT publish; published-binary scenario suite | Passed; 19 passed, 0 failed |
| Published-binary demo | Passed; TRX total/executed/passed = 1/1/1 |
| Test output assets | `atf` apphost and Linux x64 native SQLite asset present |

The published ELF is 9,788,576 bytes, SHA-256
`8515a80cc2e96dc5d716bf75b86291138a509346ac84c0ad97d3f557afbeb62e`;
the SQLite shared library is
`eddcd4aa561d5b8f252db77e8272e7d1aed96bcab9fda3f177ca542f916290bf`.
The ELF checksum differs from the writer's fresh publish despite identical
source blobs and size; the full published suite and demo passed. This review
does not require byte-for-byte AOT reproducibility.

The tooling lane still needs its own review, and the integrator must run
combined gates on the selected merged snapshot. Windows/macOS, real agents,
interactive terminals, native wake, power-loss durability, and legacy
live-session tests were not established by this Linux fake-core review.
