# Contributing to AgentTeamForge

Thanks for your interest. This page covers building from source, the test
gates, the repository layout and how changes move from plan to merge.
[AGENTS.md](AGENTS.md) is the authoritative source for project principles and
review policy; read it before your first change.

## Toolchain

AgentTeamForge targets **.NET 11**. The SDK is pinned in
[global.json](global.json) (`11.0.100-rc.1.26425.128`, no roll-forward), so a
different SDK on PATH will not build it.

The scripts look for a project-local SDK at `.tools/dotnet11/dotnet` in the main
checkout (found through Git's common directory, so linked worktrees share it)
and fall back to `dotnet` on PATH. Override with `DOTNET=/path/to/dotnet`. The
scripts never install an SDK or change your PATH. Native AOT publishing also
needs the platform's native compiler and linker, and restore may need network
access. The reasons for .NET 11 and AOT are in
[ADR 0001](docs/adr/0001-dotnet-11-native-aot.md).

To build the apphost directly:

```bash
DOTNET=.tools/dotnet11/dotnet
export DOTNET_ROOT="$(dirname "$(realpath "$DOTNET")")"
"$DOTNET" build src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -c Release
src/AgentTeamForge.Host/bin/Release/net11.0/atf --version
```

## Build, test and verify

```bash
./scripts/verify.sh   # restore, format check, warnings-as-errors build, tests,
                      # Native AOT publish and published-binary smoke scenarios
./scripts/demo.sh     # one fake-backend scenario: MCP submit, bridge death,
                      # result retrieval, idempotent replay, daemon restart
```

`verify.sh` publishes the AOT binary to a new, unique directory and prints its
path. Use that path rather than guessing one, for example
`ATF_DEMO_BIN=/printed/path/atf ./scripts/demo.sh`.

Other scripts:

- `scripts/demo-real.sh [claude|codex|pi|fake]` — opt-in end-to-end run against
  a real agent CLI. It spends tokens; `fake` is a plumbing dry run.
- `scripts/demo-web.sh` — start a demo daemon with the web console.
- `scripts/release-build.sh VERSION [OUTPUT_DIR]` — build a local Native AOT
  release bundle (linux-x64 or osx-arm64). See [Releases](#releases).

To publish a Native AOT apphost by hand:

```bash
DOTNET="$(realpath "$(git rev-parse --path-format=absolute --git-common-dir)/../.tools/dotnet11/dotnet")"
export DOTNET_ROOT="$(dirname "$DOTNET")"
"$DOTNET" publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj \
  -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o artifacts/dev
```

### Releases

A `vX.Y.Z` tag runs `.github/workflows/release.yml`. The version in the
package and in `atf --version` comes from the tag.

- `scripts/release-build.sh` publishes Native AOT on the matching host and
  produces `atf-VERSION-<rid>.tar.gz` plus `SHA256SUMS`
  (`SHA256SUMS-osx-arm64` on macOS), then runs the extracted binary. Linux also
  runs the published scenario smoke.
- A `windows-latest` job publishes `atf-VERSION-win-x64.zip` with
  `SHA256SUMS-win-x64` and separate debug symbols. Windows AOT cannot be
  cross-compiled from Linux.
- Bundles contain the native executable and its native libraries (including
  SQLite); no .NET runtime is needed on the target. Linux builds on Ubuntu
  24.04 (glibc 2.39) and links only `libc`, `libm` and the loader.
- The workflow uploads artifacts; publishing the GitHub release is a separate,
  owner-approved step. A CI build is not platform validation (see
  [platform status](docs/platform-status.md)).
- `install.sh` installs payloads into
  `~/.local/share/agentteamforge/releases/VERSION` and links
  `~/.local/bin/atf`. Local test install:
  `sh install.sh --archive atf-VERSION-<rid>.tar.gz --checksum SHA256SUMS`.

### Testing against a running daemon safely

Never run `atf setup` against your real home directory from a development
build. Use an isolated state directory instead:

```bash
atf init --state-dir /tmp/atf-dev          # --backends fake for a fake-only profile
atf start --state-dir /tmp/atf-dev
atf client submit --state-dir /tmp/atf-dev --backend claude --key k1 --instruction "..."
atf stop --state-dir /tmp/atf-dev
```

`atf setup` refuses to register a temporary or worktree binary or state path
without `--force`; only use `--force` with an isolated `HOME`. Keep runtime
databases, session state, credentials and raw evidence out of commits.

## Repository layout

```text
AgentTeamForge.slnx
src/
  AgentTeamForge.Host/       CLI, setup, MCP bridge, IPC, daemon lifecycle, web console
  AgentTeamForge.Business/   job logic, backends (Claude/Codex/Pi), terminals, wake
  AgentTeamForge.DAL/        SQLite schema, migrations, persistence
  AgentTeamForge.Tests/      unit and scenario tests
extensions/pi-wake/          Pi extension for native wake notices
scripts/                     verify, demo and release scripts
install.sh                   release installer
docs/                        user docs, architecture, data model, ADRs
```

The dependency direction is **Host → Business → DAL**. Code is organized by
feature (vertical slices), not by technical layer: Business uses DAL directly,
with no repository ports or handler/validator/mapper chains. Add an interface
only when there is a real substitution. See [architecture](docs/architecture.md).

## Principles

The short version of [AGENTS.md](AGENTS.md):

- Keep it simple and ship working code; improve it afterwards.
- Linux first and complete. Don't block Linux on other platforms, and don't
  claim platform support that hasn't been tested on that platform.
- Agents run unattended with permissions bypassed; ask a human only for
  destructive actions.
- Runtime code and tests are C#/.NET; small shell glue is fine. Other languages
  need owner approval.
- A few focused tests on critical behavior (job state, durability,
  cancellation, permissions). No tests that assert on prose or formatting.
- Documentation is in English.

## How changes are made

Most work is done by agent teams coordinated with AgentTeamForge itself (and
its predecessor, win-agent-teams):

- A Claude Code orchestrator splits work into small slices and does no
  hands-on work.
- **Planning and research:** Pi at tier `max`. A short slice list is enough;
  only risky changes (data loss, security) get a one-round plan review.
- **Implementation:** Claude Code (Opus), one fresh worker per slice on its own
  branch and git worktree, test-first where it matters.
- **Review:** one review per slice by the opposite model family (Claude code is
  reviewed by Codex; GPT code by Claude), focused on real bugs.
- **Integration:** a Codex integrator merges reviewed slices and runs the full
  build and tests. It may fix mechanical conflicts; semantic fixes go back to a
  writer. Conflicts are never resolved by dropping tests.

Human contributors follow the same shape: a branch per change, `verify.sh`
green, and for runtime changes, one run of the published binary before merge.
Report failures honestly.

## Documentation map

| Document | Contents |
| --- | --- |
| [AGENTS.md](AGENTS.md) | Contributor and agent instructions (authoritative). |
| [Install](docs/install.md) | Install, upgrade, uninstall, login autostart. |
| [Usage](docs/usage.md) | Setup, MCP tools, CLI, wake, external members, state and pruning. |
| [Launch modes](docs/terminal-modes.md) | Interactive vs headless; Herdr, Windows Terminal, macOS. |
| [Web console](docs/web-console.md) | Operator console, model tiers, security. |
| [Migrating from win-agent-teams](docs/migrating-from-win-agent-teams.md) | Tool-by-tool mapping and parity gaps. |
| [PRFactory connector](docs/prfactory-connector.md) | Opt-in connector to PRFactory. |
| [Architecture](docs/architecture.md) | Processes, projects, job lifecycle, crash guarantees, backends, wake. |
| [Data model](docs/data-model.md) | SQLite schema and transaction rules. |
| [ADRs](docs/adr/README.md) | Architecture decisions, including dropped scope. |
| [Platform status](docs/platform-status.md) | What is tested on Linux, Windows and macOS. |

Older plans, spikes, reviews and reports were removed from the tree; git
history keeps them.
