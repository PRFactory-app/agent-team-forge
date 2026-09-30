# Web token counter backend — code review

Reviewer: Claude (read-only). Scope: commit `11b17bd` (backend usage + lead native binding) and merge
`1360687` (migration renumbering, SchemaTests, app.js lead name + Σ). Checked against
`docs/fixes/web-token-counter-plan.md` and `docs/fixes/web-token-counter-backend.md`.

Tests run (Release): `Features.Usage|SchemaTests|Features.Sessions|InteractiveTranscriptReader`,
73 total, 69 passed, 4 failed. The 4 failures are the known Windows timezone/rate-limit cases
(`Claude_limit_reset_times_are_resolved_in_their_zone` ×3,
`Claude_monthly_spend_limit_maps_to_rate_limit_with_utc_reset`). They also fail on main, and this change
does not touch that code.

## Findings

### 1. HIGH: bounded 64 KiB locator breaks native Claude/Codex receipt recovery and hides usage for real transcripts

`src/AgentTeamForge.Business/Features/Agents/Terminals/InteractiveTranscriptReader.cs:38` and `:176-184`
(`HeaderLines`)

`LocateSession` always passes `bounded: true`. `ReadClaudeSession` and `ReadCodexThread` now go through
`LocateSession`. Their callers are `DispatchJob.ReconcileNativeClaude` / `TrySettleNativeCodex`
(`DispatchJob.cs:903,915`), and before this change they used the full line scan. `HeaderLines` keeps only
the lines that end inside the first 64 KiB. A first record larger than that yields `[]`. Then `HeaderId` or
`IsParent` returns `null`, `LocateSession` returns `null`, and the transcript is "not found".

The backend report says "Ordinary interactive Read behavior retains its existing full ancestry scan". That
holds for `Read()`, but not for the two static readers that the refactor rerouted.

Verified on a real transcript on this machine:
`~/.claude/projects/C--Projekt-git-Effektiv/ca54f1e1-….jsonl`.
- Lines 1-3 are `bridge-session` / `queue-operation` records with no `isSidechain`.
- Line 4, the first chain record (`isSidechain:false`), is the initial user prompt and is **270,553
  bytes**.
- The first `isSidechain` record in other recent sessions sits at 53 KB, already close to the limit.

For Codex, the `session_meta` line (base plus user instructions) is about 23 KB here. A large AGENTS.md
chain pushes it past 64 KiB.

Failure scenarios:
- A native Claude follow-up is delivered into a session whose first prompt was over 64 KiB, for example a
  long pasted plan or review brief. `ReconcileNativeClaude` never finds the file, so the job never gets
  its receipt or settlement and stays unresolved.
- The same file shows `session_tokens: null` ("—") for that session forever. The 30 s negative cache
  retries and fails every time.

Suggested fix:
- Bound by **line count, not a byte window**. `LiveFiles.ReadLines` already streams, so
  `LiveFiles.ReadLines(path).Take(N)` (say 50 for Claude, 10 for Codex/Pi) reads one large first record
  fine and still avoids a full-file scan when no chain record exists.
- Alternatively, keep `ReadClaudeSession`/`ReadCodexThread` on the previous unbounded path and bound only
  the usage caller.
- Add a test with a first chain record larger than 64 KiB.

### 2. HIGH: Codex usage is never found for headless `codex exec` jobs or Codex Desktop/VS Code leads

`InteractiveTranscriptReader.cs:112` (the Codex `IsParent` branch), used by `LocateSession:38`

`LocateSession` requires `IsParent(...) == true`. For Codex, that is true only for
`session_meta.payload.source == "cli"`. Any other string source returns `null`. Sampled rollouts on this
machine include `"source":"exec"`, written by `codex exec` / `codex exec resume`, which is
`CodexExecBackend`, and `"source":"vscode"` (Codex Desktop/extension). The plan (§1) explicitly says the
`codex exec` rollouts should be counted.

Failure scenarios:
- In headless launch mode, every Codex job card shows `—`.
- A Codex Desktop lead bound through `CODEX_THREAD_ID` or its wake target always shows `lead —`.
- The group and top totals silently omit all of these sessions.

The only test uses `"source":"cli"`, so this is not covered.

Suggested fix:
- For the locator, accept a Codex rollout unless it is positively a subagent. That means
  `source.subagent` present → false, any string source → true.
- If the native-receipt path must keep the stricter `cli`-only rule, give `LocateSession` a parameter
  for it.
- Add an `exec` test.

### 3. MEDIUM: the top "TOKENS · SHOWN" total double-counts a managed child that acts as a nested lead (plausible)

`JobsEndpoint.cs:460-472` (`LeadUsage`) with `app.js:1128-1129`

When a managed child (for example a Claude job with native session `X`) submits its own sub-jobs, its
bridge starts a lead session with a `managed-child:` binding. Claude sets `CLAUDE_CODE_SESSION_ID=X` for
its own MCP bridge; `LaunchEnvironment` strips only the *inherited* parent id. For Codex, the child's
`CODEX_THREAD_ID` or `register_codex_wake` works the same way. The child's lead binding therefore points
at `X`, which is also that child job's `session_id`.

The UI total is `usageSum(jobs)`, which includes `X` via the job, plus every value in `lead_tokens`, which
includes `X` again through the nested lead. As a result, session `X` is counted twice in the header
total. The per-group Σ values are each right on their own.

Suggested fix:
- Return the native session id with each lead entry, for example
  `lead_tokens: {id: {usage, native_session_id}}`, or a parallel `lead_native_ids` map. The UI can then
  skip lead usage whose native id is already among the page's job `session_id`s.
- Alternatively, return `null` lead usage for leads where `IsManagedChild` is true.

### 4. LOW: a deleted or rotated transcript keeps showing its last total forever

`SessionTokenUsage.cs:44-56,88`

Once `entry.Path` is set, it is never cleared. If the file is later deleted, `new FileStream` throws
`FileNotFoundException`, and the generic catch returns the stale `entry.Usage` on every poll until the
daemon restarts. The plan's smoke list expects `—` for a deleted transcript.

Suggested fix: on `FileNotFoundException`/`DirectoryNotFoundException`, reset `entry.Path = null`,
`Offset = 0`, `Usage = null` and `Seen.Clear()`. The negative cache then handles relocation.

### 5. LOW: Claude env takes precedence over Codex env in bridge capture

`JobsMcpBridge.cs:157-159`

Take a Codex lead started from a shell whose parent is a Claude Code session, for example `codex` run
from Claude's terminal or a script launched by Claude. It inherits `CLAUDE_CODE_SESSION_ID`, and the lead
is then bound to the *Claude* session. It shows that session's tokens as its own, and
`NativeBindings` never falls back to its Codex wake target.

The daemon and managed launches scrub this variable, so only manually started leads are affected.

Suggested fix: when both variables are set, prefer the one that matches the actual host. The bridge
already resolves the host via `HostSessionWake.Resolve`/`CurrentHost()`.

### 6. INFO: migration renumbering and dev databases

The resolution in `1360687` is correct:
- V29 is byte-identical to main (`display_name`).
- V30 adds the three `native_*` columns.
- `CurrentVersion = 30`, and the migrations array is in order.
- The SchemaTests downgrades drop V30, then V29, in reverse order. The version assertion now uses
  `Schema.CurrentVersion`.

One caveat applies only to developer state: a real state directory that ever ran a daemon built from
`11b17bd` itself has version 29 = `native_*`, with no `display_name`. After the merge, V30 fails with
"duplicate column" and the daemon refuses to start. The backend report suggests only a temp-dir test
and a `--version` smoke were run, so this is probably moot. It is worth a line in the release notes, or a
manual fix: `ALTER TABLE lead_sessions ADD COLUMN display_name TEXT; UPDATE schema_migrations SET version=30 …`.

## Checked, no issue found

- **Incremental parsing.**
  - The parser reads only up to the last `\n`, and a partial tail waits for the next poll.
  - When the file shrinks, the offset, totals and Claude ids are reset.
  - Oversized (> 8 MiB) records are skipped across chunks with `SkippingLine`. The skipped tail is
    consumed at the next newline and never parsed as a record.
  - Short reads are handled, and the offset advances only past complete lines.
  - A first read of a large file catches up at 8 MiB per poll and converges. For Codex the value can lag
    for a few polls, but it is not wrong.
- **Claude dedupe by `message.id`.**
  - Checked on three live transcripts: 358 ids, and every id had identical usage on all of its split
    lines. Keeping the first line is therefore exact.
  - Lines without an id are skipped.
- **Codex.**
  - The last cumulative `total_token_usage` is used, and cached tokens are subtracted from input.
  - `info: null` token_count events throw `InvalidOperationException`, which is caught, so the previous
    value is kept.
- **Thread safety.** All cache state is touched only under a single `Lock`. LRU eviction and the
  negative cache are inside it. IO runs under the lock, but only web list calls use it, so this costs
  serialized latency and is not a correctness problem.
- **Poll cost.**
  - Found paths are cached.
  - Missing sessions do one recursive scan each per 30 s. That is fine for Claude/Codex, which use
    name-filtered patterns (about 450/750 files here).
  - The Pi pattern `*.jsonl` reads a 64 KiB header of every Pi session per miss; this is bounded by the
    30 s cache and is best-effort.
- **Path traversal.**
  - Session ids containing `/ \ * ?` are rejected.
  - The file must match `<root>/**/<pattern>.jsonl`, and its header id must equal the requested id.
  - Homes come from the daemon env or from an authenticated same-user bridge.
- **MCP `list_jobs` unchanged.**
  - `IncludeUsage` is set only by `WebConsoleServer` `/api/jobs`.
  - `SessionTokens` is `WhenWritingNull`, and `LeadTokens` is null on other calls. Both are omitted by
    the source-gen `WhenWritingNull` default.
  - Only the web writer adds `session_tokens: null`.
- **Lead binding on resume and start.**
  - Both overwrite the binding, which the plan asks for (`/clear` or resume changes the id).
  - A bridge with no native env writes NULL. For Codex, the wake-target fallback then applies, because
    resume re-registers the wake.
- **`NativeBindings`.** Stored columns come first, then a `kind='codex'` wake target. Closed leads are
  excluded, and the query takes at most 50 parameters.
- **Merge in `app.js`.**
  - `leadNames` (from main) and the Σ span (from this branch) coexist in the team header.
  - The lead-card `session-id` shows the name with the full id as its title.
  - The team toggle/content class names are unchanged.

## Verdict

**CHANGES_REQUESTED.** There are 2 high, 1 medium, 2 low and 1 info findings. Finding 1 is a regression
in existing native-receipt recovery. Finding 2 makes the feature show nothing for headless Codex jobs and
Desktop leads. Both are small fixes in `InteractiveTranscriptReader.LocateSession`/`IsParent`, and each
needs one focused test.

## Re-review (2286fba + integration merge 51a32c4)

Checked `git show 2286fba` and the merged tree at `51a32c4`, read-only. Tests run in Release:
`Features.Usage|SchemaTests|Features.Sessions|InteractiveTranscriptReader|NativeClaude|NativeCodex|JobsMcpBridge`.
108 total, 104 passed, 4 failed. The 4 failures are the same known Windows timezone/rate-limit cases as
before.

1. **Fixed.** `HeaderLines` (the 64 KiB byte window) is gone.
   - `HeaderId` streams `LiveFiles.ReadLines(path).Take(10)` again, which is identical to the code before
     the refactor.
   - `ReadClaudeSession`/`ReadCodexThread` pass `usage: false`, so `IsParent` scans the full stream as
     before. The instance `Read()` path uses the default `usage: false`, so it is unchanged too.
   - Usage mode limits the scan by record count (50 Claude, 10 Codex/Pi) and never cuts a record.
   - New tests cover a 270,553-character first Claude record (usage and completed receipt) and a
     100 KB Codex `session_meta`.
2. **Fixed.** In usage mode, any string `source` counts as a parent. That covers `cli`, `exec` and
   `vscode`. An object `source` makes `Str` return null, so it reaches the `subagent` check and is still
   rejected, as a test confirms. Receipt mode stays `cli`-only, and the theory test asserts that
   `exec`/`vscode` return no receipt.
3. **Fixed.** `NativeBindings` excludes `binding_key LIKE 'managed-child:%'`. That prefix is the only
   binding form used for managed children (`ManagedChildContext.cs:38`, `JobStore.cs:440`,
   `LeadSessionStore.cs:120`). `LeadUsage` already defaults every page lead to null, so a nested lead
   gets `null`, and its tokens are counted once, through its job's `session_tokens`. The API shape is
   unchanged, and the endpoint test asserts the null.
4. **Fixed.** `FileNotFoundException`/`DirectoryNotFoundException` resets path, offset, usage, seen ids,
   skip state and `RetryAfter`, then returns null. The next read relocates the transcript, and if it
   still fails the 30 s negative cache applies. Other IO errors still return the last value, which is
   the right choice for transient locks. Tests cover both deletion and relocation.
5. **Fixed.** `NativeKind(CurrentHost()?.Kind, …)` selects the id that matches the host, and a recognised
   host with no matching id gives no binding. With an unknown host or `pi`, it keeps the old Claude-first
   fallback. `CurrentHost` is implemented for Linux, Windows and macOS, and the home follows the chosen
   kind.

Merge `51a32c4` keeps the integrated V30-then-V29 downgrade order in all three SchemaTests fixtures, with
no leftover V29-only native block. It matches production `Schema.cs`: V29 is `display_name`, V30 is
`native_*`, and `CurrentVersion = 30`.

No new real bugs found.

**Verdict: APPROVE**
