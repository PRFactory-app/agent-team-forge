# M0 runnable fake-core integration checkpoint

2026-09-26, Linux x86_64. **PASS for the bounded fake-backend checkpoint.**
Source snapshot `22a4e79fbe407b1646071ae07dccd0fa35d080bb` on
`integration/m0-e2e`; repository tree
`aa0d1a7a69fe44e550542fccfed0fc6e54876143`. All three source merges
used `--no-ff`, retain the original source commits as parents, and had no
conflicts or integrator-authored runtime edits.

| Input | Exact source commit | Integration merge | Review |
| --- | --- | --- | --- |
| Core with F1 admission fence | `a8e43524caf9cc5236abfb673d69c2dd3d4acbe7` | `fabc1a3dc8132814015595a15bf4d30b6ebd75fa` | [Pinned approval](m0-admission-fence-code-review.md), review commit `902bc6d4c410ef11f9dd458e037ff6717cb9316d` |
| IPC client deadline | `6850c0fe196cfba25174c25c1bdb97222617a0f1` | `f4b9952abc1c21b5029b9fa7e1f1a97ffdbc237e` | [Bounded approval](m0-client-deadlines-review.md), copied verbatim from review commit `c9c99bb97f490d627c66c57fcc90925b4d9c6625` |
| Demo runner | `aba651be8c261d9b2ba5fbb2dc2e5ec50b8f6b45` | `6ff227aa2a4024e2d9efb8640210a70426c0125b` | [Conditional approval](m0-demo-runner-review.md), copied verbatim from review commit `f4ef954023910ff21899080820779a29b82c8321` |

The copied review reports were committed as documentation only at
`22a4e79fbe407b1646071ae07dccd0fa35d080bb`. The core approval resolves
the demo review's base-core condition for this checkpoint. The IPC review's
deadline qualifications remain in force. The legacy interactive safety-lane
limits in [the earlier ledger](m0-integration-ledger.md) are unchanged; this
checkpoint does not promote a real agent adapter.

## Commands and results

From `spikes/m0-durable-core/`, with the main checkout's isolated .NET 11 SDK
selected for these commands only:

```bash
DOTNET="$(git rev-parse --path-format=absolute --git-common-dir)/../.tools/dotnet11/dotnet" ./scripts/verify.sh
DOTNET="$(git rev-parse --path-format=absolute --git-common-dir)/../.tools/dotnet11/dotnet" ATF_DEMO_BIN="$PWD/artifacts/linux-x64/atf" ./scripts/demo.sh
```

SDK `11.0.100-rc.1.26425.128`. Combined `verify.sh`: restore, format
verification, Release build with warnings as errors (zero warnings/errors),
**61/61** tests, Linux x64 Native AOT publish, and **19/19** published native
process scenarios passed. `demo.sh` with that published AOT binary passed its
single bridge-death and fresh-client recovery scenario (**1/1**). The tested
native `atf` SHA-256 was
`9dcc699a4ff99ebf277970003cb9d68c3ec2c256afe1d1466287d8bc9d2bfba1`.
The ignored local artifacts and logs are not committed; the hash identifies
this build without claiming reproducible bytes.

The exact F1 source separately passed format, zero-warning Release build,
**50/50** tests, AOT publish and **19/19** published scenarios, as recorded in
its review. The independent IPC and demo reviews retain their own exact-source
gate evidence. `bash -n scripts/demo.sh` and `git diff --check` passed after
integration. The .NET 10 legacy suite was not rerun for these merges because
its source was unchanged; its earlier 149/149 integrated result is recorded in
the prior ledger.

## Boundary

This is a runnable Linux fake-backend checkpoint. It does not establish real
Codex/Claude/Pi adapter execution, interactive Herdr operation, native wake,
Windows/macOS behavior, power-loss recovery, or a full product phase exit.
Same-key retry after restart remains the recovery path for an admission reply
lost around daemon halt. The late-start and post-commit response qualifications
in the [core review](m0-admission-fence-code-review.md) and the strict full-call
deadline qualifications in the [IPC review](m0-client-deadlines-review.md)
remain open. The separate job-inspection, probe, web and legacy-teardown lanes
are not prerequisites for this bounded checkpoint.
