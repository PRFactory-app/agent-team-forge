# Independent review: M0 fake-core demo runner

2026-09-26. Source snapshot `aba651be8c261d9b2ba5fbb2dc2e5ec50b8f6b45`,
diffed against base `55d3c054acf072f7d3da49c5f9bd20d9c3860058`.
Claude-authored slice, GPT/Codex reviewer. Report reviewed:
`spikes/m0-durable-core/DEMO-RUNNER-REPORT.md`. The base core B1/B2 re-review
at `1d3c409` is separate and is **not** inherited as approval here.

**Verdict: approve this bounded fake-core demonstration wrapper, subject to the
base core's independent gate.** It adds no daemon, adapter or test logic. The
script runs one existing process scenario, and the narrated lines correspond
to assertions in `tests/AgentTeamForge.Tests/Scenarios/ClientLifetimeScenarios.cs:14-58`.
It is a fake-backend checkpoint, not a real-agent or platform exit.

## Safety and result checks

- `scripts/demo.sh:28-43` requires the pinned SDK and checks the selected
  published binary before creating run state. Missing binary exits 2.
- `scripts/demo.sh:45-49` uses a private, run-specific ignored evidence
  directory. The script itself has no deletion command. It snapshots `/tmp/atf-*`
  directory names solely to warn about possible leftovers.
- `scripts/demo.sh:54-78` bounds build and test via `timeout`, records the
  test exit status and requires TRX `total=1 passed=1` before printing PASS.
  A matching TRX from a failed test cannot override a nonzero test exit. A
  missing/malformed TRX cannot satisfy the equality check. A zero-match
  `dotnet test` call was reproduced separately: it exits 0 but writes TRX
  `total=0 passed=0`, which this guard rejects. The author also reports a
  zero-match script mutation; I did not repeat that source mutation.
- `scripts/demo.sh:63-69,95-100` warns about newly observed state dirs but
  does not identify or kill a process by PID. A test-host timeout can skip
  fixture disposal and leave test-owned children/state for operator inspection.
  This is an explicit limit, not proven subprocess cleanup on timeout. The
  normal scenario relies on the pre-existing `SpikeRig`/`OwnedProcesses`
  handle-based cleanup; review of that base harness belongs to the core lane.
  A concurrent full test caused one WARN in my first JIT run, illustrating that
  the before/after directory comparison is advisory rather than ownership proof.
- `README.md` replaces a fixed `/tmp/atf-demo` path with `mktemp -d` and
  quotes state paths. Its manual cleanup instruction applies only to the
  caller's recorded daemon and new directory; the walkthrough was not run by
  this reviewer.

No blocking finding in this slice. The fixed `[ok]` output is a summary of
the scenario assertions, not a live trace; the README/report disclose this.
On test-host timeout, report-only cleanup is the safe choice for a wrapper
that lacks child handles, but it must not be called leak-free.

## Reproduced gates

Scratch worktree started at the exact base; this slice was cherry-picked there
as local commit `9ce61e7`. All commands used the isolated pinned .NET 11 RC1
SDK `11.0.100-rc.1.26425.128` on Linux x86_64, without installing an SDK or
using real models, Herdr or services.

| Check | Result |
| --- | --- |
| `bash -n scripts/demo.sh` | Pass. |
| Release format verification and warnings-as-errors build | Pass, 0 warnings. |
| Full .NET test suite after this slice | 37/37 pass. |
| `./scripts/demo.sh` with built JIT apphost | Exit 0, TRX 1/1 pass; advisory leftover WARN during concurrent full suite. No cleanup attempted. |
| `./scripts/demo.sh` with the combined tree's published Native AOT binary | Exit 0, TRX 1/1 pass, no leftover WARN. This is combined-snapshot execution, not standalone AOT authorship approval. |
| `ATF_DEMO_BIN=/usr/bin/true ./scripts/demo.sh` | Exit 1, TRX `total=1 passed=0`. Deliberate negative case; no live process touched. |
| Nonexistent selected binary | Exit 2 before build/test. |
| Zero-match `dotnet test` with TRX | Tool exit 0, TRX `total=0 passed=0`; script guard rejects by code inspection. |

The combined scratch tree after adding inspection was `cde9d20`; its AOT
publish and 14/14 published process scenarios passed separately. The demo
runner's AOT check uses that composite and does not resolve base-core review.
