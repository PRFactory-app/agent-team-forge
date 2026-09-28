# 0001. .NET 11 with Native AOT

Status: accepted

## Context

The project is maintained by a .NET team, while the reference tool
(win-agent-teams) is Python. The daemon, MCP bridge and CLI should ship as
one self-contained binary without a runtime install, and .NET 11 adds process
APIs (detached start, handle inheritance control, `SafeProcessHandle`
lifecycle) that fit a process supervisor.

## Decision

- Target .NET 11. The SDK is pinned in `global.json`
  (`11.0.100-rc.1.26425.128`, no roll-forward) and kept project-local in
  `.tools/dotnet11/`.
- Publish the Host as a Native AOT apphost (`atf`). Use explicit service
  registration and source-generated JSON so trimming stays safe.
- Measure AOT size, startup and memory; do not claim savings without numbers.
- Adopt new .NET 11 APIs only in narrow slices with tests. Keep the Linux
  pidfd-based process identity (`Pidfd.cs`): the RC's `SafeProcessHandle` is
  not a pidfd.
- C# 15 unions compile without a preview flag and may be used internally; the
  IPC wire format stays on plain source-generated DTOs.

## Consequences

- Release bundles need a native build host per platform: Linux x64 and macOS
  arm64 from `scripts/release-build.sh`, Windows x64 from the CI Windows job.
  There is no cross-compilation.
- Each bundle carries its native SQLite library; the target machine needs no
  .NET runtime.
- The pinned RC SDK must be upgraded deliberately when .NET 11 ships.
