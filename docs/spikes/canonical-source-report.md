# Canonical core source lift: import manifest and verification

**Status:** candidate for independent opposite-family code review. This is a
bounded, source-only relocation. It is not review approval, a merge to the
milestone or main branch, a publication decision, or a product phase exit.
It follows the [consolidation plan](product-core-consolidation-plan.md) and its
independent approval at `e1d77ea` (`product-core-consolidation-plan-review.md`).

## Frozen input

| Item | Value |
| --- | --- |
| Source commit | `81a11b27fb67d2ec600ace67586c092829927f8f` (reviewed fake checkpoint) |
| Source subtree | `spikes/m0-durable-core/`, tree `e8fea5170f0239d16b5116e8d8e46927699c1f2c` |
| Destination base | `bf4ffc1` (documentation-only branch) |
| Import commit | first commit on this branch after `bf4ffc1`; root tree after import `b1a3d00cfd7d8b44fa9b52032e1e5e25b7e1d43d` |
| Recorded source evidence | 61/61 tests, 19/19 published scenarios, 1/1 demo (see `m0-fake-core-integration.md`) |

Job inspection (`9fb08b6`/`289ef5b`/`f9b0dd5`), later hardening, and all legacy
runtime ancestry are **excluded**. The import used `git update-index --cacheinfo`
with each source blob ID and mode, then `git checkout-index`; it did not merge,
cherry-pick, or otherwise make `81a11b2` or its legacy parents reachable from
this branch. No untracked or ignored file was imported.

## Path mapping

`spikes/m0-durable-core/<path>` → `<path>` at repository root, for these
allowlisted paths only: `src/**`, `tests/**`, `AgentTeamForge.slnx`,
`global.json`, `Directory.Build.props`, `Directory.Packages.props`,
`nuget.config`, `.editorconfig`. Relative project references
(`../AgentTeamForge.*`, `../../src/...`) are unchanged and remain valid.

Excluded from this lane, by ownership:

| Source path (under the subtree) | Owner / disposition |
| --- | --- |
| `scripts/{verify,published-smoke,demo}.sh` | Tooling lane: root `scripts/` with the same relative layout. |
| `README.md`, `REPORT.md`, `ADMISSION-FENCE-REPORT.md`, `CLIENT-DEADLINES-REPORT.md`, `CORE-FAULT-FIXES.md`, `DEMO-RUNNER-REPORT.md` | Evidence-inventory/docs lanes: curate under `docs/spikes/`. |

The original durable-core subtree is not tracked on this branch and nothing
was deleted. Retiring it elsewhere remains a separate, file-by-file decision.

## Per-file manifest (58 files, byte-identical, source blob = destination blob)

Comparison before any edit: `git ls-files -s` of the destination paths matched
the source `git ls-tree -r` rows exactly (mode, blob, mapped path). No imported
file has been edited since.

| Destination path | Mode | Blob |
| --- | --- | --- |
| `.editorconfig` | 100644 | `65d4ac317f73b7407b5709b5dc0a0beae2bad795` |
| `AgentTeamForge.slnx` | 100644 | `6779068861323682ec27ec0042631de9a5075e19` |
| `Directory.Build.props` | 100644 | `a9d557bb82a71ed47f00e8bdec83a2949f88f847` |
| `Directory.Packages.props` | 100644 | `cea055b357f6a7dd1666fbf5f7e76065ad93929e` |
| `global.json` | 100644 | `b53c3000331ed4598ddc7d33101e3e1971c3eb5b` |
| `nuget.config` | 100644 | `15f6b3ef9c0f72827eec63e941b39dbe2326c02e` |
| `src/AgentTeamForge.Business/AgentTeamForge.Business.csproj` | 100644 | `c4932d2a199931c5eedc50a508fc72cc96ffa7b8` |
| `src/AgentTeamForge.Business/Features/Agents/Backends/FakeProcessBackend.cs` | 100644 | `8bf3443e9296e917bc3678b003e0c042c7c01494` |
| `src/AgentTeamForge.Business/Features/Agents/Backends/FakeWire.cs` | 100644 | `120f4a93bfbdaeab82f94dda7d91708a3702db0f` |
| `src/AgentTeamForge.Business/Features/Agents/Backends/IJobBackend.cs` | 100644 | `14b80dd95953b1bc4b70aeaaa3b1b23fa28e6c54` |
| `src/AgentTeamForge.Business/Features/Jobs/AcceptJob.cs` | 100644 | `bc214ebc2499938cf2fc0c6b91d9277f768342d2` |
| `src/AgentTeamForge.Business/Features/Jobs/AdmissionGate.cs` | 100644 | `e1950a558bdaa882bc949f67863cc91240a30b00` |
| `src/AgentTeamForge.Business/Features/Jobs/DispatchJob.cs` | 100644 | `3a44111df67802473e7be66441a618e920c35be5` |
| `src/AgentTeamForge.Business/Features/Jobs/GetJob.cs` | 100644 | `e19eafb6479f96ff6aa4b9620fe1613752b90cd7` |
| `src/AgentTeamForge.Business/Features/Jobs/JobContracts.cs` | 100644 | `4dd215a6e3d1c23bbe6ea92021074f2918acdb77` |
| `src/AgentTeamForge.Business/Features/Recovery/RecoverOnStartup.cs` | 100644 | `4d80b154d113f9f0e37fa3badc31c4a197081d24` |
| `src/AgentTeamForge.Business/SpikeProfile.cs` | 100644 | `6088b991181f99b095a0b085cc30e904b3ad7abf` |
| `src/AgentTeamForge.DAL/AgentTeamForge.DAL.csproj` | 100644 | `366fba37059a76f72b06b2a67d14a9a92e6d4e72` |
| `src/AgentTeamForge.DAL/Features/Jobs/JobRecords.cs` | 100644 | `d362873347ae0a6a32e3d7209024fb901193529c` |
| `src/AgentTeamForge.DAL/Features/Jobs/JobStore.cs` | 100644 | `72a9138774f5ec3f1fb35de7b3fc4891faa5c810` |
| `src/AgentTeamForge.DAL/Migrations/Schema.cs` | 100644 | `34babe05a4aa7c08302bc63d945b1e9f08ff70cf` |
| `src/AgentTeamForge.DAL/Sqlite/DurabilityCheckpoints.cs` | 100644 | `7af38c1d301ccdb58ed49ec29041802a2e2c6ffe` |
| `src/AgentTeamForge.DAL/Sqlite/JobDatabase.cs` | 100644 | `14dce9ecb09ca7e63a5864facd3469da16783484` |
| `src/AgentTeamForge.DAL/Sqlite/StorageException.cs` | 100644 | `333f9e3819674c287c224eb0dd7f9fca7c9819ab` |
| `src/AgentTeamForge.Host/AgentTeamForge.Host.csproj` | 100644 | `fa6435d93a17328bfa065703960d6cd1ac08c151` |
| `src/AgentTeamForge.Host/Features/FakeBackend/FakeBackendCommand.cs` | 100644 | `56e7925398a950fa969ece3bc9c317309dc42b51` |
| `src/AgentTeamForge.Host/Features/Jobs/ClientCommand.cs` | 100644 | `5a8ba46dcbd2ce49b8eb45cf37a773a0de8e04aa` |
| `src/AgentTeamForge.Host/Features/Jobs/JobsEndpoint.cs` | 100644 | `ebe9fa8e6cb86862c8044bb15112a65b2b42357c` |
| `src/AgentTeamForge.Host/Features/Jobs/JobsMcpBridge.cs` | 100644 | `b729ff219a81484a04d895fe662bef92fbf1c877` |
| `src/AgentTeamForge.Host/Features/Setup/InitCommand.cs` | 100644 | `897f3114a8d77a8ad97bab9ed5202beed5ce1b62` |
| `src/AgentTeamForge.Host/Features/Setup/SpikeProfileFile.cs` | 100644 | `aac27f26eab9221dc844ea57bd28f4dc0b7d425c` |
| `src/AgentTeamForge.Host/Hosting/DaemonCommand.cs` | 100644 | `ebb9669114c8cb36fbb79be15fc86fc57e71f88a` |
| `src/AgentTeamForge.Host/Hosting/DaemonLock.cs` | 100644 | `4ef1fa7e5b00349059f0105edf4d565bbf112ebb` |
| `src/AgentTeamForge.Host/Hosting/Native.cs` | 100644 | `62f667807d1f9541eaccc0521135a9133a343282` |
| `src/AgentTeamForge.Host/Hosting/StateDirectory.cs` | 100644 | `e05733d91d4d4bd03601fa75153990bc241a2817` |
| `src/AgentTeamForge.Host/Program.cs` | 100644 | `2227f6c8cdf81a3c936a2c5390861d28cc976071` |
| `src/AgentTeamForge.Host/Transport/Frames.cs` | 100644 | `b78fd2676075c47f00ef22b9efaa4e13971fc7e0` |
| `src/AgentTeamForge.Host/Transport/IpcClient.cs` | 100644 | `4f912b9afa9b41613a72ff3d2bc99b4c42d8b722` |
| `src/AgentTeamForge.Host/Transport/IpcMessages.cs` | 100644 | `05dac16d2e09c118beaa8f4c7b4b4d1f4793f571` |
| `src/AgentTeamForge.Host/Transport/IpcServer.cs` | 100644 | `d3a15091bb8bdf2c6036c17e14867f3bffdbb80f` |
| `tests/AgentTeamForge.Tests/AgentTeamForge.Tests.csproj` | 100644 | `628c484a090caf81be976662f6f6e96ff7a9101a` |
| `tests/AgentTeamForge.Tests/Features/Jobs/AcceptJobTests.cs` | 100644 | `d8351a14736bcc69fc98d1c7fa82be63dc6fd1f5` |
| `tests/AgentTeamForge.Tests/Features/Jobs/AdmissionFenceTests.cs` | 100644 | `42885875638b1dc3d2d00097d809979d7e53b9b7` |
| `tests/AgentTeamForge.Tests/Features/Jobs/DispatchFaultTests.cs` | 100644 | `ff51c20a385cc1b94f442a369df1b69916844006` |
| `tests/AgentTeamForge.Tests/Features/Jobs/DispatchJobTests.cs` | 100644 | `90e891e2bdf5daa976a0e209ad87c66679577d9d` |
| `tests/AgentTeamForge.Tests/Features/Jobs/SchemaTests.cs` | 100644 | `e69780d2a789e0b5c8b30a76e9f0fe57f6d54b49` |
| `tests/AgentTeamForge.Tests/Features/Recovery/RecoverOnStartupTests.cs` | 100644 | `02c9d1105ed309674c656aa4919f9690bdc5c465` |
| `tests/AgentTeamForge.Tests/Scenarios/ClientLifetimeScenarios.cs` | 100644 | `367ec655998245e048b0168bf43b0d3fb3d422fb` |
| `tests/AgentTeamForge.Tests/Scenarios/CoreFaultScenarios.cs` | 100644 | `1057a3bc3585ef09073e73a8561145c2a12e8af1` |
| `tests/AgentTeamForge.Tests/Scenarios/CrashBoundaryScenarios.cs` | 100644 | `5ed59187b7e73b0d8700aee1826e14d5c8c11c4c` |
| `tests/AgentTeamForge.Tests/Scenarios/PrivateBoundaryScenarios.cs` | 100644 | `3def6e570e70037af54b0354e7861849d51702ee` |
| `tests/AgentTeamForge.Tests/Support/Bounded.cs` | 100644 | `57732ed543566fba00d06a4a1d3870b20f03bf80` |
| `tests/AgentTeamForge.Tests/Support/JobFixture.cs` | 100644 | `d66a8acaafad383042c1d585212486565a48dd0e` |
| `tests/AgentTeamForge.Tests/Support/OwnedProcesses.cs` | 100644 | `2443e9df87155ba22d4ae2b6c47b8138fdc6a11f` |
| `tests/AgentTeamForge.Tests/Support/ScriptedBackend.cs` | 100644 | `b70290bd7ad8892b4c7c313f15985901eb2e724c` |
| `tests/AgentTeamForge.Tests/Support/SpikeRig.cs` | 100644 | `a2360a56006e64ed347d12c04393bc4f56ada92c` |
| `tests/AgentTeamForge.Tests/Support/TempStateDir.cs` | 100644 | `ed68fbfe403b121a30bc651c92f29d3ae32fac27` |
| `tests/AgentTeamForge.Tests/Transport/IpcClientDeadlineTests.cs` | 100644 | `678490ab773c8038d2eb4816dafec8ff52d82a3f` |

## Retained legacy isolation (config only)

Root SDK/NuGet/MSBuild files are now discovered by commands run under the
retained .NET 10 `spikes/m0-interactive/` tree. That tree's runtime source is
local and untracked; it was not imported, edited, or deleted. Effective settings
were compared in a disposable copy (runtime source only; no `.run`, `evidence`,
`bin` or `obj`) with and without the new root files, using the default `dotnet`
(SDK 10.0.401) and the project-local pinned SDK.

| Effective input | Before (no root files) | After root files only | After isolation |
| --- | --- | --- | --- |
| SDK via default `dotnet` | 10.0.401 | **fails**: pinned 11 RC1, roll-forward disabled | 10.0.401 |
| `TargetFramework` | net10.0 | net10.0 | net10.0 |
| Central package file | none | **root file imported** (5 `PackageVersion` items; CPM still off) | legacy empty file, 0 items |
| `PackageReference` versions | Test.Sdk 18.0.1, xunit 2.9.3, runner 3.1.5 | unchanged | unchanged |
| Analyzer/AOT/warning properties | legacy `Directory.Build.props` values | unchanged (legacy props still stop discovery) | unchanged |
| `.editorconfig` | legacy file, `root = true` | unchanged | unchanged |
| NuGet feeds | nuget.org plus three user-level feeds | **nuget.org only** (root `<clear />`) | nuget.org only |

Isolation added (tracked, config only):

- `spikes/m0-interactive/global.json` = `{}`: stops upward SDK pin discovery,
  restoring the previous "latest installed SDK" resolution. It does not pin or
  retarget the legacy tree.
- `spikes/m0-interactive/Directory.Packages.props`: empty; stops discovery of
  the root central versions.

**Accepted exception: feed narrowing.** NuGet has no mechanism to stop
parent-directory configuration, and restoring the previous feed set would
require naming user-level private/local feeds in the repository. The legacy
tree therefore restores from public nuget.org only. All its direct packages are
public nuget.org packages. With isolation in place, legacy `dotnet restore`,
`dotnet format --verify-no-changes`, and `dotnet build -c Release -warnaserror`
passed on SDK 10.0.401 in both the before and after copies. Legacy tests were
**not** run (unchanged binaries; its suites include live-session dependencies).
Earlier .NET 10 evidence is not reinterpreted as .NET 11 evidence.

## Temporary exception: Host after-commit test hook

`src/AgentTeamForge.Host/Features/Jobs/JobsEndpoint.cs` calls DAL
`DurabilityCheckpoints.Hit(AcceptAfterCommit)` after Business `AcceptJob`
returns `accepted`. This lift keeps that code unchanged under a narrowly scoped
**temporary exception**:

- **Scope:** only the fake/test-profile `accept.after-commit` checkpoint (after
  commit, before the IPC response). `DaemonCommand` refuses `--test-crash-at` and
  `--test-fail-at` without an explicit test profile; otherwise the callback
  matches nothing and is inert.
- **Normal operations still go through Business:** the endpoint executes only
  Business `AcceptJob`/`GetJob`; it reads/writes no DAL records.
- **Host files referencing DAL:** exactly `JobsEndpoint.cs` (this hook),
  `Hosting/DaemonCommand.cs` (daemon composition), and `Features/Setup/InitCommand.cs`
  (creates the database during `init`), plus the project reference.
  `JobsMcpBridge.cs`, `ClientCommand.cs` and `Transport/*` have no DAL reference,
  so the `mcp` bridge and `client` roles cannot open the job database.
- **Regression coverage retained:** `CrashBoundaryScenarios` drives
  `--test-crash-at accept.after-commit`; it passed in the full and published runs.
- **Removal trigger:** a separate, reviewed minor fix that routes the checkpoint
  through a small Business-owned seam (keeping failure timing and this scenario),
  or before any non-fake backend endpoint is added, whichever is first.
  No semantic refactor was made in this lift.

## Verification (fresh outputs, Linux x64)

Environment: Linux 7.2 x86_64; project-local SDK `11.0.100-rc.1.26425.128`
selected per command with `DOTNET=<repo>/.tools/dotnet11/dotnet`; no SDK or
package installation. Because root `scripts/` belongs to the tooling lane, gates
ran in a disposable scratch checkout: `git archive` of this branch plus the
three `81a11b2` scripts copied unchanged (mode 0755) into `scripts/`. No
`bin`, `obj`, `artifacts`, `evidence` or `.run` existed beforehand.

| Command (from scratch checkout root) | Result |
| --- | --- |
| `DOTNET=… ./scripts/verify.sh` (restore, format verify, `-warnaserror` Release build, full tests, AOT publish, published scenarios) | exit 0; 0 warnings; **61/61** tests passed; **19/19** published scenarios passed (native binary) |
| `DOTNET=… ATF_DEMO_BIN=artifacts/linux-x64/atf ./scripts/demo.sh` | exit 0; trx counters total=1, executed=1, passed=1 |

Published `atf` (native AOT) SHA-256
`2fcae8f340d138cc732813c06969dbbf8830d5c90b1618a57ee5158754828b0d`, 9,788,576 bytes;
`libe_sqlite3.so` `eddcd4aa561d5b8f252db77e8272e7d1aed96bcab9fda3f177ca542f916290bf`.
The test output contains the Host apphost `atf` and SQLite `runtimes/` assets,
so `SpikeRig`'s default path works from a clean build. Root `.gitignore` covers
`bin/`, `obj/`, `artifacts/`, `evidence/` and `.run/` at the new locations.
`git diff --check` is clean.

These counts are for this candidate and happen to equal the recorded source
counts because the source is byte-identical. They are **not** a combined root
demo from the tooling lane's scripts; that remains for integration.

Not run here: shell syntax checks of root scripts (tooling lane), current-doc
link/command updates (docs lane), legacy tests, Windows/macOS. Linux fake-core
results do not qualify real agents, interactive terminals, native wake,
power-loss durability or other platforms.
