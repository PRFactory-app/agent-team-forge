# Reference stability lessons: AgentTeamForge gap audit

2026-09-26. Read-only source audit, except this report. ATF baseline: `main`
`5294bc6f86b8fbba38fb8032a017e1159ed3b6ec`. Reference baseline:
`c2868be1ca5c84bf6dc0b0b222be7d935671e7ef` in
`/home/mikael/code/github/agentic-coder-teams-mcp`.

The largest remaining gaps are conservative process reconciliation, interactive
Codex binding in the presence of native subagents, managed-child communication
context, and inbox response bounds. Much of the earlier core-stability plan is
already implemented at this ATF snapshot. Do not reopen those slices wholesale.

## Scope and evidence

Inspected `git log --merges --first-parent -n 15` and `git log -n 60 --stat`
in the reference, followed by focused source/history reads. The merge-only log
ends at July PR #35 because newer PRs were squash-merged. Queried GitHub's last
12 merged PRs with a token obtained explicitly for `mikaelliljedahl`; never used
`mliljedahl-auxality`. The local `gh` wrapper printed setup output into token
capture on the first attempt; the successful retry used the installed `gh`
binary directly. PR bodies supplied intent; source supplied the assessment.

Recent set: #60–#71 (September 15–26). Also inspected older #36, #38, #53 and
#55 and the earlier compact-output commit because they directly address the
requested delivery, binding and token topics. PR numbers below link to the
reference repository, not ATF. `R:` paths are relative to the reference root;
`A:` paths are relative to the pinned ATF worktree. Every citation is a source
location at those snapshots. Status means source coverage, not live verification.

Read `AGENTS.md`, `HANDOFF.md` and
`docs/plans/core-stability.md`, including “Flows checked without a new slice.”
No build, tests, published binary, live agents or host configuration changes were
run. Proposed tests below are not claimed results. Only this file is committed.

## Lesson matrix

P0: potentially concurrent execution of one native session. P1: broken reporting,
completion observation or reliable reads. P2: bounded optimization or hygiene.
For covered/not-applicable rows, priority describes the lesson's importance;
it does not request implementation work.

| Lesson / problem and reference mechanism | Reference PR and source evidence | ATF status and evidence | Smallest proposed adjustment | Priority |
| --- | --- | --- | --- | --- |
| Codex selected built-in collaboration tools instead of the MCP that reaches its lead. Append exact MCP names on spawn **and resume**, explain upstream/downstream routing, and qualify external-member guidance too. | [#71](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/71). `R:src/claude_teams/backends/codex.py:398`, `:437`, `:656` defines the sanitizer and hint. | **gap**. `A:src/AgentTeamForge.Business/Features/Agents/Backends/CodexExecBackend.cs:52` passes the instruction unchanged; `A:src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrInteractiveBackend.cs:123` adds only correlation. `A:src/AgentTeamForge.DAL/Features/External/ExternalMemberStore.cs:10` uses bare tool names in join guidance. More fundamentally, managed children have no injected parent messaging credential; `A:src/AgentTeamForge.Host/Features/Jobs/JobsMcpBridge.cs:124` creates a new lead identity and `:185` exposes send_message only to external members. | Add an ATF-specific child context and short hint; do not paste the reference hint and advertise a nonexistent upstream send route. Reuse ticket/member messaging for explicit child-to-parent reports if needed. Qualify tools for the actual configured server key (`agentteamforge` by default, `A:src/AgentTeamForge.Host/Features/Setup/ClientSetup.cs:11`). See slice 3. | P1 |
| Native notices wake idle sessions without putting message bodies in prompts; durable inbox first, coalescing/backoff, generation guards and environment isolation. | [#70](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70). `R:src/claude_teams/native_wake.py:204`; `R:src/claude_teams/backends/process_base.py:104`. | **covered** for delivery mechanics. `A:src/AgentTeamForge.Business/Features/Wake/WakeCoordinator.cs:54` scans committed jobs/external messages, `:72` suppresses outstanding notices, `:89` checks registration, `:93` sends counts only. `A:src/AgentTeamForge.Host/Features/Setup/DaemonEnvironment.cs:12` drops inherited wake/session identity in the normal launcher; `A:src/AgentTeamForge.Business/Features/Agents/Terminals/LaunchEnvironment.cs:20` excludes it for Herdr. | Preserve the daemon-owned notice path. Wording is **partial**: `WakeCoordinator.cs:95` uses bare names and `JobsMcpBridge.cs:169` explicitly recommends polling despite native wake. Fix guidance with slice 3; retain manual inspection as fallback. Do not port a watcher service. | P1 |
| An unreadable process token is not proof of exit; resuming under that assumption overlaps a live conversation. Darwin needed a real creation-token reader. | [#69](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/69). `R:src/claude_teams/backends/process_manager.py:282`, `:355`, `:496` distinguishes alive/unverified from dead. | **partial**. Ownership-aware kill exists, but `A:src/AgentTeamForge.Business/Features/Agents/Backends/OrphanedBackendProcess.cs:49` ignores per-PID I/O/access failures and `:63` returns false. `A:src/AgentTeamForge.Business/Features/Jobs/FollowUpJob.cs:64` can then clear a terminal peer's fence. Its stored BackendPid is checked for presence, not used to resolve this unreadable-process case. | Keep the fence when a previously owned PID is live but its identity cannot be read. Prove exit or PID reuse using stored launch identity before clearing. Do not make every unrelated inaccessible `/proc` entry block the entire daemon. Linux first; Darwin's analogous null-arguments path at `OrphanedBackendProcess.cs:32` needs separate platform verification. | P0 |
| Hard reviews warranted Astra at low effort instead of Sol xhigh; avoid paying for unhelpful effort/context expansion. | [#68](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/68). `R:src/claude_teams/backends/codex.py:137`; `R:src/claude_teams/backends/pi.py:195`. | **covered**. `A:src/AgentTeamForge.Business/Features/Jobs/ModelSelection.cs:15` maps xhigh to Astra/low. | None. Treat the reference's benchmark rationale as historical PR rationale, not a new benchmark result from this audit. | P2 |
| Raise the GPT-6 effort ladder; remove the obsolete Pi high-fast tier explicitly rather than silently using a default. | [#67](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/67). `R:src/claude_teams/backends/pi.py:195`, `:209`. | **covered**. `A:src/AgentTeamForge.Business/Features/Jobs/ModelSelection.cs:8` and its Resolve implementation preserve tier-owned effort and retired-tier rejection. Production discovery is now wired at `A:src/AgentTeamForge.Host/Hosting/DaemonCommand.cs:181`. | None; the old core-stability slice about uninjected model discovery is superseded on this baseline. | P2 |
| Replace stale GPT-5.6 tier targets with GPT-6 Sol/Luna while retaining raw model selection. | [#66](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/66). `R:src/claude_teams/backends/codex.py:137`, `R:src/claude_teams/backends/pi.py:195`. | **covered**. `A:src/AgentTeamForge.Business/Features/Jobs/ModelSelection.cs:11`. | None; keep one shared ATF mapping rather than copying per-backend tables. | P2 |
| Long-lived terminal servers lose caller launcher settings; nested agents then launch differently. Carry explicit launcher policy in per-child MCP configuration. | [#65](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/65). `R:src/claude_teams/backends/process_manager.py:108`; `R:src/claude_teams/backends/codex.py:508` onward. | **partial**. ATF correctly centralizes mode selection in the daemon (`A:src/AgentTeamForge.Host/Hosting/DaemonCommand.cs:139`), so nested clients using that daemon need no copied Herdr/tmux policy. But launches do not inject child MCP endpoint/state-dir/parent context (`A:src/AgentTeamForge.Business/Features/Agents/Backends/CodexExecBackend.cs:34`, `:55`; `A:src/AgentTeamForge.Business/Features/Jobs/DispatchJob.cs:373`). | Carry only the selected ATF endpoint/state directory and child identity in per-launch configuration. Keep terminal selection at the daemon; preserve the environment denylist. Fold into slice 3. | P1 |
| Noninteractive helpers inherited the MCP stdin pipe and hung. Close/detach stdin for helpers, preserve real TUI stdin. | [#64](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/64). `R:src/claude_teams/server_simple.py:6799` uses DEVNULL; model discovery does likewise in `R:src/claude_teams/backends/codex.py:65`. | **partial**, with lower Linux exposure: normal daemon launch redirects stdin to `/dev/null` (`A:src/AgentTeamForge.Host/Features/Setup/SetupCommand.cs:215`); model discovery explicitly closes it (`A:src/AgentTeamForge.Business/Features/Jobs/BackendModelDiscovery.cs:67`, `:80`). Git/Herdr helper builders still inherit stdin (`A:src/AgentTeamForge.Business/Features/Jobs/JobWorktree.cs:71`; `A:src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrCommands.cs:17`). | Close helper stdin explicitly at the process runner/start sites. Do not close a real interactive agent's TTY or the intentional headless prompt pipe. No current Linux hang reproduced. | P2 |
| Edge-only watchers missed already parked agents; SubagentStop incorrectly overwrote a parent's state. Read parked state plus per-reader acknowledgement, ignore native child-stop churn. | [#63](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/63). `R:src/claude_teams/cli.py:472`, `:733`; `R:src/claude_teams/hooks.py:22`. | **covered** by different ownership of state. ATF scans durable unread state, not filesystem edges (`A:src/AgentTeamForge.Business/Features/Wake/WakeCoordinator.cs:54`). Turn completion requires correlated native completion (`A:src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrInteractiveBackend.cs:152`, `:180`). No SubagentStop marker writer to port. | None. Preserve catch-up after restart and do not add state-marker files/hooks. Transcript polling costs are a separate issue below. | P1 |
| Herdr tabs from unrelated repositories piled into the active workspace. Derive a label from git common-dir and create/reuse under a lock. | [#62](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/62). `R:src/claude_teams/backends/process_manager.py:2890`, `:2903`. | **not applicable** to the same failure. ATF creates an owned session/workspace and addresses its workspace explicitly (`A:src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrTerminal.cs:47`, `:108`, `:138`). | Keep current ownership isolation. Repo grouping is optional UX work, not a stability requirement; do not adopt human workspaces merely to mirror the reference. | P2 |
| Machine-specific Stop hooks were committed into shared project settings and broke other machines. Move to settings.local.json and migrate legacy hooks. | [#61](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/61). `R:src/claude_teams/server_simple.py:6751`. | **not applicable**. ATF's wake uses daemon registrations/native transports (`A:src/AgentTeamForge.Business/Features/Wake/WakeCoordinator.cs:23`), not installed per-repository Stop hooks. | No hook migration layer. Keep personal endpoints/credentials out of tracked project files when adding child context. | P2 |
| Pi needed an optional low-latency tier without changing the normal ladder. | [#60](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/60), subsequently amended by #66/#67. `R:src/claude_teams/backends/pi.py:199`, `:209`. | **covered** for current semantics. `A:src/AgentTeamForge.Business/Features/Jobs/ModelSelection.cs:38` exposes Pi-only medium-fast; Resolve maps it to Sol/medium. | Do not resurrect the superseded high-fast entry. | P2 |
| A late receipt for old request A caused new request B to be reported delivered without sending B. Reconcile by nonce to its durable row and compare the complete request fingerprint. | [#53](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/53), based on [#36](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/36). `R:src/claude_teams/server_simple.py:2375`, `:2395`, `:5419`. | **covered** for ATF's smaller contract. `A:src/AgentTeamForge.DAL/Features/Jobs/JobStore.cs:89` scopes key lookup and compares fingerprints; `:239` fences session recording to the started run. Interactive submission is once-only (`A:src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrInteractiveBackend.cs:134`). | Preserve same-key recovery and distinct-job identity. Do not add cross-key receipt aliases, agent-level pending nonces or the reference's lease/delivery-store layer. | P1 |
| Busy/idle follow-up must not treat transcript silence as authority to kill/resume a live worker. | [#55](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/55). `R:src/claude_teams/server_simple.py:4594`, `:4644` permits an authoritative parked marker, not silence. | **covered**, subject to the unreadable-process gap above. `A:src/AgentTeamForge.Business/Features/Jobs/FollowUpJob.cs:41` refuses busy without interrupt; `:71` checks the whole session fence. `A:src/AgentTeamForge.DAL/Features/Jobs/JobStore.cs:187` prevents claiming fenced siblings. | Retain refusal plus explicit interrupt. No busy-message queue or kill-on-stall heuristic. | P1 |
| Unconfirmed/stale deliveries must survive response loss and must not be expired into permission to resend. | #36/#53. `R:src/claude_teams/server_simple.py:2334`, `:2356`, `:5409` retains uncertainty barriers; `:5426` commits before waiting. | **covered** through job state. `A:src/AgentTeamForge.DAL/Features/Jobs/JobStore.cs:326` expires queued jobs; `:372` fences uncertain outcomes; `:416` quarantines restart uncertainty. `A:src/AgentTeamForge.Business/Features/Jobs/DispatchJob.cs:207` sweeps queue TTL. `A:src/AgentTeamForge.Host/Features/Jobs/JobsMcpBridge.cs:107` retries only a provably unsent request. | Do not auto-expire reconciliation fences or build deliver_pending. Keep explicit stop/reconciliation for uncertain live sessions. | P1 |
| Correlation markers alone become ambiguous when a Codex native subagent inherits its parent's prompt/history. | #36 binding machinery; **not fixed by #71**. `R:src/claude_teams/agent_output.py:1266` scans marker matches; `:1283` returns binding_ambiguous. Older identity work: commit `fd6c715`. | **partial**. Headless Codex takes thread.started identity from its own stdout (`A:src/AgentTeamForge.Business/Features/Agents/Backends/CodexExecBackend.cs:328`). Interactive scanning instead searches all rollouts and returns null for a second matching file (`A:src/AgentTeamForge.Business/Features/Agents/Terminals/InteractiveTranscriptReader.cs:20`, `:38`, `:84`). Only resume filters an expected native ID. | Bind the actual launched interactive thread, exclude native subagent rollouts using verified source/parent metadata, and retain the verified path/ID for that run. Return a distinct ambiguity reason. Never choose newest/first match. See slice 2. | P1 |
| Full transcript/status payloads waste tokens; compact status, bounded tails and delta inbox cursors avoid repeated history. | Earlier commit [85474b9](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/commit/85474b965e90f74e7095ee251ae2cc675f83f58a), extended by #36/#38; not falsely attributed to the newest PRs. `R:src/claude_teams/server_simple.py:1959`, `:3812`, `:6297`; `R:src/claude_teams/agent_output.py:662`. | **partial**. ATF list pages are bounded/no result bodies (`A:src/AgentTeamForge.Business/Features/Jobs/ListJobs.cs:16`, `:45`), activity preview is 160 chars (`A:src/AgentTeamForge.Business/Features/Jobs/JobLogs.cs:123`), logs have absolute offsets/truncation (`:18`). But `get_job` always returns the full result (`A:src/AgentTeamForge.Business/Features/Jobs/GetJob.cs:46`), and log reads emit both UTF-8 and base64 (`JobLogs.cs:43`). | Provide an optional compact status read and make the MCP output projection text-first with opt-in raw bytes. Preserve explicit full-result access and existing output cursors. Avoid a second status persistence system. | P2 |
| Delta reads need bounded work and bounded responses, not just a count limit. | Earlier compact-output commit; [#38](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/38). `R:src/claude_teams/server_simple.py:3812` defines watermarks/nonconsuming limit=0. | **partial**, with a concrete ATF frame failure. `A:src/AgentTeamForge.DAL/Features/External/ExternalMemberStore.cs:423` loads all historical bodies even for limit=0, then takes a page at `:453`. `A:src/AgentTeamForge.Business/Features/External/ExternalTeam.cs:140` defaults to 50 untruncated messages; full bypasses the count bound. Each message can be 65,536 chars (`:21`), exceeding the 2 MiB IPC frame cap with a legal default page (`A:src/AgentTeamForge.Business/SpikeProfile.cs:13`). Cursors advance before transmission (`ExternalMemberStore.cs:462`); the client rejects the frame (`A:src/AgentTeamForge.Host/Transport/Frames.cs:33`). | Bound the page by serialized UTF-8 bytes before committing its read cursor, page even with full requested, filter/limit in SQL, and answer limit=0 from cursor/count metadata without loading bodies. Do not just increase the frame cap or silently consume omitted messages. | P1 |
| Repeated full transcript discovery/scanning becomes expensive as sessions grow. | #36's binding cache and bounded scans: `R:src/claude_teams/agent_output.py:1238`, `:1251`. | **gap** in interactive reads. `A:src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrInteractiveBackend.cs:165`, `:211` scans every 250 ms; `InteractiveTranscriptReader.cs:22` discovers up to 200 candidates, `:136` rereads whole files, and `:123` rejects files above 32 MiB outright. Progress-count deduplication avoids repeated log writes but not scanning. | After slice 2, retain one verified path and incremental complete-line offset per run; handle partial lines/rotation without misattribution. Read the correlated tail of large files rather than classifying a long-lived session as unobservable merely due to size. | P2 |
| Stall reporting is advisory; silence cannot justify a destructive action. | #55 and compact status: `R:src/claude_teams/server_simple.py:298`, `:1981`. | **partial** for visibility, **covered** for avoiding silence-based success/resend. ATF exposes durable running state/activity rather than heartbeat age (`A:src/AgentTeamForge.Business/Features/Jobs/ListJobs.cs:11`, `:58`). Explicit runtime deadlines exist (`A:src/AgentTeamForge.Business/Features/Jobs/DispatchJob.cs:342`); Herdr quiet-idle becomes uncertainty without replay (`HerdrInteractiveBackend.cs:198`). | No separate stall engine. If useful, expose last observed activity age in the compact status slice, clearly distinct from durable status and explicit timeout. Never use it to clear a session fence. | P2 |
| External members are authenticated inbox participants, not managed processes; wake is best effort and leave/rejoin must not resurrect revoked membership. | #38/#70/#71. `R:src/claude_teams/server_simple.py:3560`, `:4089`; `R:src/claude_teams/native_wake.py:222`. | **covered** for basic membership/wake. `A:src/AgentTeamForge.DAL/Features/External/ExternalMemberStore.cs:149` handles transactional join replay/revocation, `A:src/AgentTeamForge.Business/Features/External/ExternalTeam.cs:77` joins and `:95` token-authenticates send; `A:src/AgentTeamForge.Business/Features/Wake/WakeCoordinator.cs:54` includes external messages. Existing `A:tests/AgentTeamForge.Tests/Features/External/ExternalTeamTests.cs:95` covers commit/read/wake/cursor behavior. | Keep inbox semantics and no process-kill authority for external members. Fix guidance and page bounds in slices 3/4. Do not import guaranteed downstream process delivery for these members. | P1 |

## Binding diagnosis and child behavior

The reported reference `binding_ambiguous` is consistent with inherited history
producing multiple matching rollouts. This audit did not launch agents or inspect
private user transcripts, so it does not claim a live reproduction or a specific
Codex metadata schema. Both the reference and ATF fail closed when multiple
matches are visible; choosing the latest rollout would turn a visible failure
into wrong-agent attribution. ATF's 200-candidate cutoff can additionally hide a
second match, so uniqueness inside that sample is not proof of launch identity.

ATF already learns a native ID during an interactive run, but its next read still
uses `launch.ResumeSessionId`, which remains null for a fresh launch. Thus a later
native subagent can invalidate a previously successful lookup. Preserve identity
from the owned launch/verified first binding, validate native ancestry against real
CLI fixtures, and make an unresolved first binding explicit. Headless stdout
binding does not need the broad transcript-discovery machinery.

Child guidance must describe ATF's actual contract: final output completes a job
only when the backend emits authoritative completion; a textual `DONE` alone is
not completion evidence. A child should finish the turn after its report rather
than spin on an inbox. New managed work arrives through `follow_up`; busy work
requires explicit interrupt. A nested lead uses ATF MCP tools for ATF children;
Codex's built-in collaboration tools only address Codex-native children. Mention
`DONE/FAILED`, commit and test results as the repository's reporting convention,
not as a parser or scheduler protocol. Enable upstream messages through a real
member identity before telling a managed child it can send them.

## Recommended slices (seven)

1. **P0 — Keep session fences on unreadable owned-process evidence.** Touch
   `src/AgentTeamForge.Business/Features/Agents/Backends/OrphanedBackendProcess.cs`,
   `src/AgentTeamForge.Business/Features/Jobs/FollowUpJob.cs`; inspect the other
   fence-clearing calls in `DispatchJob.cs` without changing proven-stop paths.
   Use existing run/PID identity fields,
   adding persisted creation identity only if needed. Extend
   `tests/AgentTeamForge.Tests/Features/Jobs/SessionFenceTests.cs`: a terminal
   uncertain run whose known PID is live but identity read fails must block its
   sibling across restart; proven exit/reuse allows progress; an unrelated
   inaccessible PID does not globally block jobs. Never signal an unverified PID.

2. **P1 — Bind interactive Codex to its actual native thread.** Touch
   `src/AgentTeamForge.Business/Features/Agents/Terminals/InteractiveTranscriptReader.cs`,
   `HerdrInteractiveBackend.cs` and, only if needed to obtain launch identity,
   `HerdrAgentControl.cs`. Extend `InteractiveTranscriptReaderTests.cs` and
   `HerdrInteractiveBackendTests.cs`: parent and native-child rollouts share the
   marker; only the proven parent completes. Repeat with the child appearing
   after first binding, >200 decoys, true ambiguity, resume and later human turns.
   Preserve uncertainty if no authoritative parent can be identified.

3. **P1 — Supply minimal managed-child identity and accurate tool hints.** Touch
   `src/AgentTeamForge.Business/Features/Jobs/DispatchJob.cs`, backend request and
   launch builders under `Features/Agents`,
   `src/AgentTeamForge.Host/Features/Jobs/JobsMcpBridge.cs`,
   `src/AgentTeamForge.Business/Features/Wake/WakeCoordinator.cs`, and
   `src/AgentTeamForge.DAL/Features/External/ExternalMemberStore.cs` join guidance.
   Carry the actual daemon endpoint/state-dir in private per-launch configuration.
   Reuse the existing member-ticket route for upstream messaging instead of adding
   a new messaging bus; keep nested lead identity distinct from parent membership.
   Use the configured MCP prefix and repeat the short routing hint on follow-up.
   Tests: fake spawn/resume captures the same intended endpoint and correct parent
   membership; two nested leads cannot send into each other's inbox; external
   join and wake instructions select the correct registered tool family. Assert
   routing/identity behavior, not exact prose. Until that route exists, use final
   job results and do not advertise `send_message(to="team-lead")` as functional.

4. **P1 — Bound external reads before advancing cursors.** Touch
   `src/AgentTeamForge.DAL/Features/External/ExternalMemberStore.cs`,
   `src/AgentTeamForge.Business/Features/External/ExternalTeam.cs` and the IPC/MCP
   projection only as needed. Extend `ExternalTeamTests.cs` and
   `tests/AgentTeamForge.Tests/Scenarios/ExternalJoinScenarios.cs`: 50 maximum-size
   legal messages, Unicode/JSON-escaped text, full reads and limit=0 all stay within
   transport bounds; continuation drains every message once; omitted rows remain
   unread. Use SQL filtering/paging and metadata-only watermarks. Existing
   ReadTeam/sender-cursor tracking is an adjacent known minor, not a second slice.

5. **P2 — Make inspection economical for an LLM caller.** Touch
   `src/AgentTeamForge.Business/Features/Jobs/GetJob.cs`, `JobLogs.cs`,
   `src/AgentTeamForge.Host/Features/Jobs/JobsMcpBridge.cs` and response DTOs as
   needed. Keep the current bounded list. Add compact inspection without a full
   result; send text-only log pages by default at the MCP boundary, retaining raw
   bytes explicitly. Optionally surface observed activity age without changing job
   state. Test a large completed result, truncation metadata and continuation
   through trimmed/multibyte log boundaries; explicit full/raw access still works.

6. **P2 — Read a bound interactive transcript incrementally.** Touch
   `InteractiveTranscriptReader.cs` and `HerdrInteractiveBackend.cs` after slice 2.
   Store a per-run path/identity/offset, not a global scanner framework. Extend
   `InteractiveTranscriptReaderTests.cs`: partial JSONL lines, append, replacement,
   truncation and a >32 MiB prior history retain correct turn boundaries and emit
   each progress record once. Verify unchanged reads do not rescan all history.

7. **P2 — Detach stdin for noninteractive helper processes.** Touch
   `src/AgentTeamForge.Business/Features/Jobs/JobWorktree.cs`,
   `src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrCommands.cs`,
   `HerdrProcessRunner.cs`, and inspect the analogous Host helper start sites.
   A small helper fixture with the parent pipe left open must observe EOF and
   exit promptly; existing headless prompt delivery and interactive launch tests
   must retain their intended stdin. Keep this local; no cross-platform launcher
   rewrite and no claim that the reference's Windows hang was reproduced here.

## Work deliberately not proposed

Do not port reference delivery leases, cross-key aliases, pending-drain queues,
watch processes, Stop-hook installers, marker acknowledgements or automatic
workspace adoption. Durable ATF job rows, explicit busy refusal, session fences
and native notices already replace those mechanisms. Do not expire uncertainty
into a retry or turn inactivity into kill permission. External sends remain
inbox operations; they do not need a managed-process delivery protocol.

The original core-stability defects around repeat Herdr submission, interim-text
completion, sibling fences, bridge reconnect, Claude profile transcript lookup
and production model discovery have changes in current code; this report cites
the current mechanisms rather than repeating the old plan as open findings.
Platform validation remains separate: the reference's macOS and Windows fixes
are lessons, not evidence that ATF works on those machines.
