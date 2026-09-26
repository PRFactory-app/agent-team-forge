# D2 — Herdr launch characterization (test-only)

2026-09-26. Claude implementation worker; **not reviewed** — needs opposite-family
(Codex) code review. Base `2d6d0c9`, branch `feature/char-d2-herdr-launch`.
No shell, Herdr or terminal was launched; no production or project file changed.

## Files

- `tests/AgentTeamForge.Tests/Support/ManagedDemo/HerdrLaunchFixture.cs`
- `tests/AgentTeamForge.Tests/Features/Agents/Terminals/HerdrLaunchCharacterizationTests.cs`

## Provenance

Source: `spikes/m0-interactive/src/AtfSpike/Herdr/{LaunchEnvironment,HerdrCli,HerdrOwnership}.cs`
at `6e06de7` (spike/m0-codex-safety).

- `LaunchEnvironment.cs`, `HerdrCli.cs`: blobs identical at seed `78f1e06`,
  `8a5e385` (claude-isolation) and `6e06de7`, and to the untracked main
  working tree (read only). Reviewed positively in the independent GPT
  re-review `spikes/m0-interactive/RE-REVIEW.md` @ `6e06de7` (argument lists,
  refusal of listed preexisting names `HerdrCli.cs:77-80`, environment
  allowlist and unforwardable agent-session prefixes `LaunchEnvironment.cs:10-40`).
- `HerdrOwnership.cs`: the `6e06de7` version with stopped-session refusal, approved
  in `docs/spikes/m0-safety-lanes-review.md` (Codex safety lane). The untracked
  main copy is the older pre-fix version and was **not** used.
- Not carried: the `8f7b634` bounded output capture (`BoundedProcess`) — it is
  process execution, outside this no-launch lane; D7 must take it from there.

Deviations from source, all mechanical: environment is seeded from an injected
dictionary instead of this process; `ProcessStartInfo` construction is split out
before `Process.Start`; `SpikeState` is reduced to an `OwnedSession` record plus
recorded name.

## Tests (red → green)

| Test | Pins |
| --- | --- |
| `ArgumentVector_PreservesUnicodeAndMetacharacters` | `herdr` argv is exact `ArgumentList`, no shell/`Arguments`; server launch passes a hostile session name only as `$0` to a fixed script. |
| `Environment_ExcludesCredentialSentinel` | Sentinel in API-key, agent-session, win-agent-teams and `HERDR_*` vars is absent for targeted, global and server launches, even when opt-in names it; targeted sets only the owned socket. |
| `ExistingUnownedSession_RefusesReuse` (running/stopped) | Listed name refuses creation; teardown refuses without record, identity, label, or when stopped. |

Red evidence: mutating `CanCreate` to always allow failed the reuse test;
dropping the `HERDR_` never-prefix failed the environment test.

## Gates

.NET SDK `11.0.100-rc.1.26425.128` (`.tools/dotnet11`), Linux x64: restore, `format
--verify-no-changes` pass; Release build `-warnaserror` 0 warnings; tests 65/65
(61 base + 4). AOT publish/published smoke not run (no runtime change).

## Limits for D7

Explicit env opt-in still forwards named non-session credentials (as reviewed).
S1 (replacement between ownership check and stop/delete) and concurrent-create
races remain open; these vectors do not close them.
