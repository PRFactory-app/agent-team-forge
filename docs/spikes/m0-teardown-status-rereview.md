# Independent rereview: M0 teardown status (T1 and T2)

## Snapshot and verdict

Reviewed Claude-authored commit **`85f3fdd18c52a3acf26177e1f47fe3e2441ae988`** atop
**`22e359859c1a1d39d45025f8d5b7ba63d9b364ea`** in the separate
`review/m0-teardown-status` worktree. The requested scope is T1 (missing structured CLI
refusal status) and T2 (outdated teardown instructions) from the independent M0 teardown
fence review, plus the unchanged S1 destructive fence. I read the exact diff, `AGENTS.md`, `HANDOFF.md`, the author's
`TEARDOWN-STATUS-REPORT.md`, the teardown and CLI paths, and the related tests.

**Verdict: approve `85f3fdd` for T1 and T2, with no blocking findings.** The CLI now emits
the refusal's structured `status` through `Program.cs` and `CliErrors`, while returning exit 1.
The current real Herdr provider still has no identity-bound destroyer, so the checked teardown
path cannot issue a global stop or delete. This approves the bounded status repair and the
previously reviewed S1 fence; it is not full interactive-adapter or live-Herdr approval.

## Findings and limits

- **T1 resolved.** `Launch.Teardown(SpikeContext)` throws `TeardownRefusedException` for every
  non-`Destroyed` outcome. It derives from `InvalidOperationException`, so the `Program.cs`
  catch accepts it. That catch emits `CliErrors.ToEvent(e)` and returns `CliErrors.ExitCode`
  (1). The helper emits `event: "error"`, preserves diagnostic `type` and `message`, and adds
  `status` from `Outcome.Status` for teardown refusals. `TeardownFenceTests` checks the exact
  fields for all three refusal statuses and checks that ordinary CLI errors have no `status`.
  Clients can branch on `status`; `message` remains prose and is not a machine contract.
- **T2 resolved.** `spikes/m0-interactive/README.md` says real-Herdr automated teardown is
  unsupported and gives the manual ownership-check and cleanup procedure.
  `spikes/m0-interactive/REPORT.md` visibly marks both earlier stop/delete claims as
  superseded. The author's status report identifies the older fence report's message-prefix
  wording as historical.
- **S1 regression check passed by inspection and fake tests.** The reviewed diff does not add
  a stop/delete call. `HerdrTeardownProvider.IdentityBoundDestroyer` remains null. After
  read-only checks, `Launch.Teardown` returns `UnsupportedNoIdentityBoundDestroy`; failed
  proof returns `RefusedUnproven`. Existing replacement-boundary tests confirm that a
  provider without the identity-bound capability receives no destructive call. A future
  provider would need its own atomic identity-bound stop-and-delete evidence.
- The tests exercise the same helper that the top-level `Program.cs` catch calls, but do not
  execute that catch on a status-bearing refusal. The published-binary probe reaches the
  earlier no-state error only. The delegation is direct and inspectable, so this limit does
  not block T1. No live Herdr refusal, real destroy, model, terminal, or service was run.

## Independent gates

Linux, command-scoped existing .NET SDK **10.0.401**, local offline package cache. Source
and test files were read only. The worktree initially lacked NuGet assets; the first
`dotnet format --no-restore` failed with missing xUnit types. An offline local-cache restore
passed, then these gates passed:

| Gate | Result |
| --- | --- |
| `dotnet format AtfSpike.slnx --verify-no-changes --no-restore` | Pass |
| `dotnet build AtfSpike.slnx -c Release --no-restore -warnaserror` | Pass; 0 warnings, 0 errors |
| `dotnet test AtfSpike.slnx -c Release --no-build --no-restore` | Pass; 162/162 |
| Framework-dependent Release `dotnet publish src/AtfSpike --no-restore` | Pass |
| Published `AtfSpike teardown` with a new empty isolated `ATF_SPIKE_HOME` | Exit 1; `error` event without `status`, as expected before ownership checks |

The independently published `AtfSpike.dll` SHA-256 was
`69be7617ca983f5351824919829454dc8bd9bf578487afc04407180951e184a7`.
It differs from the author's recorded published artifact hash; the gates above used the exact
reviewed source snapshot. Native AOT, .NET 11, Windows/macOS, live Herdr, and a published
status-bearing refusal were not run. S2 and interactive issues #5/#7 remain separate gates.

After the reviewed code commit, docs-only commit
`6ebb47b99f432dcf3aa41b7cb56e3784d49e4331` normalized the author's personal SDK path
to `<local-dotnet-sdk>` in `TEARDOWN-STATUS-REPORT.md`. That edit does not change the reviewed
source or tests: their Git tree hashes remain
`45fe2f2193cc24400da576c310514712147dc026` and
`8ad96f0e866ccd021b3a626c69396892ce506f01`, respectively. This rereview remains
pinned to the original code hash `85f3fdd`.
