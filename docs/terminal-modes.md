# Setup choices and interactive terminals

## 1. Fixed user requirement

During setup, the user must choose how agents are launched:

- **Interactive on Linux:** in **Herdr**.
- **Interactive on Windows:** in a **visible terminal tab**.
- **Interactive on macOS:** an equivalent **visible terminal tab**.
- **Headless:** available only through an explicit setup choice.

The user specified Herdr for Linux. Windows Terminal is the proposed first
Windows provider. Terminal.app and iTerm2 are macOS candidates; the first
supported provider will be selected through an M0 spike, not an assumption that
their APIs are equivalent.

Interactive operation is a first-class mode and must not be deferred to a later
attached/Desktop feature. The earlier headless-only scope has been superseded.

## 2. What interactive means

A person must be able to see and use the **backend's real interactive agent
session** in a visible tab or pane: read output, type to the agent, and operate
its UI. Showing `tail -f` for a headless process in a terminal is insufficient.

The lead agent must also be able to submit work and receive status and results
through AgentTeamForge. The daemon must know which agent, conversation, and
terminal instance belong together. A visible terminal without machine control
does not meet the requirement.

Human input can change the agent's work. Status must therefore not assume that
all input came from the daemon. Concurrent human and machine control needs an
explicit contract; SQLite cannot be claimed to lock physical terminal input.

## 3. Setup and configuration

The installation/setup flow must:

1. Detect the OS and installed, supported terminal and backend versions.
2. Present valid launch modes and require an explicit choice.
3. For interactive mode, verify the terminal provider, session connection, and
   permissions.
4. Persist the launch mode and provider in versioned user configuration.
5. Show a summary and run a setup doctor in the daemon's intended production
   launch context. It must verify launch identity plus backend discovery,
   `PATH`, `HOME`/profile and config resolution, credential access, and
   authentication for the selected platform and mode.
6. Stop or request a new choice if the selected provider is missing or fails.

There must be no silent fallback to headless, tmux, or a terminal other than the
one selected. Noninteractive installation requires an equivalent explicit
parameter or configuration value.

Changing the default mode affects new agents. Existing agents' actual mode and
terminal binding must not be rewritten as though they had moved. A per-job
override is not needed in the PoC; separate test profiles can exercise both
modes.

The setup contract and persisted choice are part of the PoC. A full installer,
autostart, and uninstallation come later in M3.

## 4. Four independent adapter axes

```text
Backend:       Claude | Codex | future backend
Launch mode:   interactive | headless
Terminal:      Herdr | Windows Terminal | selected macOS provider
Control path:  backend/mode-specific, verified separately
```

`Managed` means launched and lifecycle-managed by AgentTeamForge, not
necessarily headless or a direct OS child of the daemon. A terminal server may
own the actual process tree. `Attached` means a session launched independently
earlier and registered later; this remains a separate future feature.

The headless candidates Codex app-server and Claude stream-json must not be
assumed to control an existing TUI process. Interactive mode needs its own
verified path:

- Persist an attempt generation and correlation before spawning, launching a
  terminal, or delivering a prompt; then bind stable backend/terminal IDs.
- Native message/wake path where it is actually supported.
- A candidate **child-side host bridge** where own-child rules require it. Its
  capability is scoped to one child/session and the minimum lifecycle,
  follow-up, result, and approval operations; it is not an administrative
  daemon client.
- Authoritative status/result signal; terminal text or silence is not proof of
  completion.
- Verified turn interruption, separately authorized agent stop, process
  ownership, and client-independent lifetime.

### Interactive evidence contract

Only a documented backend lifecycle signal or a verified durable backend
session/transcript record, correlated to the run generation and turn, can move
a job to `completed`. A model-invoked self-report tool is informational evidence
only. Terminal text, prompt scraping, process silence, and tab presence are not
authoritative completion or delivery evidence.

Human input or any activity that cannot be correlated to an AgentTeamForge run
is recorded as foreign/human-origin observed activity, not attributed to the
next queued job. The v1 policy is conservative human-wins: observed foreign
activity makes the session `foreign_busy` and pauses automatic follow-ups until
an authorized operator or lead explicitly reconciles verified idle. The daemon
cannot prevent physical terminal input and must not claim exclusive control.

Keystroke injection and raw terminal input simulation are **not supported v1
delivery transports** and cannot produce a delivery acknowledgment. M0 may
probe native alternatives. A backend × provider pair remains blocked unless it
has a supported native/control path, or a later explicit ADR and user scope
decision changes this rule with new tests. Scraped terminal text is never a
result contract.

Any helper that supplies lifecycle/results while the daemon is unavailable must
make events replayable. Prefer the backend's durable session record. If that is
insufficient, M0 may specify a bounded, private, append-only helper spool with
correlation and replay protection; files are allowed for this purpose. If no
replayable path exists, the pair is unsupported or requires reconciliation and
must not claim zero result loss.

## 5. Ownership and crash rules

- The lead agent's client/bridge may die without closing subagent terminals.
- The daemon stores the terminal provider, instance/session/tab/pane IDs where
  available, the backend session ID, and verified process identity. No adoption
  based on PID alone.
- Killing an agent must not close an entire terminal window containing other
  agents or the user's unrelated tabs.
- A closed tab, dead terminal server, and agent exit inside a tab are distinct
  events. None may be reported as successful job completion without evidence.
- If the result/control transport is lost while the agent lives, expose the
  uncertainty and block unsafe automatic redelivery.
- After a daemon restart, never automatically terminate a live interactive TUI.
  Verify identity and rebind through a supported native path; otherwise leave
  it live, mark it uncertain, and block future machine work for that
  session/workspace until an authorized operator or lead reconciles it.
- Releasing a database lock or changing status does not establish physical
  ownership. Starting a new attempt requires verified idle or safe termination
  of the old run. Stopping a live interactive session during recovery requires
  explicit human intent.
- Kill-on-close, process-group, or Job Object teardown policies apply only to
  headless runners under an explicitly accepted policy; they are not the
  default interactive recovery policy.
- Whether the lead agent survives or can be woken is separate from subagent
  terminal survival. Both paths must be tested.

If the user closes a terminal tab, the job may be interrupted. AgentTeamForge
must show the correct status and must not automatically reopen and rerun the
work without an established policy.

Interactive cancellation defaults to interrupting the current job turn while
keeping the agent and tab alive. Stopping the agent is a separate operation.
A deadline may escalate to terminating an owned agent only when that escalation
policy was explicitly accepted; otherwise an unverified interrupt is reported
as blocked/unconfirmed, not false success or a guaranteed hard cap.

## 6. Platform-specific risks

| Platform | Questions to verify |
| --- | --- |
| Linux / Herdr | Server and session binding, stable pane IDs, visible pane/tab, environment/correlation bootstrap, and lifetime independent of the host. |
| Windows | Reusing terminal windows, tab identification, `.cmd`/argv/Unicode, environment/correlation bootstrap, Job Objects, and GUI session versus service account. |
| macOS | Per-tab stable identity and targeted close, environment/correlation bootstrap, Apple Events/TCC and Accessibility requirements, scriptability without Accessibility access, prompt transport, and GUI session. |

A background service does not automatically have the right or ability to open
the user's GUI. M0 must determine whether a small **session launcher** is needed
in the logged-in desktop session on any platform. It must not move durable job
ownership back to the lead agent. When the user is locked out or logged out,
interactive launch must return a clear waiting state or error, never a hidden
headless launch.

Provider-specific environment inheritance is not assumed. The bootstrap
candidate is a private, short-lived, single-use nonce file in the user's runtime
directory. The child-side bridge exchanges the nonce locally for a
child-scoped capability, then invalidates it. Secrets never appear in argv or
logs. Restarted operators/leads reattach through authenticated local operator
delegation; same-OS-user access is useful transport authentication but is not a
sandbox against malicious filesystem access.

M0 tracks an explicit platform × mode × production-launch-context matrix for
`PATH`, home/profile, backend config, credential store, and authentication. The
full PoC runs T04, T07, and T10 once per platform with the daemon in the intended
production context (a temporary user service unit, logon task, or LaunchAgent
is sufficient; a full installer remains M3). On Linux, the reference daemon is
launched outside both Herdr and the client process tree that the crash test
terminates.

## 7. PoC and support gates

### Linux reference PoC

Requires real Claude and Codex sessions in Herdr and an explicitly selected
headless mode. Verify launch, human interaction, lead-agent-controlled follow-up,
status/result, turn interruption/agent stop, and client crash. Merely opening
the terminal is insufficient.

### Windows and macOS platform spikes

M0/M1 must exercise a real visible agent session and control/result transport
in the selected provider. Real interactive backend turns are required before
either platform is called supported. Headless or fake smoke tests do not count
as interactive platform support.

If a test machine is unavailable, report the platform as blocked; do not invent
passes. The Linux PoC may be evaluated separately, but **the full three-platform
requirement remains unverified**. An explicitly Linux-limited preview may ship
before the other platforms; the Windows and macOS requirements remain on the
roadmap rather than silently falling away.

## 8. Required terminal test cases

| ID | Test | Expected outcome |
| --- | --- | --- |
| T01 | Setup without an explicit choice | Requires a choice; no implicit headless default. |
| T02 | Persist choice, restart daemon | New agents use the saved mode and provider. |
| T03 | Provider missing or unable to open GUI | Clear error/wait state; no silent alternative launcher. |
| T04 | Launch Claude and Codex interactively | Real visible agent TUIs, not log tails. |
| T05 | Human gives an instruction in the agent tab | Agent responds there; daemon binding remains correct. |
| T06 | Lead agent sends a follow-up | The correct existing conversation receives it with honest delivery status. |
| T07 | Lead agent and MCP bridge crash | Subagent tabs continue; a new client can read/reconnect. |
| T08 | Interrupt one interactive turn with neighboring tabs open; separately exercise authorized agent stop | Interrupt keeps the intended tab/agent alive by default; an explicit stop affects only that agent, and unrelated tabs remain open. |
| T09 | Human closes tab / terminal server dies | Status shows interruption/uncertainty, not false completion. |
| T10 | Daemon restarts with live tabs | Live TUIs are not auto-killed; verified reconnection or honest reconciliation blocks new machine work, never a duplicate spawn. |
| T11 | Unicode/newlines/metacharacters in prompt | Instruction remains intact; no shell interpretation. |
| T12 | Change setup mode while agents are active | Only new agents are affected; existing bindings are preserved. |
| T13 | Service operation in a logged-in desktop session | The correct user's terminal opens; crash guarantees match manual operation. |
| T14 | Interactive job requested without an available desktop session | Explicit wait/error, no headless fallback or launch as another user. |
| T15 | Human and lead agent control concurrently | Human/foreign activity is recorded separately, human wins, and automatic follow-ups pause until explicitly reconciled idle; no fictitious exclusivity. |
| T16 | Model self-report, terminal text, or silence claims completion without authoritative evidence | Job does not become `completed`; evidence remains informational or the run needs reconciliation. |
| T17 | Interactive run completes while the daemon is down | Correlated completion replays from a verified backend record or bounded private spool; otherwise the pair is unsupported or reconciliation is explicit. |
| T18 | Interactive TUI waits for approval with no lead connected | Adapter reports an observable approval-blocked state or is explicitly unsupported; it never fabricates `running` or bypasses approval. |
| T19 | Retried `agent_start` after a lost response | The same operation and terminal binding are returned; no second tab or process is launched. |

T01–T12 and T15–T19 apply to the PoC/spike on each tested platform. T13–T14 must
also pass in a real service/autostart installation before the respective
platform's MVP.
