# Codex control probe: independent rereview of P1–P3 fixes

2026-09-26. Reviewer: Codex/GPT, in a separate session from the Claude author.
Reviewed source commit `5dc36318301eebe71a4122e13998de423050e8ba`
against `73dab8d465d7884f8d68c94f38b62303e0b26576`. I read the actual
diff, the earlier P1–P3 review, `REVIEW-FIXES.md`, the source, tests, fixtures,
vendored response schema, and the updated probe report.

**Verdict: P1–P3 are resolved for this fake-only probe. No new blocking finding
in the reviewed fix.** This approves the probe's bounded history/classifier
evidence only. It does not qualify a Codex adapter, native protocol behavior,
visible TUI/thread binding, or strict human-wins dispatch. The strict profile
still refuses because no atomic idle-only start is established.

## Disposition and evidence

| Finding | Rereview result |
| --- | --- |
| P1: schema-required item fields skipped in traversal | Resolved. `HistoryTraversal.Traverse` calls `HistoryPageValidator.Validate` for every fetched page. That method validates the entire result against embedded `ThreadTurnsListResponse.json` before projecting items into local records. The embedded bytes are checked against SHA-256 `02a662f5fe56fccbf7ff12765b4f8854896895d8ff5bc9971c269e5c455b5586`; a missing or changed resource refuses pages. The schema's `ThreadItem.oneOf` branches require a known `type` and its fields, so an unknown type matches no branch. Tests remove schema-required fields from the valid fixture, reject missing `userMessage.content`, missing `agentMessage.text`, and an unknown item type, and show a malformed later page invalidates the whole aggregate. The mutation test exercises the variants present in that fixture; it is not exhaustive fixture coverage of all 19 known item variants. |
| P2: release despite concurrent history or noncompleted terminal turn | Resolved for the stated outcome contract. A completed returned turn with another new turn sets both `PauseRequired` and `FenceRetained`. `interrupted` and `failed` retain the fence and require pause; `MixedUncertain` does too. An in-progress turn retains the fence. Tests cover these cases and assert release implies `OwnCompleted` with no pause, and pause implies a retained fence. Consumers must gate on those flags, since the kind can be `OwnCompleted` while a concurrent turn requires pause. |
| P3: unavailable evidence without pause | Resolved. Incomplete preflight, unusable start response, unavailable post-history, and an absent returned turn take the `EvidenceUnavailable` path, which now sets both flags. Tests directly cover malformed start response and unavailable post-history, including a completion notification; the shared classifier assertion checks the unavailable invariant. |

`clientId` remains caller-supplied correlation, not authentication. The
colliding-ID fake case is detected because of a pre-send notification; it does
not prove a forged ID can always be distinguished. The classifier's “own”
result means no contrary evidence was observed in these inputs.

## Checks

Linux x86_64, repository SDK `11.0.100-rc.1.26425.128`, with command-scoped
`DOTNET_ROOT`/`PATH`, 120-second timeouts for .NET commands, and the probe's
local-cache-only `NuGet.config`. No network source was configured.

| Check | Result |
| --- | --- |
| `dotnet restore CodexControlProbe.slnx --configfile NuGet.config` | Pass |
| `dotnet format CodexControlProbe.slnx --verify-no-changes --no-restore` | Pass |
| `dotnet build CodexControlProbe.slnx -c Release --no-restore -warnaserror` | Pass, 0 warnings / 0 errors |
| `dotnet test CodexControlProbe.slnx -c Release --no-restore --no-build` | Pass, 50/50 |
| `sha256sum -c ../SHA256SUMS` from the vendored schema `v2` directory | Pass, 7/7 |

## Remaining qualification limits

The traversal caps pages and turns, detects cursor loops and duplicate turn
IDs, and refuses a later-page error. It has no byte or elapsed-time limit and
no deadline for `fetchPage`. Its complete result describes fetched pages; live
pagination is not an atomic snapshot or a lease against later activity. This
probe has no transport, so request/response correlation and stable
thread/direction/view must be established elsewhere before safety use.

The fake models start-or-steer behavior; it cannot establish that a native
Codex app-server, visible TUI, or particular platform behaves the same way.
No real model, Codex app-server, Herdr, user credential, service, network,
published binary, Native AOT, or Windows/macOS run was used for this rereview.
No runtime or test source was edited.
