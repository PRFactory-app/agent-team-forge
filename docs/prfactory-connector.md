# PRFactory connector

The PRFactory connector lets tasks created and assigned in
[PRFactory](https://app.prfactory.dev) run on your local ATF agent team, with
results posted back to the task. It is opt-in, off by default, and ATF works
fully without it. Decision record: [ADR 0011](adr/0011-prfactory-outbound-polling.md).

**Status:** the ATF side is built (`src/AgentTeamForge.Host/Features/PRFactory/`).
The PRFactory server-side durable ATF acceptance is being finalized; until it
is released, end-to-end use against the hosted app is not supported.

## Connect

1. In PRFactory settings, create a repository-scoped **worker** token (not an
   MCP token).
2. Map each PRFactory repository ID to a local checkout and pipe the token on
   stdin (the token is never accepted as an argument):

   ```sh
   printf '%s\n' "$PRFACTORY_WORKER_TOKEN" | atf prfactory connect \
     --url https://app.prfactory.dev \
     --repo REPOSITORY_ID=/path/to/checkout
   ```

   Repeat `--repo` for more repositories. Add
   `--external REPOSITORY_ID:MEMBER_NAME` for recipe members that are
   interactive Desktop sessions (see below).
3. `atf prfactory status` shows the URL, mappings, pending join prompts and
   whether the token was rejected. `atf prfactory disconnect` removes the
   settings and token.

Settings and token are stored owner-only in the state directory. The local IPC
credential is never sent to the server.

## How it works

The daemon talks to PRFactory only by **outbound HTTPS polling**, reusing the
protocol of PRFactory's own local worker: register the machine, heartbeat,
poll, claim, report progress, upload artifacts, complete or fail. There is no
inbound listener, tunnel or public MCP endpoint. A rejected token stops remote
intake; local jobs continue.

- **Claim → jobs.** A claimed work item is stored in `prfactory_teams` and
  acknowledged to the server (`atf-acceptance`) before dispatch. The recipe's
  lead and members become ordinary ATF jobs in the mapped checkout, in the
  launch mode chosen at setup. Each member submission is keyed by server, work
  item, member and turn (`prfactory_members`), so a repeated claim never
  starts a second job. One mapped repository per work item; multi-repository
  work is refused.
- **Commands.** The daemon drains pending server commands. `SendMessage`
  becomes a `follow_up` for a managed member, or a message to an external
  member. `KillAgent` stops a managed member's job, or closes the external team
  for that work item (which revokes membership without killing the user's
  session). Command IDs are deduplicated (`prfactory_command_receipts`).
- **Output.** Job status, results and logs are uploaded through PRFactory's
  agent-stream endpoint with persisted positions. Artifacts are uploaded before
  completion. Managed uploads persist the exact pending batch before HTTP, so
  a lost acknowledgement or daemon restart replays the same sequence numbers.
- **Writable work.** Writable jobs run in ATF job worktrees. Completion reports
  the lead's actual worktree branch and HEAD, including after a follow-up or
  branch switch; read-only work omits branch and commit metadata.
- **Wake** stays local: ATF's commit-then-notice mechanism
  ([ADR 0005](adr/0005-native-wake.md)). PRFactory only receives persisted
  events.

## External recipe members

A recipe member mapped with `--external` is a person's own interactive session
(for example Codex Desktop) rather than an ATF-launched agent.

- The daemon creates an external team for the work item and issues a join
  ticket for that member name. The name must match the recipe.
- The ticket is a bearer secret and the agent stream is visible to everyone in
  the tenant who can open the work item, so the stream only shows a "waiting
  to join" notice. `atf prfactory status` prints the join prompt; the
  participant pastes it into `join_team` in their session.
- A ticket expires after ten minutes. If it expires unused, the daemon issues a
  new one on the next tick.
- A `SendMessage` to a member who has not joined stays unacknowledged until
  they join; one to a member who has left is rejected with `member_left`.
  Drained commands are retained locally until acknowledged. If `KillAgent`
  revokes an unjoined member, its waiting messages are rejected with
  `member_closed` before the work item finishes.
  Member replies are uploaded to the agent stream from a persisted cursor.

## Disconnects and restarts

ATF owns accepted work, logs and pending uploads in SQLite and files. Losing
the connection does not cancel jobs. After reconnect or daemon restart,
polling resumes and uploads replay from persisted acknowledgements; an already
mapped job is never resubmitted.

This relies on the server not reassigning accepted ATF work while the machine
is offline. That server-side durable acceptance (tied to machine, work item
and ATF job identity, reconciled idempotently after a lost response) is the
part being finalized in PRFactory. If the server revokes authority, ATF keeps
local results but stops publishing them.

The connector advertises worker contract version `1.0.0` consistently during
registration, polling and claiming. This is PRFactory's single-repository
compatibility level, independent of the ATF product version; it does not claim
the newer multi-repository capability.

## Phase artefacts

Built-in phases collect top-level Markdown and HTML documents from the latest
lead job's ticket folder. Decomposition additionally collects JSON. Filename
stems determine kinds, including `qa`/`questions` → `qa-po` for TicketRefinement
and `qa-dev` otherwise, and numbered plan/code review reports. Custom steps
upload only their named `ExpectedOutput` as `custom-step`, matching the worker's
custom-step result contract. Child deliverables must be present in the lead
workspace; the connector does not merge independent child worktrees.

Planning adds `plan-basis.json` with the repository ID, actual branch/HEAD and
tracked paths when Git can report them. As with the worker, an unreadable basis
does not discard the plan; PRFactory retains its approval checks. Built-in
document completions omit CLI result chatter so it cannot overwrite uploaded
reviews or proposals. Custom completions carry the expected file's frozen text.

Before the first upload, SQLite stores the exact request body, including file
contents and lease token. Restarts and lost HTTP responses resend those bytes.
Missing required phase outputs, unsafe paths (including intermediate/file
symlinks), failed jobs and terminal upload rejections go directly to Fail with
a persisted diagnostic. Network errors, HTTP 408/429 and server errors retry;
token rejection and lease fencing retain their existing handling. An execution
failure never depends on a successful artefact upload.
