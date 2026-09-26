# PRFactory connector — reuse the local worker

After ATF MCP validation, add an opt-in adapter, not another runner or relay. PRFactory already sends tasks and team recipes to a local executor over **outbound HTTPS polling**, not WebSocket/SignalR. Source inspection only; hosted behavior has not been tested.

## Existing code to reuse

Paths below are relative to `/home/mikael/code/github/PRFactory`:

- `src/PRFactory.Worker/Api/PRFactoryClient.cs` and `IPRFactoryClient.cs`: repository discovery, machine registration/heartbeat, poll → claim → progress → artefacts → complete/fail; lease fencing included. Server counterpart: `src/PRFactory.Web/Controllers/WorkerController.cs` (`/api/worker`). Port the relevant client/DTO code into ATF, not the worker's process host.
- `src/PRFactory.Worker/Api/Models/WorkerWorkItemDto.cs`: prompt, backend/model/effort, repository, read-only/output instructions and `TeamPlan` (lead/member roles and concurrency limits). `Host/Team/TeamShape.cs` and `Host/Wake/TeamRunCoordinator.cs` under the same worker project supply the existing recipe interpretation/team-loop approach.
- `src/PRFactory.Worker/Host/Commands/AgentCommandDrainLoop.cs`: poll pending `SendMessage`/`KillAgent`, then acknowledge. `Host/Upload/AgentStreamUploader.cs` and `Api/Models/AgentStreamModels.cs`: batched topology/output upload, per-agent sequence acknowledgements and credit limits. PRFactory's SignalR is the browser-update path, not a local-runner transport.
- Pairing examples: `src/PRFactory.Worker/Commands/InitCommand.cs`, `src/PRFactory.Web/Pages/Settings/WorkerSettings.razor.cs`, and `src/PRFactory.Web/Authentication/WorkerAuthenticationSetup.cs`.

No PRFactory integration was found by searching `/home/mikael/code/github/agentic-coder-teams-mcp` for PRFactory references. PRFactory instead has its own C# host port; `docs/planning/epic_24_worker_agent_teams_host/port-map.md` identifies the Python reference as its source. Do not introduce a Python intermediary.

## Connection and job mapping

Pair once: create a repository-scoped **Worker** token in app.prfactory.dev settings (not an MCP token), configure ATF with `https://app.prfactory.dev`, token and approved repository-ID → local-directory mappings, then register the machine. Store credentials owner-readable only; never send the local IPC token. Connector stays disabled until explicitly enabled. Use existing bearer authentication, HTTPS validation and retry/backoff; no inbound listener, tunnel or public MCP. Token rejection stops remote intake, not local jobs. Honor the user's selected interactive/headless launch mode.

A claimed work item becomes a durable local team record and lead `submit_job`; recipe members become ATF jobs through the existing team/MCP path. Persist work-item/lease, team/member and job IDs; key submissions by server/tenant/work-item/member/turn so retries cannot spawn twice. Resolve paths locally, reject unsupported backends, and carry model/effort and recipe limits without silently dropping them. ATF's current `Features/Jobs/JobContracts.cs` lacks model/effort fields: add that small passthrough where needed. Reuse PRFactory's prompt/output handling, including artefact upload before completion and read-only suppression of publication; first slice of execution supports one mapped repository, explicitly refusing multi-repository work until supported.

`SendMessage` maps to durable `follow_up` for a managed member. For a manually started external member, create or recover an `ExternalTeam` actor team from the work-item key, issue `CreateTicketForTeam`, and map `SendMessage` to `SendToMember(teamId,...)`; upload replies from `ReadTeam(teamId,...)` to the agent stream using a persisted cursor. Retain commands while a managed member's current turn is busy, deduplicate by command ID and acknowledge local acceptance, not delivery. `KillAgent` maps to `stop` for managed members or `CloseTeam` for an external team; closing an external team revokes membership without killing the user's session. Upload `job_status`/results and cursor-based logs through the existing agent-stream endpoint. Publish queued/delivered/failed truthfully. Local wake remains ATF's committed-message-then-notice mechanism (`Features/Wake/WakeCoordinator.cs`); PRFactory receives persisted events, never owns agent wake or execution.

For a recipe participant using an interactive session, configure `atf prfactory connect ... --repo REPOSITORY_ID=DIR --external REPOSITORY_ID:MEMBER_NAME` (repeat `--external` for additional recipe members). The daemon publishes a short-lived join prompt to the work item's agent stream and `atf prfactory status` prints the current prompt from an owner-private snapshot. The participant pastes it into `join_team` in their session. The member name must match the recipe; the current PRFactory recipe DTO has no external backend value.

## Disconnects

ATF owns accepted jobs, logs and pending uploads in SQLite/files. Connector loss does not cancel them. Resume polling and replay uploads/results from persisted acknowledgements after reconnect or daemon restart; never resubmit an already mapped job.

**One server change is necessary:** PRFactory currently expires/requeues leased work (`src/PRFactory.Infrastructure/Application/WorkItemService.cs` and `Persistence/Repositories/WorkItemRepository.cs`). Ordinary heartbeat retry cannot promise offline ownership. Add durable ATF acceptance tied to machine/work-item/job identity, reconciled idempotently after response loss; exclude accepted ATF work from automatic reassignment until explicit release/cancellation. Persist local acceptance held before confirming server acceptance, then dispatch. Preserve legacy worker leases. If authority is revoked, retain local results but fence remote publication; show reconciliation needed rather than lose work or run a duplicate.

## Implementation slices

1. **ATF:** opt-in setup, token storage, repository mappings, reused HTTPS client and machine heartbeat; test rejected tokens and disabled-by-default behavior.
2. **PRFactory:** durable ATF acceptance/reconnect support alongside existing leases; test lost acknowledgement and no reassignment during disconnect.
3. **ATF:** persisted work-item adapter, lead/member submission, recipe limits, model/effort passthrough and single-repository artefact completion; test duplicate claims and team execution.
4. **ATF:** command drain → follow-up/stop, durable deduplication and normal native wake; test busy members and repeated commands.
5. **ATF:** resumable logs/events/results upload and reconnect reconciliation. Exercise a published Linux binary against PRFactory: start team, follow up, disconnect, restart connector, reconnect and stop. Reuse existing PRFactory team UI; no new dashboard.
