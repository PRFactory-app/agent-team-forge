# Independent re-review: M0 admission fence

2026-09-26. Reviewed the Claude-authored source commit
`a8e43524caf9cc5236abfb673d69c2dd3d4acbe7`, parent
`1d3c409514dc30a774376d7cdc124a6069c843c4`, and its
`spikes/m0-durable-core/ADMISSION-FENCE-REPORT.md`. This is a focused
opposite-family Codex review of blocking finding F1 in
[the prior re-review](m0-core-fault-fix-code-review.md), including preservation
of the B1/B2 fixes. No runtime code was changed in this review.

**Verdict: approved for the bounded Linux fake-core integration.**
`AdmissionGate.TryEnter` and `Close` use the same lock. A submit entering before
closure can finish its SQLite transaction; one entering afterward returns
`daemon_unhealthy` before database access. The dispatcher closes the gate when
its terminal write fails, when it faults, or when its loop exits. The host
waits for admitted submits to drain within a bound before disposing the
dispatcher and retains unhealthy exit 70. The barrier test holds a real
acceptance transaction across a forced dispatcher halt, checks the post-halt
responses and durable intent/events, then verifies same-key recovery after
restart. This addresses F1's required ordering and lost-reply recovery.

The timeout path now calls the guarded owned-child termination helper, so a
failed termination still records `needs_reconciliation/backend_timeout` while
the daemon is alive. The earlier B2 full-effect deadline, conservative
uncertain-attempt handling, and B1 profile validation and fail-stop behavior
remain in place. The diff does not weaken prior safety tests.

## Independent gates

From the exact clean source worktree, ran `DOTNET=<main-checkout>/.tools/dotnet11/dotnet ./scripts/verify.sh`
inside `spikes/m0-durable-core/`. SDK `11.0.100-rc.1.26425.128`:
restore, format verification, Release build with warnings as errors (zero
warnings/errors), **50/50** tests, Linux x64 Native AOT publish, and **19/19**
published native process scenarios passed. The tested `atf` SHA-256 was
`6ccc2e21e4f4a2b1a076f1aacbc488bb4a2152a536084def6258624513e6ddcd`.
This binds the tested artifact; it does not claim byte-for-byte reproducibility.
`git diff --check 1d3c409..a8e4352` passed. The source worktree remained clean.

## Limits carried forward

- The halt-to-exit interval is tested deterministically in process against
  the real Business objects and SQLite; the published daemon scenarios prove
  unhealthy exit and restart, but do not force that interval over IPC.
- A submit admitted before closure may commit while dispatch is stopping and
  remain queued until restart. Its reply can be lost. Same-key retry after
  restart is the recovery path. The drain is bounded and logs if it times out.
- Late-start cleanup across daemon death, post-commit response ambiguity,
  bounded private-file reads, and the remaining findings in the
  [original core review](m0-durable-core-code-review.md) remain qualified.
  The separately reviewed IPC deadline fix addresses its bounded transport
  portion only. No real adapter, Herdr, Windows/macOS/Pi, native wake, service,
  or power-loss behavior is approved by this fake-core review.
