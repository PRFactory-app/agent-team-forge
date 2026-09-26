# Full-product scope and acceptance baseline

## 1. What “full product” means

AgentTeamForge is a local-first execution engine for coding-agent teams. A lead
agent or human operator can create teams, launch and control agents, exchange
work and results, disconnect, and reconnect without owning the lifetime of
accepted work. Existing backend CLIs run the models and keep their own user
interfaces and authentication.

This is the **proposed complete planning baseline**, authored at the user's
request for a waterfall roadmap. It is not a claim that all capabilities are
available or that their design has passed review. P01 freezes the supported
combinations and acceptance contracts before full implementation proceeds.

A **full v1 release** means every required baseline row below has evidence for
its declared supported configurations, no unresolved blocking safety findings,
and an approved release decision. “Unsupported” is honest status, but is not a
pass for a required row. A narrower preview must name its exclusions explicitly.
Any change to the full baseline is an explicit scope decision, not a silent
fallback to a smaller product.

## 2. Product feature inventory

| ID | Capability and observable contract | Main delivery phase | Release treatment |
| --- | --- | --- | --- |
| F01 | A separately owned local daemon runs accepted jobs without an active lead/bridge. | P02 | Required. |
| F02 | Durable SQLite acceptance, scoped idempotency, queued intent, pre-effect attempt generation, atomic results/events. | P02 | Required; lost responses never blindly duplicate work. |
| F03 | Authenticated private IPC and thin MCP; CLI/operator and agent roles cannot self-escalate through payload IDs. | P02, P04 | Required. |
| F04 | Versioned APIs/config/schema with bounded errors, resource limits, and fail-closed compatibility checks. | P02, P07 | Required. |
| F05 | Managed Claude Code, Codex and Pi: launch, follow-up in the same conversation, status, authoritative results, approvals, turn interrupt, separate stop. | P03 | Required for each supported backend/mode contract; blocked capability needs resolution or explicit scope change. |
| F06 | Real interactive TUIs: Herdr on Linux, selected visible terminal provider on Windows and macOS; machine control and genuine human input. | P03, P05 | Required, not log-tail UI or headless simulation. |
| F07 | Explicit headless execution choice with verified backend control and honest approval/cancel behavior. | P03, P05 | Required option, never an automatic fallback. |
| F08 | Verified process/session/terminal ownership, safe targeted teardown, foreign/human pause and authorized reconciliation. | P03, P04 | Required; PID/name/tag alone is not authority. |
| F09 | Client-crash survival, daemon recovery/quarantine, backend failure handling and replayable result evidence where claimed. | P02, P03, P07 | Required; no blanket exactly-once or arbitrary process-adoption promise. |
| F10 | Multiple teams, stable participants, parent/child and bounded nested delegation with explicit permissions and budgets. | P04 | Required full-product team capability. |
| F11 | Durable messages and events, consumer-bound read/ack, reconnect fencing, no skipped unread events or automatic model-action claims. | P04 | Required. |
| F12 | Native session wake is standard: authenticated host-native notice, coalescing, bounded retry and durable unread catch-up, following the public PR #70 mechanism. | P01 feasibility; P04, P06 | Required for promised host/platform cells, including native Windows evidence; manual read/catch-up is explicit degraded recovery, not normal polling or a silent substitute. Test each independently. |
| F13 | Scheduling, concurrency, queue/output/retention bounds, deadlines, approvals and usage reporting without fictitious hard spending caps. | P04 | Required. |
| F14 | Workspace/worktree isolation and ownership, artifact/result references, bounded logs and safe diagnostic export. | P04, P07 | Required. |
| F15 | Operator CLI: start/stop/status, team/job inspection, approvals/reconciliation, diagnostics and actionable errors. | P04, P05 | Required; no custom dashboard implied. |
| F16 | Setup/doctor persists explicit launch mode/provider and checks backend authentication in the actual service/desktop context. | P05 | Required. |
| F17 | Linux user service, verified Windows user/desktop launch model, macOS user/GUI launch model; no silent root/system or Session-0 GUI assumptions. | P05 | Required. |
| F18 | Packages, safe configuration merge, install/upgrade/rollback/uninstall, data retention choices and backend compatibility checks. | P05, P08 | Required for Linux x64, Windows x64 and one verified macOS architecture; additional architectures optional. |
| F19 | First-class spawned Pi backend through the same job/authorization model, including native Windows execution, same-session follow-up, results, interrupt/stop and reconnect. | P03; P05 installed contexts | Explicit user requirement, not a future optional adapter or attach-only substitute. Test real Windows CLI/GUI behavior in both selected modes; Linux and cross-build evidence are insufficient. |
| F20 | Supported attached/join sessions with explicit consent, capability-scoped credentials, revocation and honest ownership limits. | P06 | Included; arbitrary uncooperative Desktop sessions are not promised. |
| F21 | Optional external orchestrator connector: documented generic contract, pairing/revocation, remote idempotency/ownership, offline policy and explicit export. | P06 | Required available option; off means no contact or sharing. No private vendor/source dependency. |
| F22 | Backups/restores and migration/upgrade compatibility protect WAL state and in-flight ownership. | P02, P05, P07 | Required; copying a live DB file is not a backup contract. |
| F23 | Threat-model-based security qualification, credential redaction, least privilege, safe paths/configuration and supply-chain checks. | P01, P07 | Required; does not claim an OS sandbox against same-user arbitrary code. |
| F24 | .NET 11 build, reproducible packaging and Native AOT validation with actual MCP/SQLite/process paths on every claimed RID. | P01, P02, P05, P07 | Required target; a JIT-only preview is labelled separately. Any final AOT scope change needs explicit approval. |
| F25 | Public contributor/user docs, license and third-party notices, sanitized examples, release artifacts and support matrix. | P08 | Required; Git/publication remains separately authorized. |
| F26 | Performance/resource evidence, upgrade regression checks, operational recovery/runbooks and maintained compatibility policy. | P07, P08 | Required; no unmeasured memory/token savings claim. |

A phase owns implementation, but cross-cutting safety and testing are continuous.
For example, P07 validates the integrated product; it does not postpone
permissions, credential handling, TDD, or code review until the end.

## 3. Explicit option boundary

These are tracked options rather than automatic requirements for full v1:

| Option | Decision needed before adding scope |
| --- | --- |
| Product web/TUI dashboard | User value and maintenance budget; setup/CLI already provide the required human interface. |
| More backend vendors or arbitrary terminal providers | Public supported protocol, test access, and a real capability need. |
| More CPU architectures | Actual package, native dependency and GUI/process testing for each RID. |
| Legacy MCP compatibility facade or in-place session migration | Concrete consumer need, bounded compatibility contract and rollback. |
| Multiplexed backend workers | Measurements justify complexity without weakening ownership/failure isolation. |
| Seamless arbitrary worker survival across daemon restart | Verified independent ownership/control protocol, not PID adoption or guessed I/O recovery. |
| Replacing an external system's execution worker | Separately scoped migration, not a consequence of having an opt-in connector. |

Do not build a custom model loop, distributed scheduler, mandatory remote control
plane, fleet/billing platform, or automatic telemetry collection. These remain
outside the product vision, not hidden tasks in a later phase.

## 4. Waterfall baselines and change control

1. **Requirements baseline:** feature rows, supported configuration matrix,
   nonfunctional limits, threat model, and user acceptance scenarios.
2. **Design baseline:** architecture, schemas/APIs, delivery/identity contracts,
   adapter capability decisions, and required platform evidence.
3. **Implementation phase gates:** each phase consumes versioned predecessor
   artifacts, delivers vertical slices and tests, then hands reviewed contracts
   forward. A discovered incompatibility returns to the relevant design decision.
4. **System qualification baseline:** release-candidate snapshot, complete
   coverage/limitations matrix, upgrade/recovery/security and user acceptance.
5. **Release baseline:** authorized license/distribution, sanitized artifacts,
   support/maintenance policy and a reversible deployment procedure.

Changes record affected F IDs, phase/work-package dependencies, migration and
test impact, and an explicit decision. Do not silently weaken a requirement to
turn a blocked gate green. Major design changes get plan review; all code gets
opposite-family review. Small fixes do not need another heavyweight plan cycle.

## 5. Near-term demonstration versus complete acceptance

The early target is a real Codex TUI in Herdr controlled through a durable local
.NET 11 core and MCP bridge, including bridge death and fresh-client result
retrieval. See [the demo plan](spikes/e2e-demo-plan.md).

That demonstration can happen before full P03 acceptance. It is explicitly
Linux/Codex-limited and does not count as complete Claude support, full
Pi support, three-platform operation, service installation, full team controls, or a public
release. A fake-backend core is a necessary engineering checkpoint, not a
substitute for the real-agent demonstration.

Current experimental work has unresolved independent review findings until fixes
are re-reviewed. Forty-nine passing unit tests at the reviewed spike snapshot
were useful evidence, not approval of its adapter safety. Existing .NET 10
observations do not prove the new .NET 11 toolchain.

The execution sequence and phase plans are consolidated in
[the roadmap](roadmap.md) and [full-product planning brief](planning/full-product/README.md).
