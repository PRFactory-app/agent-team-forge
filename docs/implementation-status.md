# Implementation checkpoint — 2026-09-26

Evidence-bound snapshot, not release approval or live worker telemetry. No full
product phase is complete. The canonical root solution is on `main` at `d7d24ae`.

## Canonical Linux fake-core checkpoint

`main` at **`d7d24ae`** promotes `integration/canonical-wave` tip `e2340dd`:
canonical source `3f0b0c3`, reviewed tooling fix, P2 private-file reads and D2
offline Herdr launch characterization. The root `AgentTeamForge.slnx` contains
Host → Business → DAL plus tests, targeting .NET 11 RC1.

| Gate | Recorded result at this snapshot |
| --- | --- |
| Restore, format, Release warnings-as-errors build | Passed |
| Full tests | 76/76 passed |
| Linux x64 Native AOT publish | Passed |
| Published process scenarios | 22/22 on clean rerun; one earlier run 21/22 (intermittent, under investigation) |
| Published-binary fake demo | 1/1 passed |
| Source review | Approved at `a86dbe4` |
| Tooling, P2 and D2 reviews | Approved (separate Codex records) |
| Promotion to `main` | Done at `d7d24ae` |

See the [combined gate record](spikes/canonical-wave-integration.md) for exact
inputs and evidence, and [README commands](../README.md#run-the-bounded-checkpoint)
for SDK discovery, verification and demo execution. This document update does
not rerun or independently approve those runtime gates.

The fake demo exercises durable MCP acceptance, bridge death, fresh-client
result retrieval, same-key replay and daemon-restart recovery. It does **not**
launch a real coding agent, prove interactive terminal control or qualify
Windows/macOS. The published executable is a checkpoint, not an installed service.

## Full-product phases

| Phase | Current qualification |
| --- | --- |
| P01 — requirements and design | In progress; plans exist, not all product gates closed. |
| P02 — core platform | Bounded fake-core checkpoint implemented; full phase incomplete. |
| P03 — native execution | Isolated spikes and contract research; real-agent E2E unqualified. |
| P04–P08 — remaining product delivery and release | Not started as completed runtime phases; planning documents are not implementation. |

The [roadmap](roadmap.md) defines the complete ordered gates. The
[HTML report](project-status.html) is a separately timestamped visualization;
its historical integration status is not live telemetry.

## Other lanes: do not confuse baselines

- The earlier approved fake checkpoint `81a11b2` is the canonical source's
  behavioral baseline. Its [original record](spikes/m0-fake-core-integration.md)
  remains historical evidence, not a second product runtime to preserve forever.
- Later legacy integration `cd60824` combines inspection and hardening but has
  a recorded `CS7036` build failure in `AcceptanceOutcomeTests` after the
  `JobsEndpoint` constructor changed. That composite is **not green** and is
  not the canonical `main` snapshot above. It needs a fresh Claude fix,
  independent Codex review and combined gates before any promotion.
- F27 has a [console design](ui/operator-console-plan.md),
  [static mockup](ui/operator-console-mockup.html) and
  [approved design corrections](ui/operator-console-plan-rereview.md).
  Those are contracts, not a runtime web console. New implementation slices
  and their security review remain separate gates.
- [Codex queue research](research/codex-queue-admission.md) and
  [native downstream delivery research](research/native-downstream-delivery.md)
  identify useful mechanisms and unresolved admission/recovery boundaries.
  They do not establish safe native adapters or strict human-turn priority.

## Next gates and boundaries

1. Diagnose the intermittent published-scenario failure
   (`Dispatcher_fault_stops_admission_instead_of_leaving_a_ready_daemon`).
2. Port independently reviewed follow-on slices through dedicated integration
   branches. Codex integration does not self-approve semantic fixes.
3. Deliver real owned-agent launch, same-session follow-up, results and stop
   with the chosen visible interactive mode or explicitly chosen headless mode.
   Keep fake-only previews labelled and unsupported actions unavailable.
4. Qualify native wake and managed Claude, Codex and Pi separately on promised
   platforms. Linux tests and cross-builds cannot establish native Windows or
   macOS support; upstream PR #70 evidence is not local qualification.

The operator console must remain inside Host, reuse daemon authority and avoid
its own database access or scheduler. Schema changes belong to their assigned
owners, not incidental UI work.

The pinned isolated SDK is `11.0.100-rc.1.26425.128`; root scripts discover it
without changing global SDK/PATH. Legacy .NET 10 evidence remains version-bound.
Retire superseded spikes only after reviewed behavior and regression tests move
into the canonical solution; preserve live sessions and unique recovery evidence.

GitHub organization/account, license and release decisions remain owner-dependent.
Keep worktrees, SDKs, build outputs, raw state and credentials ignored. No release
date or complete platform support follows from a planned wave diagram.
