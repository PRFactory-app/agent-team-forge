# Web console MVP — Linux runnable checkpoint

Status: **narrow runnable checkpoint**, Linux only, fake backend tested. Written
by Claude Opus; opposite-family (Codex) code review is still required before merge.

## Scope change (owner direction, 2026-09-26)

The owner superseded the elaborate console planning (S0 contracts, branch
`8624078`, not imported) with a KISS text-only console on top of the MVP jobs
API (`184c0d9`). This is an **explicit single local operator/profile-bound MVP**:
not multi-team delegation, not receipt/reload recovery, not complete F27. No
separate plan re-review was required by the owner for this checkpoint.

## What exists

- `atf web --state-dir DIR --port PORT` — a separate client process in Host
  (`Features/WebConsole`). Binds numeric `127.0.0.1` only (`0` = ephemeral).
  It never opens the job database and never starts a daemon; every operation is
  forwarded through the existing `IpcClient` (`job_list`, `job_get`,
  `job_follow_up`). No schema, Business or IPC changes.
- Static `index.html`/`app.js`/`app.css` embedded in the binary; no framework,
  no build pipeline. Job list, details (status, reason, backend, session, parent,
  cwd, attempts, result) and a follow-up textbox.
- **Stop is visibly unavailable**: the daemon has no stop/cancel operation.
  Nothing maps or signals process PIDs.

## Security fundamentals kept

- Kestrel via `WebApplication.CreateEmptyBuilder` + `UseKestrelCore` (no config
  files, no env-var URLs, no logging providers → request headers are never
  logged). Single `RequestDelegate`; no minimal-API reflection; AOT clean.
- Exact `Host: 127.0.0.1:PORT` on every request (421 otherwise).
- Per-run 256-bit random bearer, printed only on the launcher's stdout. The page
  asks for it once and keeps it in a JS closure: never URL, query, cookie or
  Web Storage. The daemon credential is never sent to the browser.
- All `/api/` calls need the bearer (constant-time compare); every POST needs
  the exact `Origin`; any present non-matching Origin is refused. No CORS, no
  cookies. Rejections happen before any IPC call.
- CSP `default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; …`,
  `no-store`, `no-referrer`, `nosniff`, `X-Frame-Options: DENY`. All server text
  is rendered with `textContent`.
- Bounds: 16 KiB request body, 8 KiB instruction, 128-char key, header/line
  limits, 32 connections, 4 concurrent IPC calls (excess → `web_busy`, not
  queued), 15 s IPC budget, 20 s browser fetch deadline.

## Outcome honesty

The page generates the idempotency key once per follow-up attempt. After a lost
response or `outcome_unknown` the attempt stays pending; the operator chooses
**Retry with same key** or **Discard**. No automatic retry, no new key after a
lost response. IPC calls are not tied to the browser connection; closing the
tab or killing `atf web` never cancels accepted jobs.

## Evidence (2026-09-26, Linux x64, SDK 11.0.100-rc.1.26425.128)

- `scripts/verify.sh`: restore, format, build `-warnaserror`, 101/101 tests,
  Native AOT publish with warnings-as-errors (no IL trim/AOT warnings), and
  published-binary scenarios 25/25 (native), including the two web scenarios.
- Unit tests (in-process Kestrel, fake IPC): wrong Host, missing/wrong bearer,
  missing/wrong Origin, invalid and oversized bodies all return errors with
  zero IPC calls; list/get/follow-up forward exactly one matching call without a
  credential; `outcome_unknown` forwarded once, no retry; concurrency bound.
  A mutation check disabling the Host and Origin checks failed 8 tests.
- Scenario (real `atf daemon` + `atf web` processes, fake backend): list → HTTP
  follow-up → child completes in the parent's native session; no HTTP body
  contains the daemon credential; killing `atf web` leaves the job completed.
- Manual native smoke: page headers as above; `api/jobs` lists the fake job;
  POST without Origin → 403. Native binary 14.6 MB; RSS ≈ 27 MB for `atf web`,
  ≈ 25 MB for the daemon (single observation, not a benchmark).

## Run it

```bash
scripts/demo-web.sh 18080                      # fake backend, new private state dir
# or against an existing daemon:
atf web --state-dir DIR --port 18080           # prints: url …, token …
```

## Missing / risks

- No whole-agent stop until a daemon stop op exists; then add a single-confirm button.
- Token is lost on reload by design; reload means re-entering it.
- Depends on the unreviewed MVP jobs API snapshot (`184c0d9`, `37b0e02`);
  changes there (field names, op names, error codes) break this console.
- JS was syntax-checked (`node --check`) but not exercised in a real browser in this checkpoint; no browser automation test.
- Windows/macOS unrun; the console inherits the Linux-only IPC transport.
