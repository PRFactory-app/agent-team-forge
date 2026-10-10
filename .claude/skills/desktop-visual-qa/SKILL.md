---
name: desktop-visual-qa
description: Run a visual QA / end-to-end session in which a browser-capable desktop agent (Codex Desktop or Claude Desktop) tests a change on your local stack while the ATF lead owns the stack, the brief, triage and the dialogue with the product owner. The lead collects the test material, starts the stack from the worktrees under test, writes the brief, and only then — code final, stack up — mints an ATF join ticket and a paste-ready prompt, sends GO when the tester joins, triages each finding and routes fixes to ATF jobs. The tester never edits code; the lead never merges.
argument-hint: <TARGET: issue | epic | bundle label> [--pr <owner/repo#N> ...] [--bus <dir>] [--lang <code>]
---

# Desktop visual QA (lead side)

You are the **lead**. `$ARGUMENTS` names what is under test: an issue
(`#123`, `ABC-123`), an epic, or a bundle label, optionally with its PRs
(`--pr acme/web#655 --pr acme/api#34`). `--lang` sets the language you use with
the product owner (PO); the brief, the prompt and every message to the tester
are in **English**.

The **tester** is a desktop agent with a real browser (Codex Desktop, Claude
Desktop). It joins your ATF lead session as an **external member** (see the
[`external-member-invite`](../external-member-invite/SKILL.md) skill) and only
reads and clicks. **You** own the stack, the brief, data prep, triage and the
PO dialogue. The tester never edits a repository, and you never ask it to.

Why ATF: the tester's messages are durable and wake you natively, so you do
not run watcher loops; fixes go to durable ATF jobs that survive your own
restarts; and `resume_session` brings the whole session back after a crash or
compaction.

## Paths and local values (resolve once)

Write these into `<BUS>/LOCAL-ADAPTATIONS.md` (create it if missing, reuse it
if present). Every later step uses them.

| Value | Default |
|---|---|
| `<BUS>` (shared folder for artefacts; `--bus` wins) | `~/qa-bus/` on Linux/macOS, `%USERPROFILE%\qa-bus\` on Windows |
| `<REPOS>` | the parent folder of the current repo |
| Free RAM (want ≥ 4 GB) | `free -g` / `vm_stat` / `Get-CimInstance Win32_OperatingSystem` |
| Who holds a port | `ss -ltnp \| grep :<p>` / `lsof -iTCP:<p> -sTCP:LISTEN` / `Get-NetTCPConnection -LocalPort <p>` |
| Stop a process you started | `kill <pid>` / `taskkill /PID <pid> /T /F` |

**Developer-specific values come from the developer's own config, never from
this file**: database names, credentials, personal queues, buckets or API
endpoints live in their gitignored local config (`.env`,
`appsettings.Development.json`, `config.local.*`). Anything missing there is
asked of the user **once** and recorded in `LOCAL-ADAPTATIONS.md`. Never guess
a personal resource name, and never write a person's name, credential or
absolute home path into the brief.

Artefacts, with `<T>` = the target label:

- `<T>.desktop-e2e-prompt.md` — the paste-ready prompt.
- `<T>-E2E-BRIEF.md` — overview, environment, deltas, order, DoD, report format.
- `<T>-E2E-CASES.md` — the per-issue detail.
- `<T>-E2E-FINDINGS.md` — the tester writes here; you read it.
- `<t>-e2e/` — screenshots and your prep evidence. **Never in a repo**: check
  every diff for images before you accept it.
- `RESUME-STATE.md` — your ledger: ATF session id, job ids, SHAs, rulings.

## Phase 0 — Pre-flight (each is a hard stop)

1. **Is the code final?** Run this only on finished work: every builder job is
   done, every review round closed with zero blockers, the base branch merged
   in. A builder still running (`list_jobs`) → wait; QA would test a moving
   target. For every PR: `gh pr view N --json headRefName,headRefOid,isDraft,mergeable,body`.
   Find the worktree holding that branch (`git worktree list`), check
   `HEAD == headRefOid` and a clean tree, and record the short SHAs for GO.
   From the PR body collect what is included, what is deliberately
   **excluded**, and the verification already done (so it is not repeated).
2. `gh pr checks` per PR: note the state; pending CI does not block QA.
3. Free RAM and any cloud/SSO login the stack needs. Expired login → ask the
   user to log in. Never log in for them.
4. Port check for every port the stack needs. A port held by a process you
   did not start → **do not kill it**; name it to the PO and ask.
5. ATF: `session_info`; after a restart, `resume_session` on **your own**
   prior lead session (its id is in `RESUME-STATE.md`). Never adopt another
   lead's session. `wake_status` should show native wake; without it, poll
   `read_messages` yourself during Phase 5.

## Phase 1 — Test material (the manual test cases are the test plan)

1. **Manual test cases first.** Collect the written manual cases for every
   issue under test — a QA sub-issue, a test-plan section, a checklist in the
   PR — into `<BUS>/<t>-qa/` with the project's tracker CLI (`gh issue view`,
   or whatever the project uses). The tester runs them **by id** and reports
   per id. An issue without manual cases is tested through the flows of the
   others; say so in the brief.
2. **Deltas.** Where the build under test changes a case's expectation (several
   issues combined, or a PO decision that changed the design), write the delta
   explicitly. Deltas outrank the written cases and must cite their source
   (`file:line`, decision record, PR comment).
3. **Per-issue detail** in a background ATF job (`submit_job`, backend
   `claude`, read-only) that writes `<T>-E2E-CASES.md` and reports ≤ 15 lines
   of gaps. Give it the issue links and doc paths (spec, plan, decisions,
   acceptance-criteria tables). For each issue it writes: what changed and
   why; the acceptance criteria, short and verbatim; binding PO decisions; UI
   routes and menu paths; the manual cases with deltas applied; cases that
   cannot be tested locally, with a reason; known limitations. It names test
   files only — never their content. Relay its gaps to the PO with GO; that is
   where wrong expectations surface.
4. **Mockups / design reference** for UI work: tell the tester which file and
   region is the reference and which viewports to use (e.g. 1280×800 and
   1536×864, light and dark; a brief look at 375 px).

## Phase 2 — Stack and data prep (you, never the tester)

Discover how the project runs locally (README, `docker-compose.yml`,
`Procfile`, `.claude/launch.json`, `package.json` scripts) and record the
recipe — service, port, directory, env — in `LOCAL-ADAPTATIONS.md`. Then:

- Build first when dependencies or code changed since the last run.
- Start each service from the **worktree under test**, as its own background
  shell with a log in your scratchpad. Copy the gitignored local config from
  the main checkout into any fresh worktree.
- Use **runtime env overrides** for local differences. Never edit checked-in
  config files to make the stack run.
- Wait for each service to listen (an until-loop on the port or a log line),
  and read the head of each log for unhandled errors.

**Data prep and DB writes are yours.** Migrations, seed scripts and one-off
repair steps named in a test case run **before GO**, dry run first, against an
**explicit localhost connection string** — never against whatever a shared
config file points at. Save output as evidence in `<t>-e2e/lead-prep-*.txt` and
mark those steps "done by the lead" in the brief. The tester runs read-only
queries at most.

**Local data sanity.** Before GO, list in the brief anything that will confuse
the tester: foreign test data from other branches, thin reference data, stale
settings. Never delete foreign data; it belongs to someone else's test.

## Phase 3 — Brief

`<T>-E2E-BRIEF.md`:

1. **What is under test:** the goal in 2–3 sentences; a PR table (repo, PR,
   branch, SHA, worktree); an issue table (link, what, repos, manual-case
   link). **Excluded scope**, explicitly. Verification already done.
2. **Deltas** that outrank the written cases, and prep steps already done,
   with evidence paths.
3. **Environment:** services and URLs, account/tenant to use, the data flow,
   what is disabled on purpose, logs the tester may read, test files by name,
   known observations.
4. **Test order:** the end-to-end flow first, then per-issue cases, regression
   spot checks, then viewports and themes.
5. **Definition of Done (PASS):**
   - every manual case (deltas applied), and every acceptance criterion not
     covered by one, is marked PASS, FAIL or NOT TESTABLE LOCALLY with a
     reason; nothing is skipped silently;
   - the main flow runs end to end with no unhandled error in the logs;
   - outputs are correct (numbers checked against queries, no raw codes or
     untranslated keys);
   - the UI matches the reference on the agreed viewports, light and dark, in
     every UI language;
   - no regressions in the spot checks.
6. **Report format:**
   ```
   ## Progress
   HH:MM case — OK | FINDING F<k> | BLOCKED
   ## Findings
   ### F<k> — BLOCKER|MAJOR|MINOR|COSMETIC — <issue> — <title>
   Where, Steps, Expected, Actual, Evidence (screenshot path, query + result, log line)
   ## Case matrix
   | Issue | Case id | AC | PASS/FAIL/NOT TESTABLE LOCALLY | Actual | Evidence |
   ## AC matrix
   (acceptance criteria not covered by any case)
   **E2E verdict**: PASS | FAIL
   ```

## Phase 4 — Ready gate, then join ticket, prompt and GO

The prompt is the **last** thing you produce. ATF join tickets are one-time
and **expire after ten minutes**, and the PO pastes the prompt the moment they
see it. Mint the ticket only when **all** of these hold, checked just before:

- the code under test is final (Phase 0.1) and every worktree `HEAD` equals
  the pushed tip you will name in GO;
- every service listens with no unhandled errors, and prep is done with
  evidence saved;
- `<T>-E2E-BRIEF.md` and `<T>-E2E-CASES.md` exist and name those SHAs.

If one fails, fix it or wait; do not mint the ticket yet.

1. The tester's host needs a restricted ATF MCP entry
   (`ATF_EXTERNAL_ONLY=1`, see [usage: external members](../../../docs/usage.md#external-members)).
   If the PO has not set one up, give them the one command first.
2. `create_join_ticket(name="<member-name>", note="<one line: E2E tester for <T>, wait for GO, test only>")`.
3. Write `<BUS>/<T>.desktop-e2e-prompt.md` from the template below, filling
   in the ticket's `session_id` and token, and **show it to the PO inline in a
   fenced block**. It contains the join token; that is intended.
4. Yield. The tester's `JOINED` line wakes you.

### Prompt template (fill in `<…>`)

```
You are the end-to-end tester for <T> (<PR list>), testing in your real browser. You join an AgentTeamForge session as an external member called `<member-name>`. The team lead (an orchestrating agent) has started the local stack and will send you the test brief.

## 1. Join the team (do this first)
Use only the `agentteamforge-external` MCP tools: join_team, external_read, external_send, external_set_wake, leave_team.
1. Call `join_team(session_id='<session-id>', token='<join-token>')`.
2. Keep the returned `member_token` in this conversation; every later call needs it, even after an MCP restart. If you lose it within ten minutes, repeat the same join_team call.
3. Register wake: Codex — read $CODEX_THREAD_ID and ${CODEX_HOME:-$HOME/.codex} in a shell, then `external_set_wake(member_token=..., codex_thread_id=..., codex_home=...)`. Claude — usually automatic; otherwise `external_set_wake(member_token=..., kind='claude')`.
4. Send `external_send(member_token=..., text='JOINED <member-name>')`.

## 2. Wait for GO
Do NOT start testing until a message starting with `GO <T>` arrives. It names the commits under test and the files to read. A wake is only a doorbell: read with `external_read(member_token=...)`; without wake, poll every 2–3 minutes.
During testing, call `external_read(member_token=...)` between test cases/M-cases and before any long-running step (e.g. a long E2E run), so lead messages are handled without waiting for the turn to end.
One `external_read` returns every unread message; queued wake rows that arrive afterwards can be ignored.

## 3. Rules (non-negotiable)
- Test only. Do not edit, commit, push, stash or check out anything in any repository. No migrations, no writes to the database; read-only queries against <local db> are allowed.
- The stack is already running; do not start or stop it: <service list with URLs>. If one is down, report `BLOCKED: <service> down` and wait. Never kill any process.
- Login: <how to sign in with the account already signed in on this machine>. Never enter a password or an MFA code; if one is asked for, report `BLOCKED: login needs the user`.
- Test data in <folder> may be uploaded through the UI only. Never copy it into a repository or paste its rows into reports (counts and names are fine).
- Screenshots go to <BUS>/<t>-e2e/, never into a repository.
- Changing data through the UI is allowed when a case requires it. Log every change (what, where) in the progress log and restore it at the end.

## 4. Report
Append findings as you go to <BUS>/<T>-E2E-FINDINGS.md (format in the brief). Send one line for every BLOCKER at once. At the end send:
`<T> E2E <PASS|FAIL> — <n> findings — report: <path>`
Stay joined: a second GO may follow after fixes. When the lead says DONE, call `leave_team(member_token=...)`.
```

When `JOINED` arrives (`read_messages(from_agent="<member-name>")`), send GO
at once with `send_message(to="<member-name>", text="GO <T> — <repo> <sha> (PR #n) …")`.
GO tells the tester to:

- check each worktree's `git rev-parse --short HEAD` and stop on a mismatch;
- read in order: BRIEF → CASES → `<t>-qa/`;
- use the service URLs and read-only queries for data-only cases;
- report to the findings file and send one line per BLOCKER;
- restore any settings it changed.

**Every later change to the brief, the deltas or the stack gets its own
message to the tester immediately.** It does not re-read files on its own.
Batch messages to a busy tester into one message instead of many small ones.

## Phase 5 — Watch and triage

On every wake: `read_messages(from_agent="<member-name>")`, then read only the
new part of the findings file. For each finding, before telling the PO:

1. **Verify** it in code or data (read-only; a few targeted greps or queries)
   and name the `file:line` or query result.
2. **Classify:**
   - **Caused by the change:** `git diff --stat origin/main...HEAD -- <paths>`
     touches the code path.
   - **Pre-existing:** path untouched and the behaviour already on main.
   - **Environment:** local config, reference data, foreign data.
   - **Definition question:** the code does what the plan says, but the case
     or spec reads otherwise.
3. **Route:**
   - Environment and pre-existing: your own `LEAD RULING` to the tester,
     relabelled (`PRE-EXISTING`, `ENV`); mention a follow-up candidate to the PO.
   - Definition questions and "fix now vs. follow-up": one question per
     finding to the PO in `--lang`, recommendation first, 2–3 options (fix in
     this PR / follow-up issue / current behaviour is correct).
   - Relay the PO's answer to the tester with the relabel (`FOLLOW-UP (PO)`,
     `NOT A BUG (PO: …)`) and what the case should now assert.
4. PO requests during the run (another service, an extra test): do it, tell
   the tester at once, and append extra tests as "run LAST".
5. Log every event and ruling in `RESUME-STATE.md`.

**Fixes go to a builder, never to you or the tester.** If the original builder
was an ATF job, `follow_up(job_id, instruction, idempotency_key)` continues it
in the same session and worktree with the finding, the evidence and a
failing-test-first instruction; otherwise `submit_job(backend="claude",
cwd=<worktree>, …)`. Follow the
[`agent-orchestration`](../agent-orchestration/SKILL.md) loop: reporting
protocol, yield for wake, verify the diff and test results, then an
opposite-family review job (e.g. `codex`) before you accept it. After the fix
is pushed and verified, rebuild, restart only the affected service, and send a
second `GO <T>` naming the new SHA and the finding ids to re-test.

## Phase 6 — Close

When the final line arrives:

1. Read the verdict, the case matrix and the data-change log in the findings
   file. Report to the PO: the verdict; findings by class; NOT TESTABLE cases;
   written cases that PO rulings made wrong (offer to correct them, with the
   PO's OK); data the tester changed; follow-up candidates.
2. Send `DONE` to the tester once the PO is satisfied — a second GO may still
   come before that.
3. Stop the stack **by port pid**, only the processes you started. Never kill
   by name pattern (`pkill -f`, `taskkill /IM`): it matches shell wrappers and
   other people's processes. Remove worktrees you created.
4. Leave the bus artefacts in place and record the outcome in `RESUME-STATE.md`.

## Never

- Let the tester edit, commit or push, or do so on its behalf.
- Write to a shared (non-local) database, or run a tool against a connection
  string you did not set explicitly to localhost.
- Kill or restart a process you did not start, or change the PO's own checkouts.
- Delete foreign test data, or copy test files with real data into a repo.
- Record a verdict or finding you did not read in the findings file.
- Mint the join ticket or show the prompt before the Phase 4 gate holds.
- Send GO before the brief and the cases file exist, or change the brief
  without messaging the tester.
