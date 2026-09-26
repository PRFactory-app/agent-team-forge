# D2 Herdr launch characterization — independent code review

2026-09-26. Reviewer: Codex (opposite model family from the Claude author).
Reviewed immutable `fc5e37f` against `2d6d0c9` on `review/char-d2`.

**Verdict: approve the bounded, test-only D2 characterization. Blockers: 0.**
This is not approval of a production Herdr adapter, live session teardown, or
platform support.

## Findings and evidence

- The diff adds only the two D2-owned test files and its provenance report. No
  production code, project file, or launch script changed. The fixture constructs
  `ProcessStartInfo` values; neither file calls `Process.Start` or launches Herdr,
  a terminal, or a shell.
- Provenance matches the reviewed sources at `6e06de7`: `LaunchEnvironment.cs`
  blob `2d6435e`, `HerdrCli.cs` blob `49e58a4`, and `HerdrOwnership.cs` blob
  `97cca49`. The first two blobs are unchanged from `78f1e06` and `8a5e385`;
  the ownership blob is the stopped-session repair reviewed in
  `docs/spikes/m0-safety-lanes-review.md`. The earlier independent
  `spikes/m0-interactive/RE-REVIEW.md` covers argument-list launch,
  environment filtering, and listed-name refusal. The fixture's deviations
  are explicit and appropriate for an offline test: injected environment,
  extracted start-info construction, and a reduced ownership record. The
  copied allowlists, fixed server script, existing-name check, and teardown
  decision branches match the source. `fc5e37f` descends from `2d6d0c9` only;
  `6e06de7` and legacy integration `cd60824` are not ancestors.
- The tests invoke that copied logic and assert observable vectors and
  decisions. Unicode and shell metacharacters remain single argument-list
  elements; a hostile session name is passed as `$0` to a fixed script.
  Targeted, global, and server environments drop credential/session sentinels,
  including explicitly opted-in forbidden prefixes; the targeted socket is
  reintroduced only for its owned target. Running and stopped listed names
  refuse creation; missing ownership, missing session, identity mismatch,
  missing label, and stopped session refuse teardown. These are not assertions
  on fabricated expected values alone.

No code-review findings require a fix for D2. The fake inputs and supplied
`serverIdentityMatches` boolean do not prove native process identity,
replacement-safe stop/delete, concurrent creation safety, or real terminal
behavior. D7 must address those separately; the source reviews already record
the teardown replacement race.

## Reproduced gates

Linux x86_64, repository SDK `11.0.100-rc.1.26425.128`, with
`DOTNET_ROOT=/home/mikael/code/github/agent-team-forge/.tools/dotnet11` and
`/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`:

| Root command | Result |
| --- | --- |
| `dotnet restore AgentTeamForge.slnx` | Pass |
| `dotnet format AgentTeamForge.slnx --verify-no-changes --no-restore` | Pass |
| `dotnet build AgentTeamForge.slnx -c Release --no-restore -warnaserror` | Pass, 0 warnings/errors |
| `dotnet test AgentTeamForge.slnx -c Release --no-restore --no-build` | Pass, 65/65; 0 failed/skipped |

Published binary, AOT, live Herdr, and other-platform checks were not run: this
slice adds test-only offline characterization and claims none of those behaviors.
