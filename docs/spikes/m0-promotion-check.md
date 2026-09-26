# M0 reviewed durable-core promotion check

2026-09-26, Linux x86_64. **Hold source promotion to `main` pending a
deterministic inspection test fix, independent rereview, and a green full-suite
rerun.** The existing fake-core checkpoint remains bounded and approved on its
earlier snapshot. This check does not promote real agents or a product phase.

## Pinned integration and combined gates

Integration started at `81a11b27fb67d2ec600ace67586c092829927f8f`.
Reviewed inspection source `9fb08b6263a2e30e4eb04423e2ecababf99eb562`
was merged with `--no-ff` as `289ef5bf96b02bc24b334cae4d4b45ca793b815d`;
its parents are the stated base and source commit. Git reported no conflicts.
`DaemonCommand` retains both `AdmissionGate` and `ListJobs` composition; no
integrator-authored runtime or test change was needed. The public inspection
rereview from `1e9fc7213b18eb6d285f25523c100acda82ebd48` was copied
byte-for-byte at `f9b0dd5038a0e723df0f0883fd6b92f754652c11`.
The tested source tree before this report was
`7d6b71092d83a85651c373846376d2a19d0d1f4f`.

The pinned SDK was `11.0.100-rc.1.26425.128` from the main checkout's
`.tools/dotnet11/dotnet`; no SDK or package was installed. From
`spikes/m0-durable-core/`, `DOTNET=<main checkout>/.tools/dotnet11/dotnet
./scripts/verify.sh` passed restore, `dotnet format --verify-no-changes`,
and Release build with warnings as errors (zero warnings and errors). It stopped
at the full test gate: **73 passed, 1 failed, 0 skipped, of 74**. A direct full
test rerun produced the same **73/74** result. Both failures were
`ListJobsTests.Paging_over_concurrent_changes_never_duplicates_stable_rows` at
line 85. The test assumes the first dispatch claim equals the smallest
lexically sorted UUIDv7 job ID; same-millisecond IDs can violate that
assumption. The rereview already noted another timing-sensitive assertion for
the later accepted job in this test. The reviewed source was left unchanged.

After the test failure, the same combined tree separately passed `linux-x64`
Native AOT publish with warnings as errors and **20/20** published native
process scenarios through `published-smoke.sh`. The published `atf` SHA-256 was
`05fd053fccda62d25ba1cead2aaab9a2682b78e781b96fb9a8c0f72d0f831d49`.
`ATF_DEMO_BIN=<published atf> ./scripts/demo.sh` passed its one bridge-death,
fresh-client recovery and idempotent-retry scenario. It warned that two new
temporary state directories remained; they were not removed or treated as
owned live processes by this integration task. `bash -n` on all three scripts,
`git diff --check 81a11b2 HEAD`, and the review-copy comparison passed. The
full `verify.sh` gate is **failed**, even though its individually rerun publish
and process gates passed. There was no real agent, model, Herdr, service,
Windows/macOS, or native-wake run.

## Exact source boundary for a later main promotion

Select only the tracked `spikes/m0-durable-core/` tree at Git tree
`2b1942dc4bfdec50995e1aa174622bdb4a1656f4` (**72 files**): its 14
top-level config, solution and report files; all three files in `scripts/`
(tree `bc704de9631385d496ce1b1df9251a1872801c06`); all 35 files in
`src/` (tree `0b5452c900f56a81ca22418b7da1c42d621c2627`); and all 20
files in `tests/` (tree `0b13819e45342bf053d3ec77d1dcda0090d43c36`).
`git ls-tree -r --name-only 289ef5b -- spikes/m0-durable-core` is the exact
per-file manifest. Keep the `spikes/m0-durable-core/` path for this checkpoint;
any move into product paths is a separate planned change. Do not merge the
integration branch wholesale or include `spikes/m0-interactive/` and its
unapproved legacy runtime. No `AtfSpike` or `.Spike` project occurs in this
72-file tree.

For public review traceability alongside that tree, the current `main` already
has the plan review, original core review, B1/B2 rereview, IPC review, demo
review, and original inspection review. The additional exact documents needed
from this integration branch are:

- `docs/spikes/m0-admission-fence-code-review.md` (F1 approval);
- `docs/spikes/m0-job-inspection-rereview.md` (J1/J3 approval and J2 limit);
- `docs/spikes/m0-fake-core-integration.md` (previous runnable checkpoint);
- this `docs/spikes/m0-promotion-check.md` (combined snapshot and failed gate).

## Public hygiene and remaining gates

No tracked file under the 72-file tree is a binary, raw log, test result,
database, credential, or generated artifact. The current `.gitignore` matches
`main` and excludes `.tools/`, `.run/`, `artifacts/`, `evidence/`, `bin/`,
`obj/`, database files, logs, keys, and sockets. Local generated files exist
under ignored `artifacts/`, `evidence/`, and `.run/`; select the Git tree, not
the worktree contents. A bounded tracked-text scan found no personal absolute
home path, Windows user path, email address, or private company reference in
the source tree or copied rereview. Recheck the exact staged promotion diff.

The solution has exactly three production projects:
`AgentTeamForge.Host`, `AgentTeamForge.Business`, and `AgentTeamForge.DAL`, plus
one test project; they target `net11.0`. Host references Business and DAL,
Business references DAL, and DAL has no upward project reference. One
architecture point needs an explicit disposition before a product-path move:
`JobsEndpoint` directly receives DAL `DurabilityCheckpoints` for the fake
test-profile after-commit hook. The endpoint's job operations still delegate
through Business, but this direct test hook sits outside the normal
composition-only Host-to-DAL guidance. No change was made here.

The restored test graph contains 31 NuGet packages. Local package license
metadata reports 15 MIT, 15 Apache-2.0, and one SQLite package with a bundled
`LICENSE.txt` declaring public-domain status. Direct pins are in
`Directory.Packages.props`; transitive versions come from the restored assets
file. This is an inventory, not a legal clearance. `main` has no project
license file or owner license decision; the owner must choose the repository
license and confirm distribution notices before publication. No license was
selected in this task.

Next handoff: a fresh Claude writer should make the concurrent-page test's
ordering assumptions deterministic, including the later-insert assertion,
without changing the approved live-keyset contract. An independent Codex
reviewer should review the exact fix and test evidence. Then rerun the full
.NET 11 format, build, all-tests, AOT, published scenarios, and published demo
on one snapshot and update this record. The accepted nonblocking private-file
and post-commit response follow-ups remain separate from this fake checkpoint;
J2 scalability and general outgoing-frame bounds remain outside inspection
approval. No main merge, push, product-path move, or live process cleanup was
performed.
