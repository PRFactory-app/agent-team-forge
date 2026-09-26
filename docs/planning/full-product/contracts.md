# Cross-phase contracts and synthesis decisions

**Draft synthesis, not independent approval.** This document resolves boundaries
between phase drafts. It does not waive a safety or platform gate. The
[product scope](../../product-scope.md) owns global F01–F27 requirements; phase
work packages and gates refine them.

## 1. Scope IDs and phase ownership

P01's R01–R16 are phase-local requirement groupings, not a competing product
scope. Use this crosswalk in the final evidence ledger:

| P01 grouping | Canonical feature IDs |
| --- | --- |
| R01 | F01, F02, F09 |
| R02 | F05, F06, F07, F08 |
| R03 | F03, F04 |
| R04 | F10 |
| R05 | F11 |
| R06 | F05, F13 |
| R07 | F12 |
| R08 | F08, F14 |
| R09 | F15, F16, F17, F18 |
| R10 | F04, F09, F22 |
| R11 | F19 |
| R12 | F20 |
| R13 | F21 |
| R14 | F24, F26 |
| R15 | F23, F25, F26 |
| R16 | F27 |

Every F row must have implementation ownership and final qualification, not only
a documentation reference. C/L/T cases remain the original PoC/terminal case
identities; phase-specific gates supplement them. Do not renumber historical
cases or equate a subset of a case with its entire platform/mode requirement.

## 2. Architecture, process and schema

Three production projects remain Host → Business → DAL. Backend/terminal
interfaces may isolate genuinely different I/O behavior inside Business; this
does not introduce Business-owned repository ports or another assembly.

P02 owns one schema/migration stream and the source records for identities,
operations, jobs, attempts, results and lifecycle events. P03 extends bindings,
evidence and adapter controls; P04 extends team policy, recipient deliveries,
approvals and workspace metadata; P06 extends attachment and remote-command
mapping. No phase creates another scheduler or authoritative job store.

Only daemon mode opens the runtime job DB. Host client/bridge roles use private
IPC. Installation/doctor can inspect filesystem/package metadata without gaining
a second writable job database. Privileged OS setup must remain separate from
normal job authority and must be explicitly authorized.

Acceptance, external-effect attempt, acknowledgment, final result and observed
foreign activity are distinct. Physical ownership cannot be released by a logical
status or expired lease alone. A library process handle or restored DB backup
does not prove a surviving process is safe to adopt or replace.

## 3. Event sequence versus delivery cursor

P02's global monotonic event sequence is the durable lifecycle/audit identity.
P04 may add a **recipient-delivery index** with a sequence per durable consumer
stream. A delivery references the existing event/message ID; it must not copy
lifecycle authority into a second event store or execute messages as jobs.

P02 provides stable events, consumers, generations and retention primitives; it
does not expose a complete public ack API. P04 owns canonical batch receipts and
public read/ack, including migration of any experimental internal cursor.

Canonical delivery has no destructive filter: an ack is bound to principal,
stream, consumer generation, previous cursor and the exact issued batch. Filtered
inspection/search is read-only and cannot advance the canonical cursor. Event
sequence, recipient sequence, run generation and consumer generation are distinct
fields; never accept one in place of another. Revocation/retention gaps are
explicit markers or an explicit recovery protocol, not silent consumption.

## 4. Host wake and backend support are different

**Native session wake is the standard mechanism**, per the user's selected
public [PR #70](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70)
design. Unlike the reference's opt-in rollout, our supported host integrations
use native wake by default. Watchers, keystroke injection and tight model polling
are not the normal notification path. Manual read/catch-up is explicit degraded
recovery; it cannot silently pass a required native-wake gate.

P01 freezes required host/platform cells and proves transport feasibility; every
promised cell needs native idle-wake evidence. At least one automatic host is
necessary for the early team checkpoint, not sufficient for full-platform release.
P04 implements/tests Claude/Codex native host integrations; P06 adds Pi lead-host
integration and attachment wake. P03 already owns managed Pi execution, including
Windows; executing a backend never proves that backend's lead-host wake.

Reference mechanisms are Claude's owning-session inbox socket on Linux and
Codex's generation-bound registered thread via `codex queue`. Its Windows-Claude
transport, macOS reader and Pi path are not established by the PR. Native Windows
qualification remains required; missing transport/access blocks the relevant
claim rather than being covered by Linux or cross-build evidence.

Persist messages/events before attempting wake. Notices carry no message bodies
or credentials; recipients fetch through authenticated durable read/ack. Bind
registration to authorized principal, native host/session and generation; validate
nearest-host ownership and scrub inherited wake handles/environment on spawn or
resume. One fenced notifier per reader, bounded coalescing/backoff, stale-target
rejection and unread catch-up prevent floods and cross-session routing. A failed
wake cannot roll back a committed send; report its status separately. A successful
transport write is not an ack, result, or proof the model started a turn.

## 5. Proposed limits have one owner

P01 freezes the measured policy profile. Until then these are proposal inputs,
not approved limits or measured capacity:

- Two active turns globally by default; explicit finite team/root/backend limits
  cannot exceed their ancestor/global allocation.
- Root delegation depth zero, at most three delegation edges; at most 32 created
  descendant teams/agents, 100 accepted jobs and 20 wake attempts per root work
  epoch as initial safety inputs. A work epoch is not the idempotency-key epoch.
- Global queue proposal: 1,000 jobs; a per-root epoch cap may be lower. Duplicate
  idempotent retries do not consume a new admission.
- Prompt/frame proposals: 256 KiB / 1 MiB. Spool: 64 MiB per agent. Explicit
  global quotas must also be fixed before enabled dispatch.
- Retention proposals: 30 days for completed payload/acked delivery, seven days
  for rotated logs; 256 MiB log cap per team under a 1 GiB global cap. Artifact
  proposal: 1 GiB per team, also subordinate to an explicit finite global quota.

P04 provides budget accounting and enforcement details, not conflicting default
values. Define retry windows/tombstones, reserved recovery space, quota exhaustion
and retention gaps before implementation. Cost/token caps remain advisory unless
backend measurements and controls actually support hard enforcement.

Any chosen namespace/epoch compaction for idempotency is a reviewed contract, not
an automatic framework requirement. The small demo may retain its keys without
pruning; product retention cannot silently turn an old retry into a fresh job.

## 6. Waterfall path versus early experimental preview

Formal full-phase progression follows P01 → P08 and requires the phase's full
exit. The separately authorized Linux demo is an **experimental checkpoint**, not
permission to mark P01/P02/P03 complete before all their gates pass.

A reviewed Linux-specific contract may permit isolated durable-core/adapter
experiments while full P01 feasibility is blocked on another platform or backend.
Their code/evidence can later be promoted after required phase review and gates;
this is not production implementation against an unresolved unsafe contract.
The baseline therefore does not promise that full P01/P02 qualification can fit
into the user's hours-scale demo target.

P01 owns early platform/control feasibility; P03 owns product adapter semantics;
P05 owns installed production-context behavior; P07 reruns or reuses explicitly
unchanged snapshot-bound evidence for integrated qualification. A successful
probe, product adapter, and installed support claim are different artifacts.

AOT remains a full-release target. A labelled JIT-only experiment is useful but
cannot pass required AOT gates. SDK/package installation, platform access, model
budget, license and publication remain explicitly authorized actions, not effects
of accepting a document.

## 7. Scoped operator console addition

The user explicitly added F27 after the initial baseline: a small cross-platform
text-only web console for status/output, human follow-up and confirmed whole-agent
stop. This supersedes blanket no-web-UI exclusions, not the ban on a custom model
loop, rich analytics dashboard or terminal emulator. Plan and static HTML mockup
come first; implementation requires review of the new HTTP/security boundary.

P04 owns shared authorized projections/control; P05 owns the Host web surface and
packaging; P07 verifies browser security, bounded output, lifecycle and real-platform
behavior; P08 documents operation. Exactly three projects remain. Only the daemon
opens runtime SQLite; the browser is another client and never owns accepted work.
See [the console plan](../../ui/operator-console-plan.md) and its mockup.
