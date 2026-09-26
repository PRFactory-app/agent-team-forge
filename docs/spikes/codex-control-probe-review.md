# Independent Codex control-probe review

2026-09-26. Reviewer: Codex/GPT, separate from the Claude writer. This review is
bound to source commit `73dab8d465d7884f8d68c94f38b62303e0b26576`
(`c7250a7` runtime plus the README command fix), against base
`0dbf44bd4cd9ab3c176f60a8df52ae47fe52ab06`. I read the actual diff,
probe README/report, C# source, tests and fixtures, and the upstream
`codex-native-control-verification.md` and `m0-safety-lanes-review.md` evidence.

**Verdict: changes required before accepting this as a safe history/classifier
primitive.** The fake-only start-or-steer demonstration and strict refusal are
useful bounded evidence. This is not approval of a Codex adapter, native
behavior, visible-TUI/thread binding, or strict human-wins dispatch. The
upstream finding remains: `turn/start` can steer a human turn after an idle
read, and this API has no atomic idle-only start precondition.

## Findings

**P1 — High; blocks treating history as complete safety evidence.**
`spikes/codex-control-probe/src/CodexControlProbe/History/HistoryPageValidator.cs:63-70`
accepts any item with a nonempty `id` and `type`; it checks only `clientId`
for `userMessage` and skips every other item type. For example, remove the
required `content` from a `userMessage` or `text` from an `agentMessage` in a
valid full-page fixture. The vendored `ThreadTurnsListResponse` schema rejects
either page, while this parser returns `Ok`. `HistoryTraversal` then marks the
aggregate `Complete`, and the classifier can use it to release a fence. The
fixture tests validate their fixed files against the schema but do not put
schema validation in the traversal path. This repeats the material #3 gap
identified in the earlier safety-lane review. Add schema-derived negative
fixtures for required known-item fields and make the actual page validation
fail closed for them before using `Complete` as safety evidence. Unknown item
variants also need an explicit conservative policy. The response has no page
`threadId`; do not add a fabricated requirement for one.

**P2 — Medium; classifier result and report overstate a clean outcome.**
`Dispatch/Dispatch.cs:74-86` sets `PauseRequired=true` when another turn appeared
after preflight, but still returns `OwnCompleted` with `FenceRetained=false` for
the returned turn. The existing
`Foreign_turn_completed_between_preflight_and_send_requires_pause` test
(`InterleavingTests.cs:90-103`) demonstrates that exact outcome. A failed or
interrupted returned turn also releases the fence through the final switch.
Consequently `REPORT.md`'s statement that only the clean idle-to-own-completion
path releases the fence is false. Keep the fence when concurrent history is
unresolved, or define and test a caller contract that makes pause authoritative
before any later admission/retry; then state precisely which terminal outcomes
may release it. `clientId` is caller supplied and provides correlation only;
the current two-message forged-ID test does not establish authenticated origin.

**P3 — Medium; unavailable evidence is reported as no pause required.**
`Dispatch/Dispatch.cs:89` returns `EvidenceUnavailable` with
`FenceRetained=true` but `PauseRequired=false`, including when post-send
pagination or the start response is malformed. There is no consumer in this
probe, so this is a contract risk rather than a demonstrated external effect.
It conflicts with the upstream rule that incomplete history cannot clear a
pause or authorize dispatch. Make the unknown state conservative in both
signals, or specify and test an invariant that retained fences always block
further action regardless of the pause flag.

**Qualification — traversal bounds are partial.** `HistoryBounds` and
`HistoryTraversal.cs:100-125` cap pages and turns, and detect cursor loops,
duplicates and later-page errors. They do not cap bytes or elapsed time; a
single large page can consume unbounded work, and `fetchPage` can wait without
a deadline. The upstream #3 recommendation includes page, byte, turn and time
bounds plus stable thread/direction/view and JSON-RPC correlation. This probe
has no transport and explicitly cannot prove those caller-side properties.
Keep “bounded” scoped to pages/turns until a future adapter supplies the other
bounds and request checks. Pagination across live mutable snapshots also does
not prove a stable complete history.

## Checks and limits

On Linux x86_64 with the local .NET 11 RC1 SDK
`11.0.100-rc.1.26425.128`, all commands used command-scoped `DOTNET_ROOT`
and `PATH`, a 120-second timeout each, and the repository's local-cache-only
NuGet configuration. No network source was configured.

| Check | Result |
| --- | --- |
| `dotnet restore CodexControlProbe.slnx --configfile NuGet.config` | Pass, local cache only |
| `dotnet format CodexControlProbe.slnx --verify-no-changes --no-restore` | Pass |
| `dotnet build CodexControlProbe.slnx -c Release --no-restore -warnaserror` | Pass, 0 warnings / 0 errors |
| `dotnet test CodexControlProbe.slnx -c Release --no-restore --no-build` | Pass, 34/34 |
| `sha256sum -c` for the seven vendored schemas; `git diff --check` | Pass |

The test pass confirms the implemented fake scenarios, not native behavior or
the missing negative cases above. No real model, Codex app-server/TUI, Herdr,
service, credential or retained session was accessed. No live conformance run,
published-binary runtime check, Native AOT, or Windows/macOS test was run.
Visible-TUI/thread association and strict human-wins remain open gates. No
production or test source was edited in this review.
