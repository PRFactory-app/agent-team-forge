# MVP jobs review and integration

**Verdict: approved with fixes.** Reviewed Claude-authored `37b0e02` against `d7d24ae`. Schema v2 preserves v1 jobs and adds backend, cwd, parent, and session fields. Dispatch fences session evidence to the active run and passes the parent's native session and cwd to a follow-up. The MCP and CLI paths use daemon IPC, so accepted jobs survive client exit.

One real bug was fixed: a parent in `needs_reconciliation` could retain a session ID and admit a follow-up while its earlier agent might still be active. Follow-ups now require a completed parent; a regression test covers the uncertain case. During integration with main's P1 listing and acceptance outcome work, the merged endpoint retained paged listing and the post-commit `outcome_unknown` response. The accepted in-memory job view now includes the new fields, matching the committed row.

Merged main (including approved Codex backend), approved Claude backend, and approved Pi backend. Registered `fake`, `claude`, `codex`, and `pi` in `BackendCatalog` for the real-agent profile. Kept the P1 `job_*` MCP tool names as aliases while adding `submit_job`, `get_job`, `follow_up`, and `list_jobs`.

## Linux gates

- `scripts/verify.sh` with the repository .NET 11 SDK: format and Release build passed, 139 tests passed (3 opt-in real backend tests skipped), AOT publish passed, 26/26 published scenarios passed.
- `scripts/demo.sh` against the published AOT binary: passed the bridge exit, durable completion, idempotent retry, and daemon restart scenario.
- `git diff --check`: passed.

## Native demos

Each `scripts/demo-real.sh` backend was run once with the tiny `PELICAN` prompt against the published AOT binary. All three returned `OK`, then `PELICAN` from a follow-up with the same native session ID.

| Backend | Result | Session ID | Evidence |
| --- | --- | --- | --- |
| Claude | pass | `73632ebb-45ee-4c5b-b045-8410ccdc5fa9` | `.run/demo-real-claude-20260926T152120Z-JHgSJ1/results.jsonl` |
| Codex | pass | `01a0de4e-c0ad-73c0-ae4d-e4f1310ea0d6` | `.run/demo-real-codex-20260926T152138Z-yOWuYN/results.jsonl` |
| Pi | pass | `d809cc79-82e1-4b51-b7c4-5500dd2998e3` | `.run/demo-real-pi-20260926T152153Z-eKWi6U/results.jsonl` |
