# M0 integration ledger

This records the earlier safety-lane baseline. The current reviewed runnable
fake-core checkpoint is in [the fake-core integration ledger](m0-fake-core-integration.md).

## Readiness and approval boundary

Recorded 2026-09-26 on Linux x86_64, in the dedicated `integration/m0-e2e`
worktree. Three reviewed interactive primitive-fix lanes have been merged with
their source ancestry preserved. The durable core remains unmerged after a
changes-required code review. The baseline is an
**unapproved legacy source snapshot for diffs**, not an approved checkpoint.
Passing its existing tests does not close the safety review findings.

This ledger follows [the integration plan](m0-integration-plan.md),
[the architecture](../architecture.md), and the contributor review policy in
[AGENTS.md](../../AGENTS.md). The parent coordinates writers and the separate
`m0-codex-verifier`; the integrator does not author feature code or self-approve.

## Input register

Branch tips below are the initial observed snapshots, not approval or instructions
to merge moving branch heads. Updates require the parent-supplied exact commit,
review disposition bound to that commit, affected files, test evidence and blockers.

| Input | Initial commit | Status |
| --- | --- | --- |
| Integration baseline | `78f1e06d5df293615a5e166476e48d773e93943d` | Unapproved; retained only for review/diffs. |
| `spike/m0-codex-safety` | `6e06de771a49a0fdbbf13581ee957436480d0e8e` | Merged as bounded #1/#2 and malformed-page primitive fixes only; full #3, name-race teardown, #5/#7 remain blocked. |
| `spike/m0-transport-bounds` | `8f7b6344380d9d5938cad798b0d44f241d861ff8` | Merged as bounded transport/CLI primitive only; caller-wide deadline remains open. |
| `spike/m0-claude-isolation` | `8a5e385dd11ca4105c145c452e990c823eaf2654` | Merged as bounded managed-path disablement/hook-cap hardening; Claude native adapter remains unsupported. |
| `spike/m0-durable-core` | `55d3c054acf072f7d3da49c5f9bd20d9c3860058` | Independently reviewed; B1/B2 changes required, unmerged. See [code review](m0-durable-core-code-review.md). |
| `spike/m0-core-fault-fix` | `1d3c409514dc30a774376d7cdc124a6069c843c4` | B2 substantially repaired; [re-review](m0-core-fault-fix-code-review.md) requires a halt/admission fence for B1. Unmerged. |
| `verify/m0-e2e` | `67c2cd92e595a32842a1482cba590d317a27f89f` | Separate [pinned safety-lane review](m0-safety-lanes-review.md); approved bounded primitives, not full adapters. |

The durable lane starts from an older ancestor. Preserve its ancestry when merging;
do not cherry-pick or treat the inherited legacy snapshot as newly approved code.

## Source manifest and solution composition

Git object identities bind the entire baseline source manifest without copying
source or raw evidence into this document:

| Baseline object | Git object ID |
| --- | --- |
| Repository tree | `70251551d10564c88c4c7e8ca792829ded3b2ba9` |
| `spikes/m0-interactive/` tree | `908c0c52f4808e3205571f6f6a4f57439840a324` |
| `spikes/m0-interactive/src/` tree | `eb0e1c04074ec01056fefe122f9726b5f938586c` |
| `spikes/m0-interactive/tests/` tree | `74ceb59e56db29e46934d38524645efd20bfa68b` |
| Legacy `AtfSpike.slnx` blob | `a2df7fc51ee89214cd7497c653078a25c5bbc513` |
| Legacy `Directory.Build.props` blob | `e5dfdbc66947026f798a6890e2f2ee9a514aeabd` |
| Legacy `scripts/gates.sh` blob | `b07310c6826aee2286d1c2014cd7f19643ca3a16` |

Reproduce the complete per-file manifest with:

```bash
git ls-tree -r 78f1e06d5df293615a5e166476e48d773e93943d -- spikes/m0-interactive
```

The current solution contains `src/AtfSpike/AtfSpike.csproj` and
`tests/AtfSpike.Tests/AtfSpike.Tests.csproj`, targeting `net10.0`. It has one
experiment executable, not the required product project structure. No tracked
`AgentTeamForge.slnx`, durable-core runtime or .NET 11 pin exists in this baseline.

The new core must arrive as `spikes/m0-durable-core/AgentTeamForge.slnx`, with
exactly three runtime projects: `AgentTeamForge.Host`, `AgentTeamForge.Business`,
and `AgentTeamForge.DAL`, plus tests. Verify Host → Business → DAL references,
DAL's lack of upward references, and any Host → DAL use confined to composition.
Bridge/client endpoints must delegate through Business and must not open the job
database. Require the .NET 11 RC1 pin and `net11.0`; do not retarget the old
experiment to obtain a combined green result.

## Reproducible integration gates

Toolchains verified locally: legacy SDK `10.0.401` at the existing .NET 10
installation; new SDK `11.0.100-rc.1.26425.128` at
`<main-checkout>/.tools/dotnet11`. Use command-scoped environment only. No
installation or global environment change is authorized here.

From the integration worktree root, define shell helpers for explicit selection:

```bash
DOTNET10=/absolute/path/to/existing/dotnet10/dotnet
main_checkout="$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)"
DOTNET11="$main_checkout/.tools/dotnet11/dotnet"
dotnet10() { env DOTNET_ROOT="$(dirname "$DOTNET10")" \
  PATH="$(dirname "$DOTNET10"):$PATH" "$DOTNET10" "$@"; }
dotnet11() {
  env DOTNET_ROOT="$(dirname "$DOTNET11")" \
    PATH="$(dirname "$DOTNET11"):$PATH" "$DOTNET11" "$@"
}
```

After each merge, run the complete legacy offline gates while that experiment
remains present (from `spikes/m0-interactive/`):

```bash
dotnet10 format AtfSpike.slnx --verify-no-changes
dotnet10 build AtfSpike.slnx -c Release -warnaserror
dotnet10 test AtfSpike.slnx -c Release --no-build
```

Once the reviewed core exists, additionally run from `spikes/m0-durable-core/`:

```bash
dotnet11 --version
dotnet11 sln AgentTeamForge.slnx list
dotnet11 restore AgentTeamForge.slnx
dotnet11 format AgentTeamForge.slnx --verify-no-changes
dotnet11 build AgentTeamForge.slnx -c Release --no-restore -warnaserror
dotnet11 test AgentTeamForge.slnx -c Release --no-build
dotnet11 publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -p:TreatWarningsAsErrors=true -o artifacts/linux-x64
```

Run the lane's reviewed published scenario wrapper against
`artifacts/linux-x64/AgentTeamForge.Host` using the .NET 11 command environment.
Inspect the supplied wrapper before execution: orchestration must remain in C#,
with private temporary state, fake children, bounded waits and held-handle cleanup
of only its own processes. Required published scenarios include MCP
initialize/list/call, durable acceptance, client death with fresh-client result
retrieval, idempotent retry, and conservative uncertain-attempt recovery.
An AOT publish alone cannot establish those behaviors. If AOT is blocked, report
the checkpoint as JIT-only and leave AOT open. Repeat scenarios after relevant
combined runtime changes; no real model calls or existing-session smoke here.

Finally run `git diff --check`, record the integration commit and source tree
identities, and report failed, blocked and unrun gates individually. There is no
repository-wide lint script at baseline; format and build analyzers are the
available code checks. Do not filter tests, weaken ownership checks or suppress
broad diagnostics to pass integration.

## Gate results

| Snapshot / gate | Result |
| --- | --- |
| Baseline legacy format verification (.NET 10.0.401) | Passed. |
| Baseline legacy Release build with warnings as errors | Passed; 0 warnings, 0 errors. |
| Baseline legacy full test suite | Passed; 103 passed, 0 failed, 0 skipped. |
| Baseline working tree before ledger | Clean. |
| Safety merge `40f7cef63916cfdfb018f0daf2c2b840ee616f19` | Format/build passed; 130/130 tests passed. |
| Transport merge `c93a35f9d37de7b79a70dcfb720d28723f462ead` | Format/build passed; 144/144 tests passed. |
| Claude isolation merge `ffa8d1e371098cd90e45bb2e5dfbb0c530713f80` | Format/build passed; 149/149 tests passed; framework-dependent JIT publish and published `--help` passed. |
| Focused integrated negative-test selection | 45/45 passed (history envelope, launch intent, transport bounds, Herdr CLI bounds, Claude capability gate and hook runner). The independent verifier's separate selection passed 54/54. |
| Durable core `55d3c054acf072f7d3da49c5f9bd20d9c3860058` in author worktree | Independent .NET 11 format/build/37 tests/native AOT/13 scenarios passed; code review **changes required**, unmerged. |
| Core fault fix `1d3c409514dc30a774376d7cdc124a6069c843c4` in author worktree | Independent .NET 11 format/build/47 tests/native AOT/19 scenarios passed; re-review **changes required**, unmerged. |
| New core composition / .NET 11 build / tests / publish | Not run: reviewed core not yet supplied or merged. |
| Combined published failure scenarios | Not run: reviewed core not yet supplied or merged. |
| Real Codex/Herdr or Claude runtime qualification | Not run; safety review and separately authorized resources required. |
| Windows/macOS/Pi/native wake qualification | Not run; remains outside this bounded Linux fake-core checkpoint. |

Only offline temporary test resources were used. No raw evidence, live session,
model call, system install, main merge, push or remote change was performed.

## Merge and conflict register

Three source commits merged in safety → transport → Claude isolation order, each
with a two-parent merge commit listed above. No conflicts or GPT-authored
executable resolution occurred. Changed files stayed within the assigned
interactive lanes. The review [verdict](m0-safety-lanes-review.md) and
[native protocol qualification](codex-native-control-verification.md) bind the
capability limits. Full Codex adapter dispatch remains blocked by incomplete
history schema validation, unsafe name-based teardown race, native visible
TUI/thread association, the idle-to-start human race, and caller-wide deadlines.
The reviewed `turn/start` API can steer an already active human turn; local
preflight cannot establish strict human-wins. Claude managed send/cancel remain
disabled and its native adapter unsupported. The fake core's B2 deadline path is
substantially repaired, but a concurrent submit can still be acknowledged after
the dispatcher halts and before the host closes admission. It remains unmerged
pending a Claude-authored fix and Codex re-review.

Mechanical conflicts may be resolved without changing behavior. Under the
current user instruction, Codex does not author executable conflict fixes;
semantic runtime conflicts return to Claude writers, then Codex re-reviews the
exact repair. Passing combined gates is not review approval; unapproved inherited
source stays excluded from promoted capability claims.

## Consolidation and retirement gate

After reviewed equivalent behavior and meaningful regression tests exist in the
real solution, plan a bounded consolidation slice with a source-to-test mapping,
independent review, then remove the superseded legacy project and temporary
helpers/scripts. Do not rename the current experiment to imply replacement or
keep duplicate runtimes indefinitely. Preserve concise decisions/results and
unique recovery evidence; file retirement never authorizes terminating sessions.
The separate adapter-integration slice is still required before promoting real
agent execution through the durable core. Fake-core success cannot establish it.
