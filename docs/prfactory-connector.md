# PRFactory connector

The PRFactory connector lets tasks created and assigned in
[PRFactory](https://app.prfactory.dev) run on your local ATF agent team, with
results posted back to the task. It is opt-in, off by default, and ATF works
fully without it. Decision record: [ADR 0011](adr/0011-prfactory-outbound-polling.md).

**Status:** the ATF side is built (`src/AgentTeamForge.Host/Features/PRFactory/`).
The PRFactory server-side durable ATF acceptance is merged
(PRFactory #254).

## Connect

1. In PRFactory settings, create a repository-scoped **worker** token (not an
   MCP token).
2. Map each PRFactory repository ID to a local checkout and supply the token (it is never accepted as an
   argument). In a terminal, `atf` prompts `PRFactory worker token:` and reads it without echo: paste the token
   when prompted, or pipe it on stdin:

   ```sh
   printf '%s\n' "$PRFACTORY_WORKER_TOKEN" | atf prfactory connect \
     --url https://app.prfactory.dev \
     --repo REPOSITORY_ID=/path/to/checkout
   ```

   Repeat `--repo` for more repositories. Add
   `--external REPOSITORY_ID:MEMBER_NAME` for recipe members that are
   interactive Desktop sessions (see below).
   For a server whose certificate comes from a private CA, add
   `--ca-file /path/to/ca.pem` (PEM, one or more certificates). The absolute
   path is stored in the settings, so it survives a daemon autostarted by a
   client bridge (unlike `SSL_CERT_FILE` in the environment). The CA is trusted
   in addition to the system store; name mismatches and expired certificates
   are still rejected. If the file is missing or unreadable when the daemon
   starts, it logs once and the connector stays stopped (no insecure fallback).
   Reconnecting without `--ca-file` clears it.
3. `atf prfactory status` shows the URL, the CA file, mappings, pending join prompts and
   whether the token was rejected. `atf prfactory disconnect` removes the
   settings and token.
4. A work item the connector fenced (`reconciliation needed` in the status
   output) is never re-claimed on its own. `atf prfactory release WORK_ITEM_ID`
   forgets that team, so the next poll can claim the item afresh. It works while
   the daemon runs (one database transaction; a late authority write for the
   released item is ignored) and refuses an unfenced or unknown team, or one
   whose execution is still being stopped. Worktrees, artefacts, jobs and logs
   are kept. It does not call PRFactory: also release the acceptance there
   (`POST /api/work-items/{id}/atf-acceptance/release`), otherwise the server
   still answers `accepted` for the old job.

Settings and token are stored owner-only in the state directory. The local IPC
credential is never sent to the server.

For a **tenant-wide worker token**, add `--token-scope tenant-wide`. This enables
repo-less intake by default; `--repo-less false` disables it. Repository-scoped
tokens and existing configurations never poll repo-less work. The worker API
does not expose token scope, so the setting records the scope chosen when the
token was created; the server still enforces that scope. A tenant-wide connection
can omit `--repo` entirely for scratch-only work. With mappings, the connector
polls mapped repositories and separately sends an empty repository list for
repo-less work, filtering that response to null repository IDs.

Null or omitted repository IDs use TeamWorkspace's private scratch directory,
retained across follow-ups and restarts. They never run Git preparation,
plan-basis discovery or publication and complete with null branch/SHA. Text
artefacts, human questions, cancellation and attachments work in scratch too.

## How it works

The daemon talks to PRFactory only by **outbound HTTPS polling**, reusing the
protocol of PRFactory's own local worker: register the machine, heartbeat,
poll, claim, report progress, upload artifacts, complete or fail. There is no
inbound listener, tunnel or public MCP endpoint. A rejected token stops remote
intake; local jobs continue.

- **Claim → jobs.** A claimed work item is stored in `prfactory_teams` and
  acknowledged to the server (`atf-acceptance`) before dispatch. The recipe's
  lead and members become ordinary ATF jobs in an owned team workspace (see
  below), in the launch mode chosen at setup. Each member submission is keyed by server, work
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
- **Writable work.** See *Workspaces and publication* below. Completion reports
  only a pushed, verified public branch and HEAD; other phases omit them.
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
and ATF job identity, reconciled idempotently after a lost response) is merged
in PRFactory (#254). If the server revokes authority, ATF keeps
local results but stops publishing them.

The connector advertises worker contract version `1.0.0` consistently during
registration, polling and claiming. This is PRFactory's single-repository
compatibility level, independent of the ATF product version; it does not claim
the newer multi-repository capability.

## Authority and cancellation

Each connected server has one long-lived authority gate (`prfactory_authority`).
Acceptance reads carry the server's disposition (`accepted`, `completed`,
`cancelled`, `revoked`, `reconciliation-needed`; older servers are mapped from
the work-item status and anything unknown becomes reconciliation). Every side
effect — job submit, follow-up, external invite/send, stream/ack upload,
artefact upload, push, completion, failure — is admitted individually and only
while the team is freshly confirmed. The dispatcher launches a queued connector
turn only under that confirmation, so restart, outage or fencing hold new turns.

A cancel, revoke or completion from the server durably fences first, then stops
every owned turn (including deferred follow-ups and retained interactive
sessions) and revokes external members without touching their processes. Stops
retry every tick and after restart until the dispatcher proves the run has
unwound. Reconciliation pauses and quiesces work but keeps polling for a
definitive disposition; fencing is sticky. Local files are never deleted. A
push that was already admitted cannot be undone; its receipt is kept and the
completion is still refused. A rejected token fences all owned work.

## Workspaces and publication

Every claim gets an owned checkout set under `<state>/prfactory-workspaces`,
created from the fetched base, never from the user's working copy. The mapping's
`remote` pins origin; a claimed base snapshot pins the base branch and SHA,
otherwise the mapping's `baseBranch` or remote HEAD selects the base. A claimed
continuation takes its exact recorded commit before a `StartFromBranch` handover.
ProjectInit resumes its existing `init/<KEY>` branch. A handover without an
authoritative start SHA fails visibly. Remote, base, start SHA, internal and
publish branch are recorded before dispatch and reused on restart. Children get separate
checkouts; when all succeed, their commits are merged into the lead branch in
declared order (conflicts fail with the file list, nothing is auto-resolved),
their ticket documents are staged, and the lead gets one finalization turn.

Implementation, CodeReview, writable CustomStep and ProjectInit publish: the
lead must have committed its code (only top-level untracked Markdown/HTML documents
in the ticket artefact folder are excluded; nested files and source files block
publication); the frozen HEAD is pushed without force to the
publish branch (`prfactory/<work-item>` or the server's `PublishBranch`) using
the user's own Git credentials, verified with `ls-remote`, and recorded before
completion. Completion sends that branch/SHA plus a verified `publication`
block so PRFactory can open the PR without a server checkout. Dirty trees and
push failures fail the phase; read-only phases never push.

## Account limits and backlog

A headless connector turn that reports a usage or spend limit fails with
`agent_rate_limited`; PRFactory receives `/fail` with `shouldRetry=true`. The
backend's default account is then blocked for new claims until the reported
reset (one hour when none is reported) while other backends continue. An
interactive turn (Herdr, terminal tabs) keeps its live TUI instead: the limit
blocks the account, and the TUI's later native completion settles the job; its
turn deadline restarts at the reset (at most 24 hours when unknown), so a TUI
that never resumes is still quarantined.
Accepted-but-unfinished teams are capped (10) and polling asks only for free
slots. Pruning never removes turns of accepted teams or unresumed parks.
One connection works several work items at once, up to the worker token's
`MaxConcurrentWorkItems` on the server. When the server advertises that cap
(`maxConcurrentWorkItems`/`activeWorkItems` on poll) and it is reached,
intake pauses for that tick and `atf prfactory status` shows the last seen limit;
a 409 at claim also stops claiming for the tick.

## Human questions during a turn

Managed children do not offer `request_human_input` unless their root job has a
working human-wait path. Local jobs have no such path, and PRFactory jobs do not
offer it while the server lacks the `questionId` answer-command wire. A direct
call is rejected with guidance to proceed using best judgement or record open
questions in the artefact. Existing durable question records can still be
resumed and streamed; ATF maps their internal statuses to PRFactory's coarse
lifecycle values.

Registration advertises `authority-disposition-v1`, `remote-publication-v1`,
`workspace-continuity-v1`, `blob-attachments-v1` and `base-wip-v1`.
Not advertised yet: `human-wait-v1` (needs the server's `questionId` wire), readiness probes,
external-member human waits and multi-repository work.

## Base freshness and WIP handover

ATF discovers `base-wip-v1` through `GET /api/worker/capabilities`. When it is
absent, ATF does not call the legacy WIP, base-conflict or release routes. The
PRFactory server advertises this capability (PRFactory #263).

For a capable server, the daemon fetches the claimed base branch before the
lead's first turn. A clean lead with no commits moves to the new base; a lead
with committed work rebases. A durable original HEAD lets a restart abort and
restore an interrupted rebase. Conflicts stop dispatch, retain files and are
reported with repository-relative paths. The repository-result reports the
recorded and current base SHAs and refresh action.

The daemon pushes the committed lead tip to `wip/<machine-slug>/<ticket-key>`
when it changes and at phase end, then records a server-verified receipt.
Dirty buffers are never represented by that receipt. A release requires clean
lead and child workspaces, a matching WIP receipt and an explicit server
acknowledgement bound to the acceptance identity. An adopting host verifies
that its requested WIP branch tip equals the exact server-provided SHA before
dispatch. Cleanup of owned, inactive worktrees requires both receipts and a
retention interval; ordinary phase completion does not erase them.

## Phase artefacts

Built-in phases collect top-level Markdown and HTML documents from the latest
lead job's ticket folder. Decomposition additionally collects JSON. Filename
stems determine kinds, including `qa`/`questions` → `qa-po` for TicketRefinement
and `qa-dev` otherwise, and numbered plan/code review reports. Custom steps
upload only their named `ExpectedOutput` as `custom-step`, matching the worker's
custom-step result contract. Child documents are staged for the lead's
finalization turn; the lead chooses the canonical copies in its ticket folder.

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

## Diff and binary attachments

ATF discovers `blob-attachments-v1` through
`GET /api/work-item-blobs/capabilities`. Servers without it receive no blob
uploads. After a writable phase publishes, ATF generates `changes.patch` using
`git diff base...head` from the publication's frozen SHAs. Patches above 10 MiB
are truncated with `IsTruncated` and a binary-change summary. Scratch work
never produces a Git diff.

Agents may put PNG, JPEG, WebP, PDF, TXT, PATCH and DIFF files directly in
`<TicketArtefactFolder>/attachments/`. Unsupported extensions are ignored;
unsafe paths and symlinks fail the phase. Untracked files of these types in
that folder are upload output and do not block publication; tracked edits
still require commits. Each file is limited to 10 MiB and the complete batch,
including the patch, to 50 MiB. Oversized attachments fail visibly before any
blob from the batch is sent.

SQLite freezes the entire batch, including multipart bytes, stable ClientKeys,
machine/job/lease identity, attempt, SHA-256 and media types, before the first
send. Restarts and lost responses replay identical requests even if local files
change. Every upload passes the authority gate and all uploads precede
completion. HTTP 409 and other terminal rejections persist a Fail diagnostic;
transient failures retry. Loss of server capability holds already-frozen pending
uploads instead of completing without them.
