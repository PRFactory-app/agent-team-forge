# Independent review: M0 Herdr teardown fence

## Snapshot and verdict

Reviewed the Claude-written fix at **`22e359859c1a1d39d45025f8d5b7ba63d9b364ea`**, parent **`1f68da5f7eb097bf7259e52bc8a15a6e55077294`**, in the separate `spike/m0-teardown-fence` worktree. This review covers **S1** from the original `docs/spikes/m0-safety-lanes-review.md` on `integration/m0-e2e`. I read the exact diff, related Herdr and CLI paths, tests, and `spikes/m0-interactive/TEARDOWN-FENCE-REPORT.md`. No source was copied into `review/m0-bounds`, and no runtime or test code was edited.

**Verdict: the S1 destructive-operation fence is sound for the real Herdr provider; changes are required for the claimed machine-readable teardown status.** The real provider exposes no identity-bound destroyer, so the CLI now refuses automated teardown after read-only ownership checks. This closes the name-reuse stop/delete race by declining both operations. It does not add automated Herdr teardown capability. The CLI status contract remains incomplete as described below; do not merge this whole slice as satisfying its stated status behavior until fixed and re-reviewed.

## Findings

### T1 — CLI drops the structured refusal status (blocking the claimed machine contract)

`Launch.cs:188-205` defines `TeardownStatus` and stores it in `TeardownRefusedException.Outcome`, but `Program.cs:81-86,125-129` catches it as an `InvalidOperationException` and emits only `event`, exception `type`, and a prose `message`. `TeardownOutcome.ToJson()` is emitted only on `Destroyed`; real Herdr cannot reach that path because its destroyer is null. Therefore a client sees neither a `status` field nor a stable code for `UnsupportedNoIdentityBoundDestroy`, `RefusedUnproven`, or `RefusedIdentityChanged`. `TeardownFenceTests.cs:87-94` asserts only an exception message prefix, not the emitted JSON. This contradicts the author's machine-readable outcome claim and requires parsing a human message to distinguish refusal reasons.

**Required:** map `TeardownRefusedException.Outcome.Status` into a stable JSON field in the CLI error event, retain exit 1, and test the actual CLI mapping without a live Herdr call. Re-review the exact correction. This finding does not invalidate the fail-closed destructive fence.

### T2 — Instructions still promise automatic deletion (non-blocking documentation follow-up)

`spikes/m0-interactive/README.md:71` and `REPORT.md:238-246` still tell the operator that `teardown` stops and deletes the proven session. At this commit it always refuses on real Herdr. Update the runbook and historical capability description visibly, including the need for manual verified cleanup. This discrepancy can mislead an operator, although it cannot itself cause an unsafe automated delete.

## S1 safety analysis

- `Launch.cs:163-184` performs read-only session, process-identity and workspace checks, then calls a destructive method only if `IdentityBoundDestroyer` is present. `HerdrTeardownProvider` returns null at `Launch.cs:240-249`; it has no global stop/delete call. A replacement after any proof cannot be targeted by this real path.
- `IIdentityBoundSessionDestroyer.DestroyIfStillOwned` is a **contract**, not a proof that an arbitrary future implementation is atomic. Its fake checks the supplied PID/start time and owner label at the destruction call, but a future real provider must establish that stop **and** delete are bound to the same identity without a name-reuse gap. The fake's check and record are suitable for testing this routing, not evidence of Herdr capability.
- The tests replace the name occupant after each read boundary and show refusal without a destroyer, or a preserved replacement with the contract-honouring fake. No stop-to-delete boundary remains in the real code. Existing `HerdrOwnership.Teardown` still refuses stopped/unproven sessions. Read-only proof can become stale; refusal on Herdr makes that harmless for S1.

## Independent gates

On Linux with command-scoped .NET SDK `10.0.401` and an offline local package source: restore passed; `dotnet format AtfSpike.slnx --verify-no-changes --no-restore` passed; Release build with `-warnaserror` passed with zero warnings/errors; **159/159** tests passed. A framework-dependent Release publish passed. Its `AtfSpike` binary SHA-256 was `fd43f65c851e9e72bb59e6ad41ff93b932d6268e156093242ee68db3dfe536b7`. The published binary's `teardown` command against an empty isolated state directory emitted an error and exited **1**, as expected. The first probe wrapper treated that expected exit as a failed command; a second probe explicitly checked exit 1 and passed. This published probe reaches the no-state refusal only; the new Herdr refusal path is covered by fake-provider tests, not by a live Herdr or published-path test.

No live Herdr, agent, terminal, service, or network call was made. Native AOT publish, .NET 11, Windows/macOS, and real identity-bound provider tests were not run. S2 and interactive issues #5/#7 remain separate gates; this verdict grants no full-adapter approval.
