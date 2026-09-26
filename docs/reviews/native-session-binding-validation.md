# Native session binding validation

2026-09-26, branch `feat/native-session-binding`.

The retained `InteractiveLaunch` now holds the native session ID and transcript
path established by the first unambiguous correlated native user record plus
native parent evidence. Codex requires `session_meta.payload.source = "cli"`;
its `subagent` source is excluded. Claude requires `isSidechain = false` on a
native message. Pi discovery is scoped to the launch's session directory and
excludes headers with `parentSession`. Resume additionally requires the requested
native ID. There is no newest-file selection or 200-file candidate cutoff.

After binding, reads use that path and recheck its native ID, including after a
fresh-launch pane is retained for follow-up. Missing/changed bound identity,
unknown ancestry, and multiple eligible parents produce explicit protocol errors;
they do not acknowledge or complete a job. No Host or DAL changes.

Schema references:
- [Codex SessionSource](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/protocol.rs)
- [Pi SessionHeader](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/src/core/session-manager.ts)

Focused tests cover inherited-marker children for Claude/Codex/Pi, children
appearing after binding, follow-up with a real transcript reader in a retained
fresh-launch pane, 205 decoys, true ambiguity, explicit unknown ancestry, resume
identity filtering, replacement identity, and protocol-error propagation. Existing
turn-boundary/partial-line tests remain in place. These are synthetic native-schema
fixtures, not a claim of successful authenticated live model execution.

Verification command:

```sh
DOTNET=/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet \
DOTNET_PROCESSOR_COUNT=2 scripts/verify.sh
```

The initial unrestricted-worker run passed tests but failed during AOT publishing
with MSB4166 (worker exited prematurely). Reduced-worker verification passed;
the final run includes 577 passing tests, 6 opt-in skips, successful AOT publishing,
and 39/39 published-binary scenarios. Its binary is
`artifacts/linux-x64-20260926T212415Z-LD4Qgg/atf`.

## Live Linux attempt: blocked on authentication

Used the AOT binary under `artifacts/linux-x64-20260926T212109Z-eZJSfO/atf`,
with an empty, isolated HOME/config/state under `/tmp/atf-native-live-0DKosS`.
Setup used `--mode herdr --force` only in that isolated environment (`--force`
permits registering a binary under this worktree's artifacts directory).
Direct installed executables bypassed the owner's mise-mutating CLI wrappers.

- Claude 2.1.283: job `job_01a0df99440d7a1bb97199fdad1a3e5a`, owned session
  `atf-ff97b09184c1`; after selecting the initial theme, reached login-method selection.
- Codex 0.157.1: job `job_01a0df9a73877258a0eb9a2aa60ff8e7`, owned session
  `atf-9802b04d1b85`; reached ChatGPT/API-key sign-in selection.

No usable provider credentials were available in the environment. The owner's
`.claude`, `.codex`, `.pi`, existing Herdr sessions and ATF state were not accessed.
Both test jobs were explicitly stopped, their owned sessions closed, and isolated
daemon PID 560844 stopped through `atf stop --state-dir`.
Screens and job responses remain in that temporary evidence directory.

**The requested authenticated Claude + Codex retained-pane follow-up check remains
unverified.** It requires credentials supplied separately for the isolated HOME.
Full transcript parsing and the existing 32 MiB limit remain the separate P2 slice.

## Authenticated live Linux check (review)

Binary `artifacts/linux-x64-20260926T213640Z-7Fxn5V/atf` (after review fixes),
isolated HOME/XDG/CODEX_HOME and state under `/tmp/atf-live-nb-ZaGSj0`, herdr
mode, read-only copies of the owner's credentials (deleted afterwards).
Claude 2.1.283 and Codex 0.157.1, each in its own ATF-created Herdr session:

- Claude turn 1 spawned an async subagent whose prompt quoted the correlation
  marker: result `PARENT-ONE`. Follow-up in the same pane: `PARENT-TWO`, same
  native session `3e13a535-…`.
- Codex turn 1 spawned a `thread_spawn` sub-session whose rollout carries the
  marker: result `CODEX-ONE`, bound to the `source: cli` parent. The follow-up
  spawned a second child after binding: result `CODEX-TWO`, same session.

Review fixes found by this run and real transcripts: Claude ancestry is read from
the first `isSidechain` record at any depth (real transcripts can start the first
message at line 13); an async Agent launch (`status: async_launched`, `agentId`)
keeps the turn pending until its task notification (before, the interim "still
running" reply completed the job). A fresh isolated Claude profile also needs the
first-run upsell state from `.claude.json`, or startup is `agent_not_ready`.
