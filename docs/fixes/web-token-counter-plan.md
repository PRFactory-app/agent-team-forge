# Web console: token counter per agent, lead and total — plan

Branch `feat/web-token-counter` (on top of `feat/web-session-layout`). Request: show token usage next to
each agent (job) node, on each lead card (the lead's **own** usage), per lead group (group sum) and at
the top of the overview (total). Compact numbers (`12.3k`, `1.2M`), with a tooltip breakdown.

## 1. What exists today

- **ATF captures no usage anywhere.** `grep usage|input_tokens|token_count` only hits rate-limit text
  (`ClaudeCodeBackend.cs:393`, `InteractiveTranscriptReader.cs:366`, `AccountAdmission.cs:13-43`,
  `Schema.cs:277`). Headless stream parsing drops the `usage` object (`ClaudeCodeBackend.cs:132,364-398`,
  `ClaudeResult` has no usage field).
- **Native transcripts already hold everything**, and ATF already knows how to find them:
  - Claude: `<ClaudeConfigRoot>/projects/**/<session_id>.jsonl` (`InteractiveTranscriptReader.cs:44-60`,
    root from `ClaudeConfigRoot.cs:9-18`). Headless `claude -p --session-id/--resume`
    (`ClaudeCodeBackend.cs:74`) persists the same file (no `--no-session-persistence` in the repo).
    Every `type:"assistant"` line has `message.usage {input_tokens, output_tokens,
    cache_read_input_tokens, cache_creation_input_tokens}`. **One API message is split over several
    lines with the same `message.id` and the same usage** (checked on a live transcript) — dedupe by
    `message.id`, otherwise the count is 2-3x too high.
  - Codex: `<CODEX_HOME>/sessions/**/rollout-*-<thread>.jsonl` (`InteractiveTranscriptReader.cs:24-41`,
    home from `CodexPaths.Home`). `codex exec` and `codex exec resume` (`CodexExecBackend.cs:60`) write
    the same rollout. `event_msg` with `payload.type:"token_count"` carries
    `info.total_token_usage {input_tokens, cached_input_tokens, output_tokens, reasoning_output_tokens,
    total_tokens}` — **cumulative for the thread**, so take the last one; never sum. Note that
    `input_tokens` already includes `cached_input_tokens`.
  - Pi: `pi --session-id <guid>` / `--session <id>` (`PiBackend.cs:62-70`) writes a session JSONL under
    the pi sessions dir (the interactive path uses `launch.PiSessionDirectory`,
    `InteractiveTranscriptReader.cs:163-166`). Assistant `message` lines carry `usage {input, output,
    cacheRead, cacheWrite, totalTokens}`. Pi is not installed on this machine, so the format is
    **best effort**: parse it if found, else return null.
- **Job → session**: `JobSummary.SessionId`, `Backend`, `Cwd`, `ParentJobId` (`ListJobs.cs:78-106`).
  A follow-up is a new job with the same native `session_id` (`JobStore.cs:167-183`). The transcript is
  per **session**, not per job.
- **Lead → native session**:
  - A **Codex lead** registers wake with `address = CODEX_THREAD_ID` and `home = CODEX_HOME`
    (`HostSessionWake.cs:20-25`). `lead_sessions.wake_key` → `wake_targets(kind,address,home)`
    (`Schema.cs:68-77,125-132`). The rollout can be read today with no schema change.
  - A **Claude lead** registers a channel wake with `address = channel socket` and `home = pid`
    (`HostSessionWake.cs:57-75`), so ATF does **not** know its Claude session id today. But Claude Code
    exports `CLAUDE_CODE_SESSION_ID` (and `CLAUDE_CONFIG_DIR` when set) to its child processes (checked in
    this session's env; `LaunchEnvironment.cs:34` strips it for managed children for this reason). The MCP
    bridge is such a child and sends `SessionStart`/`SessionResume` (`JobsMcpBridge.cs:164,302`).
  - A closed lead has `wake_key = NULL` (`LeadSessionStore.cs:113`), so its Codex binding is gone → "—".

## 2. Design

**Compute on demand in the web list call, with an in-memory incremental cache. Nothing is persisted per
turn.** Transcripts are append-only JSONL, so the cache keeps `(path, bytesRead, totals, seenIds)` per
session and each poll parses only the appended tail. That is correct, cheap on the 5 s poll, and needs no
migration for managed agents. Persisting at turn completion would need a hook in three backends plus
interactive mode, and would still be wrong for a running turn.

### 2a. `SessionTokenUsage` (Business, one new file)

`src/AgentTeamForge.Business/Features/Usage/SessionTokenUsage.cs`, a singleton:

```csharp
public sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheWrite)
{ public long Total => Input + Output + CacheRead + CacheWrite; }

public TokenUsage? Read(string kind /* claude|codex|pi */, string sessionId, string? home);
```

- **Locate** the file once per `(kind, sessionId)` using the same globs as `InteractiveTranscriptReader`
  (reuse `ReadClaudeSession`/`ReadCodexThread` path logic via a small shared `internal static` locator;
  do not duplicate the header check). Cache the path. Cache a miss for 30 s, so an unknown session does
  not trigger a recursive `sessions/**` scan on every poll.
- **Incremental parse**: on `Read`, if `length == bytesRead` return cached totals. If `length < bytesRead`
  (rewritten file), reset. Otherwise read from `bytesRead` up to the last `\n` (never a partial line),
  bounded to 8 MB per call. Update the totals and store the new offset. The first read of a huge file
  catches up over a few polls. That is fine, and later polls are free.
- **Per kind**:
  - Claude: `type=="assistant"` → `message.id` into a `HashSet` (per session) → add `usage` once per
    id. `Input=input_tokens`, `CacheRead=cache_read_input_tokens`,
    `CacheWrite=cache_creation_input_tokens`, `Output=output_tokens`. Main file only; sidechain
    files under `<session>/subagents/` are ignored in v1 (see §5).
  - Codex: keep the last `token_count.info.total_token_usage` seen. `Input = input_tokens -
    cached_input_tokens`, `CacheRead = cached_input_tokens`, `Output = output_tokens`, `CacheWrite = 0`.
  - Pi: sum `usage.input/output/cacheRead/cacheWrite` on assistant messages.
- Malformed lines are skipped. IO errors return the last cached value (or null). It never throws into
  the list call.
- **Bounded memory**: at most 512 cached sessions (LRU drop). The Claude id set is per session and only
  grows with message count.
- **Homes**: managed agents use the daemon's env, the same as the reader (`ClaudeConfigRoot.Resolve(
  Environment.GetEnvironmentVariable, cwd)`, `CodexPaths.Home(...)`). A lead uses its stored home.

### 2b. Avoid double counting: usage is per *session*

- Each job gets `session_tokens`: the cumulative usage of its native session (null if unknown).
  Follow-up jobs share it.
- The UI dedupes by `session_id` when it sums (group and total). A job card shows its session's
  number. The tooltip says "session total (shared by N jobs)" when N > 1.
- A per-turn delta is **out of scope**. It would need snapshots at turn boundaries.

### 2c. Lead's own usage

- **Codex lead**: `lead_sessions.wake_key → wake_targets` where `kind='codex'` → `Read("codex",
  address, home)`. No schema change.
- **Claude lead** (small, and in scope because Claude is the usual orchestrator): add nullable
  `lead_sessions.native_kind`, `native_session_id` and `native_home` (a new migration in `Schema.cs`).
  - The MCP bridge sends `NativeSessionId = CLAUDE_CODE_SESSION_ID` and
    `NativeHome = CLAUDE_CONFIG_DIR ?? ~/.claude` (or `CODEX_THREAD_ID`/`CODEX_HOME` for Codex) on
    `SessionStart`/`SessionResume`.
  - `LeadSessionStore.Start/Resume` stores them, overwriting on resume, because `/clear` or a resume can
    change the id.
  - When these columns are set they take precedence over the wake target. That also makes Codex leads
    work without a registered wake.
- If neither source exists (older bridge, closed lead, "No lead session", PRFactory):
  `lead_tokens: null` → the UI shows `—`, with the tooltip "lead usage unknown".
- **Verify first** (the backend worker does this in 2 minutes): an MCP server started by Claude Code sees
  `CLAUDE_CODE_SESSION_ID`. If it does not, drop the Claude part of slice 2 and ship Codex-lead only.

### 2d. API fields (snake_case, as today)

- `GET /api/jobs` sets a new `IpcRequest.IncludeUsage = true` (only the web route, so MCP `list_jobs` and
  the CLI stay unchanged and cheap).
- `JobSummary.SessionTokens` → `session_tokens: {input, output, cache_read, cache_write, total} | null`.
  Filled in `JobsEndpoint` next to `WithLocations` (`JobsEndpoint.cs:333,443`) for jobs with a
  `session_id` and backend in `claude|codex|pi` (`claude-code` → claude).
- `IpcResponse.LeadTokens` → `lead_tokens: { "<lead_session_id>": TokenUsage | null }`. Only the leads
  present on the page are included. A new DAL query `LeadSessionStore.NativeBindings(ids)` returns
  `(session_id, kind, native_id, home)` from the new columns, falling back to the codex wake target.
- Register `TokenUsage` in the JSON source-gen context next to the other response types.

## 3. UI placement (`wwwroot/app.js`, `app.css`, `index.html`)

- Add a helper `fmtTokens(n)`: `<1000` → `n`, `<1e6` → `12.3k`, else `1.2M`.
- Add a helper `tokenTip(u)`: `input 1.2k · output 3.4k · cache read 45k · cache write 2k`.
  - Also `usageSum(list)`, which sums distinct `session_id`s and ignores nulls.
- **Agent node**: in `.card-side` (`app.js:1215-1217`), append `<span class="tokens" title=tokenTip>`
  with `12.3k tok`, before `elapsed`. Show `—` when the job has a `session_id` but the usage is null.
  Show nothing while there is no session yet.
- **Lead card**: add `lead_tokens[lead]` as a `.tokens` span next to `.lead-count`
  (`app.js:1148`), with the label "lead 45k tok" or "lead —".
- **Team header**: append `<span class="team-tokens">` inside `.team-toggle` (`app.js:1113-1117`), with
  `Σ 1.2M`. That is the group sum: the lead's own usage plus the deduped member sessions. Do **not**
  change the `team-toggle` or `team-content` class names (`WebConsoleScenarios.cs:120-121`).
- **Top**: add a 4th stat in the header strip (`index.html:20-21`): `<span class="stat"><span
  class="stat-label">TOKENS · SHOWN</span><strong id="token-total">—</strong></span>`, with a title
  tooltip. It shows the sum over leads on the page plus the deduped job sessions. The existing note at
  `index.html:65` ("cover this page of history") already explains the scope.
- CSS: `.tokens, .team-tokens { font-variant-numeric: tabular-nums; color: var(--muted); }`. It must fit
  the compact wide-tree column and the 680 px rules.

## 4. Slices

1. **BACKEND (Codex): managed-agent usage.** Add `SessionTokenUsage.cs` (locator, incremental parser for
   claude/codex/pi, and cache), `IncludeUsage` on `IpcRequest`, set in the `WebConsoleServer.cs:185`
   route, `JobSummary.SessionTokens`, filling in `JobsEndpoint`, and JSON registration. Wire the singleton
   in the Host composition.
2. **BACKEND (Codex): lead usage.** Add the migration for the `lead_sessions.native_*` columns, bridge env
   capture (`JobsMcpBridge.cs:164,302`), the `IpcRequest.NativeSessionId/NativeHome/NativeKind` fields,
   `LeadSessionStore` start/resume and `NativeBindings`, plus `IpcResponse.LeadTokens`.
   - Codex-lead via the wake target works even if the Claude env check fails.
   - It depends on slice 1 (`SessionTokenUsage`).
3. **FRONTEND (Claude Sonnet): counters.** Add the helpers and the four placements from §3.
   - Write it against the §2d contract. It can run in parallel with slices 1–2, using hand-made JSON.

Each slice gets one opposite-family review (Claude reviews Codex backend, Codex reviews Sonnet frontend).

## 5. Tests (focused, few)

In `tests/AgentTeamForge.Tests`, as unit tests against temporary JSONL files. No real CLIs.

- Claude transcript with 3 assistant lines sharing one `message.id` plus 1 other id → usage counted
  **once per id**. Append a new line, call `Read` again → only the delta is added; a partial trailing
  line is not consumed until it is completed.
- Codex rollout with two `token_count` events → the **last cumulative** value is returned, not the sum;
  `input` excludes cached.
- A truncated/rewritten file (length shrinks) → totals reset and are recomputed.
- Lead binding: `SessionStart` with `NativeSessionId` → `NativeBindings` returns it; `SessionResume` with
  a new id overwrites it; a lead without native columns but with a codex wake target falls back to that.
- Existing `WebConsoleScenarios` stay green. No UI or format assertions.

Out of scope: Claude sub-agent sidechain files, cost in currency, per-turn deltas, and history beyond the
current list page.

## 6. Manual smoke checklist

- [ ] `atf web` with a headless Claude job, a headless Codex job, one interactive (WT or Herdr) job of
  each kind, and a follow-up on one of them.
- [ ] Each job card shows `N tok`, and the tooltip shows input/output/cache read/cache write. The numbers
  are close to what `/cost` (Claude) or the Codex status line shows for that session.
- [ ] The follow-up job and its parent show the **same** number. The team `Σ` and the top total count it
  once.
- [ ] While a job runs, its number grows across polls. Opening DevTools shows `/api/jobs` staying fast
  (<100 ms on the second poll).
- [ ] A Claude lead started with the new bridge shows `lead N tok`, matching its own `/cost`. A Codex lead
  with a registered wake shows its thread usage.
- [ ] A closed lead, "No lead session" and PRFactory show `lead —` or nothing, never `0`.
- [ ] A job that is queued or has no session shows no counter. A job whose transcript has been deleted
  shows `—`.
- [ ] Wide (≥1100 px) and narrow (390 px) layouts show no wrapping or overflow in `.card-side` or the
  team header. Check both themes.
- [ ] MCP `list_jobs` output is unchanged (no `session_tokens`).
