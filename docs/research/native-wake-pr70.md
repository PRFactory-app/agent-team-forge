# Native wake: public PR #70 research

## Decision and evidence boundary

**Native wake is AgentTeamForge's standard mechanism, enabled by default for supported hosts—not an optional integration.** Manual read/catch-up is explicit degraded recovery, never a silent substitute for working native wake. The upstream opt-in flag and mandatory watcher guidance are upstream compatibility policy, not our product policy. A native notice triggers reading durable messages; the notice itself is neither message delivery nor completed work.

Research used unauthenticated public GitHub API/raw requests. A subsequent [PR API](https://api.github.com/repos/mikaelliljedahl/agentic-coder-teams-mcp/pulls/70) check confirms [PR #70](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/70) **merged to main on 2026-09-26 at 12:40:30 UTC**, merge commit `471a17514d041e09b69cb24b910e418da28d2027`. Source references below pin the inspected head `6da4e041f95e62e4c5f3a9ddf8f043c0fe7d769e`. No upstream tests or live hosts were rerun for this research.

The updated PR checklist and appended implementation evidence report **Windows W1–W3 passed**, establishing reference-concept evidence on Linux and Windows. The live Windows path used native `codex.exe`; the `.cmd` route remains unit-test-only. W3 verifies Claude's safe `unsupported_platform` response, not working Windows-Claude wake. Earlier pending-smoke statements are superseded by appended results. This retires the reference Windows-Codex feasibility question, not AgentTeamForge's C# integration or all host/platform gates.

## Confirmed source observations

### Claude host-local wake

[Implementation][implementation], [plan §§2.1, 2.4, 9][plan], and [native_wake.py][wake]:

- The notifier belongs to the recipient Claude host's **own MCP server**. It sends body-free unread-count/sender notices telling the host to read its inbox. It is not a daemon-to-arbitrary-session prompt transport.
- Linux only: `claude_platform_supported` and `resolve_claude_channel` reject native Windows and macOS before channel discovery. The nearest recognized host must be Claude; a nearer Codex/Pi host rejects an inherited Claude channel. A numeric `<pid>.sock` basename must match that host. Non-numeric socket names can be accepted with `owner_verified=false`; this is not universal ownership proof.
- `post_claude_notice` writes newline-delimited auth and user-message JSON over AF_UNIX, with a bounded connect/write budget (default five seconds). It reads no acknowledgment. A successful write does not prove the host accepted or acted on the notice.
- Each reader has a lifetime OS lock; contenders retry after ten seconds. Default scan interval is one second, coalescing two seconds, outstanding-notice re-notification floor 300 seconds. Failure backoff doubles from two to 300 seconds; only successful posts advance notice progress. Consumption cursors are unchanged.
- Activation checks unread backlog immediately on target transition. Repeated activation preserves same-target state; target/session revalidation prevents posting to a dropped lead target. Restart requires explicit session binding; Claude-hosted members re-arm through an authenticated member operation.
- [BaseBackend spawn/resume][spawn] blanks both inherited Claude messaging variables when upstream native wake is enabled. This supplements, rather than replaces, nearest-host validation. [procinfo.py][procinfo] recognizes Claude, Codex and Pi ancestry; recognizing Pi is not Pi wake support.

### Codex member wake

[server_simple.py][server] (`external_set_wake`, external `send_message`) and [native_wake.py][wake] (`CodexMemberWake`, `verify_codex_thread`):

- Registration authenticates running membership through the member token. It validates canonical thread UUID and absolute home; blank home defaults to `~/.codex`. Set/change/clear increments generation; clear keeps a null-thread tombstone.
- The inbox append completes before wake. Unexpected wake errors return failed wake status without undoing the successful send. The upstream file/registry transaction is **not evidence of our SQLite durability contract**.
- Queue decisions serialize per member within one process; short registry snapshots and generation/status revalidation occur before external I/O. No agents lock is held across the subprocess. There is a residual replacement race after revalidation, and separate processes can emit duplicate notices.
- First verification per generation reads existing `state_5.sqlite` read-only with a 0.5-second SQLite busy timeout, allowing WAL visibility. It rejects known archived rows and falls back to rollout filenames when appropriate. Successful verification is cached, not continuous proof that the thread remains valid.
- The discovered CLI runs `codex queue --thread <id> --message <notice>` with reported `CODEX_HOME`, lead home as cwd, null stdin, captured UTF-8 output and default 15-second subprocess timeout. Environment removes Claude channel credentials, `AGENT_*`, and session-directory inheritance. Sender text is sanitized; message bodies/tokens are absent from the notice.
- `queued`, `coalesced`, `backoff`, `timeout`, `unverified_thread` and other statuses describe wake only. **No deferred Codex retry timer exists:** a later send re-evaluates retry eligibility. A final failed wake can therefore leave unread work waiting indefinitely without another trigger.
- This PR does not implement Codex-lead self-wake or native downstream transport for managed interactive children. Those are follow-ups; Pi is unchanged. Do not confuse member notification with launch, follow-up, result, interrupt or stop control.

## Reported tests, not independently reproduced

Evidence comes from the appended live sections and validation tables in [implementation.md][implementation].

| Scope | Upstream report | Remaining boundary |
| --- | --- | --- |
| Linux automated | Format/lint/type checks green; 1832 passed, 4 skipped | Queue runners are fakes; socket unit tests use test listeners, not real Claude |
| Linux live | Claude Code 2.1.282 + Codex 0.156.1 TUI in Herdr: three automatic round trips; child-owned socket; restart/backlog catch-up; bypass-mode notices without approval | S3 inherited-channel rejection, S6 refusal, S7 flag-off not run live; Linux Codex Desktop evidence is pre-implementation |
| Windows automated | Windows 11 Pro 26200, Python 3.12.14: 1829 passed, 7 skipped; format/lint passed | Two remaining type diagnostics reported also present on main; not an all-green Windows type gate |
| Windows live Codex | Codex CLI 0.157.1 / Desktop 26.924.22138: native `codex.exe` queued notice, idle thread replied after 17 seconds without watcher/nudge | Scripted FastMCP lead; no live npm `.cmd` installation/test; not managed .NET agent-control evidence |
| Windows Claude | `unsupported_platform`, no channel pipe access, inherited variables scrubbed | Safe refusal, **not successful native wake**; cancellable named-pipe transport deferred |
| macOS / Pi | Claude macOS reader deferred; no Pi native-wake implementation established by this feature | Separate native-platform and Pi evidence required |

Closed/unloaded Codex Desktop persistence and busy-turn dispatch (V1/V2) remain unverified. The plan's broader POSIX language and inheritance expectations must not override current Linux-only code or the live observation that a spawned child's own MCP configuration needed explicit upstream enablement.

## Application to C#/.NET 11 vertical slices

Use this as behavioral research, **not reusable Python implementation**. Follow [our architecture](../architecture.md): exactly **Host → Business → DAL**, no extra production layer or Business-owned repository ports. Proposed work below requires independent plan review before implementation.

1. **Register and diagnose wake:** Host establishes authenticated IPC identity and presents capability/degraded status. Business validates recipient/session binding, backend version and registration generation. DAL persists registration/tombstone and binding state. Registration must not trust an arbitrary thread/home merely because the caller knows a membership token; validate against authorized local session scope.
2. **Commit message, then signal:** Business invokes one DAL transaction for message plus wake intent; return acceptance only after commit. Dispatch outside the transaction. Store bounded retry/degradation state separately from read/ack cursors. Rebuild pending wake from durable unread state on restart/reconnect. Do not copy upstream Codex's next-send-only retry limitation into a standard wake mechanism.
3. **Host-local Claude relay:** daemon retains durable ownership; a restricted Host bridge under the recipient host performs local notification through a Business backend adapter. Bridge uses authenticated IPC and never opens the job DB. Prove the own-child trust path; detached daemon ancestry cannot be assumed equivalent. Bridge death must not lose accepted messages; reconnect rebinds and catches up without replaying uncertain prompts.
4. **Read/ack and recovery:** native wake normally prompts authenticated reading. Distinguish message committed, notice attempted/queued, message read/acknowledged and job completed. Deduplicate notices/processing by durable identity, not notice sequence. Expose unsupported, disconnected, failed and explicitly degraded states; require visible recovery choice rather than installing polling silently.

Keep permissions, budgets and human/foreign-activity rules intact. Never bypass a receiver's approval policy or change interactive launch to headless because wake is unavailable. Target .NET 11; verify actual SDK/package and published JIT/AOT behavior rather than inheriting Python or .NET 10 evidence.

## Risks and acceptance suggestions

- **Host trust and credentials:** prove nearest-host/session ownership, PID reuse/reparenting handling, inherited-environment scrub and secret redaction. Fail closed on ambiguous ownership. Socket exports to MCP children are upstream-observed behavior, not established here as a stable public API guarantee. Review stderr/status redaction as well as notice text.
- **Bounded external I/O:** test unavailable/stalled sockets, cancelled writes, hung CLI and process-tree cleanup. A SQLite busy timeout does not bound filesystem traversal; captured subprocess output also needs an explicit size bound in our implementation. Member-selected `CODEX_HOME` selects configuration and is a trust boundary. Internal DB/schema/rollout conventions need version checks and safe failure, not blind compatibility assumptions.
- **Durable standard wake:** TDD commit-before-wake failure, crash between commit and notification, duplicate bridges, generation change/clear, revoked membership, restart catch-up, burst coalescing and backoff. Inject failure on the final message with no later send: retry or explicit degraded status must occur. No failure may undo committed acceptance or advance read cursors.
- **Native Windows remains a release gate:** run our published .NET 11 binary on Windows with actual selected visible terminal tabs and real host processes. Codex native executable and any supported `.cmd` route need separate quoting, environment, timeout and live wake evidence. Claude needs authenticated, cancellable named-pipe I/O, stalled-pipe cancellation and actual own-host wake. Reporting `unsupported_platform` leaves the required gate blocked; Linux tests/cross-builds cannot close it.
- **Pi remains first-class managed P03 scope:** independently prove native Windows launch, same-session follow-up, status/results, approvals where exposed, interrupt/stop and reconnect in selected interactive mode and explicitly chosen headless mode. Lead-host Pi wake is a separate capability; this PR supplies neither a Pi control API nor proof of managed Pi support. No invented Pi transport or externally attached session can satisfy that gate.
- **Live matrix:** record OS, SDK, CLI, terminal, host version, launch context and sanitized transcripts. Test idle/busy, closed/unloaded, hold/refuse/rate limits, wrong recipient and bridge/daemon restart. Repeat automatic round trips without watcher/human nudge. Explicit manual recovery is useful evidence of degradation, never a native-wake pass. macOS likewise needs native reader/terminal tests before a support claim.

## Pinned public sources

Links below use the inspected PR head; mutable PR/API metadata is identified separately above. No external checkout or private repository is needed to understand these recommendations.

[implementation]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/6da4e041f95e62e4c5f3a9ddf8f043c0fe7d769e/docs/features/native-session-wake/implementation.md
[plan]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/6da4e041f95e62e4c5f3a9ddf8f043c0fe7d769e/docs/features/native-session-wake/plan.md
[wake]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/6da4e041f95e62e4c5f3a9ddf8f043c0fe7d769e/src/claude_teams/native_wake.py
[server]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/6da4e041f95e62e4c5f3a9ddf8f043c0fe7d769e/src/claude_teams/server_simple.py
[spawn]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/6da4e041f95e62e4c5f3a9ddf8f043c0fe7d769e/src/claude_teams/backends/process_base.py
[procinfo]: https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/blob/6da4e041f95e62e4c5f3a9ddf8f043c0fe7d769e/src/claude_teams/procinfo.py
