# Independent M0 interactive safety-lane code review

2026-09-26. Reviewer: GPT/Codex, separate from the Claude authors. This reviews
three immutable feature snapshots, each diffed against source baseline
`78f1e06d5df293615a5e166476e48d773e93943d`, plus their combined local
cherry-pick. It is **not** approval of either full interactive adapter or a
moving branch. Review basis: `spikes/m0-interactive/RE-REVIEW.md`,
`docs/spikes/codex-native-control-verification.md`, and the actual source/tests.

| Lane / source hash | Verdict for this commit | Scope |
| --- | --- | --- |
| Codex safety `6e06de771a49a0fdbbf13581ee957436480d0e8e` | **Approve bounded #1/#2 repairs and malformed-page fail-closed primitive; changes required before #3 or full adapter closure.** | Stopped-session refusal, atomic launch-intent persistence and basic history-page validation are sound in reviewed paths. Findings S1–S2 below remain. #5/#7 remain no-go. |
| Transport bounds `8f7b6344380d9d5938cad798b0d44f241d861ff8` | **Approve bounded transport/CLI primitive.** | Per-request lock/write/response deadline, atomic quota and captured-output caps passed the fake tests. Finding T1 is an integration gate, not a reason to drop this independent fix. |
| Claude isolation `8a5e385dd11ca4105c145c452e990c823eaf2654` | **Approve bounded disabling of unsafe managed paths and hook-cap hardening.** | #4 is closed by refusing managed send/cancel before effects and downgrading old hook evidence. #8 is improved, with C1 test/guarantee limits. Claude native adapter remains unsupported. |

The local composite on `verify/m0-e2e` contains those commits in the table's
order as cherry-picks `32ca6cf`, `60e54f7`, `81d3d46`. Cherry-pick hashes are
verification-tree identities; source hashes above identify authors' exact
work. There were no conflicts or GPT-authored executable resolutions. No push,
main/integration merge or feature-branch change was made.

## Findings requiring follow-up

**S1 — High, blocks safe automated teardown; Codex safety.**
`spikes/m0-interactive/src/AtfSpike/Launch.cs:149-175` proves a running server
and owner-labelled workspace, then calls global `session stop` and `session
delete` by reusable name. The new refusal in
`src/AtfSpike/Herdr/HerdrOwnership.cs:34-40` correctly prevents deleting a
session *already observed stopped*. It does not protect a replacement created
after the ownership check or between stop and delete. The author's
`CODEX-SAFETY-FIXES.md` records this residual. Do not claim teardown safe or
exercise it on live sessions. A fresh Claude slice should either establish a
provider identity-bound stop/delete contract or refuse automatic deletion when
provenance cannot be carried through the operation; fake provider tests should
place a replacement at each boundary. The narrow stopped-session repair can
merge independently.

**S2 — Medium, #3 not fully closed; Codex safety.**
`src/AtfSpike/Codex/CodexThreadEvidence.cs:44-50` calls a page complete when
each item has any nonempty `type`; for `userMessage` it checks only `id` and
`clientId`. The generated 0.157.1 schema also requires `content` for
`userMessage`, `id`/`text` for `agentMessage`, and required fields for other
known item variants. A successful page with a recognized user-message ID but
missing/mistyped content is accepted as `Available`; tests at
`tests/AtfSpike.Tests/CodexHistoryEnvelopeTests.cs:29-47` exercise missing
turn fields and client ID but not missing required item content or malformed
agent/unknown item variants. This contradicts the new parser's claim to reject
any partial element. Add focused schema-derived negative cases and fail closed
for required fields used in the selected `full` view. Do not require a page
`threadId`: 0.157.1 has none. Separately, the local `ProvenEmpty` inference at
`CodexThreadEvidence.cs:159-167` remains unqualified native evidence; a valid
page traversal has no atomic snapshot-to-send guarantee. This finding does not
undo the demonstrated fix for missing/non-array `data` and pagination errors.

**T1 — Medium, caller integration remains open; transport bounds.**
`src/AtfSpike/Codex/JsonRpcPeer.cs:139-164` now bounds each request from lock
wait through response. `src/AtfSpike/CodexCommands.cs:321-371` still runs
binding, history, subscribe, start and optional wait as separate requests with
no shared operation deadline; `--timeout` governs the later wait loop, not
the entire dispatch. A sequence of individually bounded requests can exceed
the intended operation budget. The new timeout classification is conservative
at the unchanged caller (`CodexCommands.cs:360-364`), so it does not make an
uncertain write an unsent retry. Fresh Claude caller work should propagate one
deadline and test stalled preflight/subscribe plus possible-write uncertainty.
`UdsWebSocketTransport.cs:29-63` has a 10 s linked cancellation bound, but an
uncooperative WebSocket operation is not independently bounded with `WaitAsync`;
the new fake handshake test proves the cooperative cancellation path only.
Keep the stronger “even if cancellation is ignored” claim scoped to peer writes.

**C1 — Low/qualification; Claude isolation.**
`src/AtfSpike/Claude/ClaudeHookRunner.cs:47-69,82-105` serializes cooperating
writers around size check/append, and the marker is private/bounded. The new
`ClaudeHookRunnerTests.cs:56-77` runs 64 parallel calls in one process, while
the original #8 concern is multiple hook processes. A two-process cap test is
still needed before claiming that specific cross-process property; the
file-lock design is plausible on the tested Linux runtime. Marker creation
failure and power-loss replay remain qualified: a nonzero hook exit is not a
durable evidence record. Because managed Claude dispatch is disabled, this is
not a reason to hold the isolation fix.

## Reproduced checks

Linux x86_64, .NET SDK 10.0.401. The existing baseline assets came from a
local-cache-only restore in this worktree. Commands ran in
`spikes/m0-interactive/`; no network restore or SDK installation.

| Source tree after local cherry-pick | `dotnet format AtfSpike.slnx --verify-no-changes --no-restore --verbosity quiet` | `dotnet build AtfSpike.slnx -c Release --no-restore -warnaserror --verbosity quiet` | `dotnet test AtfSpike.slnx -c Release --no-restore --no-build --verbosity quiet` |
| --- | --- | --- | --- |
| Safety only `32ca6cf` | Pass | Pass, 0 warnings | 130/130 pass |
| Safety + transport `60e54f7` | Pass | Pass, 0 warnings | 144/144 pass |
| All three `81d3d46` | Pass | Pass, 0 warnings | 149/149 pass |

On the three-lane composite, a filter for the new/history/ownership/transport/
Claude negative-test classes passed **54/54**. A framework-dependent Release
publish passed; published `AtfSpike.dll --help` exited 0 and showed Claude
dispatch/cancel disabled. That is an entry-point smoke, not a published runtime
dispatch or AOT test. The base suite was 103/103; no conclusion is drawn from
test count alone.

No live Codex/Claude turns, Herdr/service calls, teardown, credentials, native
TUI-client binding, real human idle race, AOT, .NET 11 or Windows/macOS tests
were run. #5 (visible TUI/thread association) and #7 (native idle-to-start
human-wins fence) remain blocked by the inspected 0.157.1 public contract;
`turn/start` can steer an active turn. The reviewed fixes must not be described
as a complete Codex adapter. Fake-core S1–S4 has a separate review/gate and
does not depend on resolving these live-adapter contracts.
