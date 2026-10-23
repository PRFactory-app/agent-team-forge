# 0013. Sandboxed launch mode

Status: proposed (draft for the product owner); launch mode, argument builders and daemon git hardening implemented (work item A2). Amends [ADR 0006](0006-bypass-permissions.md).

## Context

ADR 0006 launches every managed agent with its backend's bypass flags, so an
agent can do anything its OS user can do. PRFactory Epic 52 adds a per-repository
safety mode. A repository marked *Sandboxed* gives its jobs the **requested
mode** `sandboxed`; the worker decides the **effective mode** (`sandboxed` or
`fullAccess`) from what it can enforce for that backend on that machine, and
reports both. Before ATF reports an effective `sandboxed`, it needs to know
which arguments give which guarantees on each backend and platform. This record is the result of a spike
on Linux (Omarchy, kernel 7.2, `codex-cli 0.163.0`, Claude Code `2.1.301`,
bubblewrap 0.12.0) run in a throwaway git repository with an ATF-shaped linked
worktree: the agent cwd holds a `.git` *file* pointing at
`<checkout>/.git/worktrees/<name>`, outside the cwd.

## Decision

ATF gains a second launch mode, `sandboxed`, next to today's `fullAccess`
(ADR 0006, unchanged). The mode is chosen per job. In `sandboxed` mode ATF
sandboxes only Codex and Claude, with the arguments below. Pi, Cursor and Droid
have no enforceable sandbox: they run a Sandboxed job with full access and
report the downgrade (see below).

### Arguments per backend and OS

`<wt>` is the job worktree (cwd); `<gd>` is its git common dir; `<name>` the
worktree name. `<settings>` is a per-job settings **file path** (never inline
JSON, see the PowerShell 5.1 note in `InteractiveAgentCommand.cs`).

| Launch path | Linux | macOS | Windows |
|---|---|---|---|
| Codex headless (`codex exec`) | `exec --json --skip-git-repo-check -c 'permissions.atf.filesystem={"/"="read",":workspace_roots"="write","~/.ssh"="none","<gd>/worktrees/<name>"="write","<gd>/objects"="write","<gd>/refs"="write","<gd>/logs"="write", <cache dirs>}' -c permissions.atf.network.enabled=<Q4> -c default_permissions="atf" -c mcp_servers.agentteamforge.default_tools_approval_mode="approve"` (no `-s`; `exec` never asks for approval) | same; Seatbelt instead of bubblewrap — **not verified** | Codex has a native Windows sandbox (`windows.sandbox`); **not verified**, see below |
| Codex TUI | same `-c` set plus `-a never -C <wt>`, instead of `--dangerously-bypass-approvals-and-sandbox`. **Without** the trusted-project override (see "Settings sources"); whether the TUI then starts without a trust prompt is not yet verified | same, not verified | not verified |
| Claude headless (`claude -p`) | `-p --output-format stream-json --verbose --permission-mode dontAsk --setting-sources "" --settings <settings>` instead of `--dangerously-skip-permissions` | same; Seatbelt — not verified | **no sandbox** (Claude Code's sandbox supports macOS, Linux and WSL2, not native Windows): downgrades to full access, see below |
| Claude interactive | `--permission-mode dontAsk --setting-sources "" --settings <settings>` instead of `bypassPermissions` + `skipDangerousModePermissionPrompt`; keep env `CLAUDE_CODE_SANDBOXED=1` | same, not verified | no sandbox: downgrades to full access |
| Pi, Cursor, Droid | no sandbox: downgrade to full access (today's ADR 0006 flags), reason `sandbox_unsupported_backend` | same | same |

Claude `<settings>`:

```json
{"permissions": {"allow": ["Edit(./**)", "Read(./**)", "mcp__agentteamforge"]},
 "sandbox": {"enabled": true, "autoAllowBashIfSandboxed": true,
   "allowUnsandboxedCommands": false, "failIfUnavailable": true,
   "filesystem": {"denyRead": ["~/.ssh"], "allowWrite": ["<cache dirs>"]},
   "network": {"allowedDomains": ["<Q4>"]}},
 "disableAllHooks": true}
```

No unix-socket allowance: `atf mcp` is a stdio server started by the CLI
outside the sandbox and needs none (see guarantee 4).

### Settings sources ATF controls (requirement)

`--settings` is **additive**: user, project and local settings still merge in,
including their `allowWrite`, `additionalDirectories`, `Edit`/`Bash` allow
rules, `excludedCommands` and hooks. A user `excludedCommands` entry for an
interpreter plus a `Bash` allow rule runs that interpreter outside the sandbox,
and CLI hooks run unsandboxed and can execute a workspace script. Before A3 may
report an effective `sandboxed`, the launch must satisfy this policy:

- **Claude:** `--setting-sources ""` loads no user, project or local settings;
  only managed (admin) policy and ATF's `--settings` file apply. The settings
  file sets `disableAllHooks: true`. MCP comes only from `--mcp-config` with
  `--strict-mcp-config`. Spike: a workspace `.claude/settings.json` with a
  `SessionStart` hook wrote outside the sandbox with the default sources and did
  not run with `--setting-sources ""`. The sandboxed shell **can** write
  `.claude/settings.json` in the workspace, so a later process that loaded
  project settings would run the agent's hook; excluding the project source is
  what prevents it. Managed settings still merge and are out of ATF's control;
  A3 reports them in `atf doctor`.
- **Codex:** 0.163 has no equivalent of `--setting-sources`. The user's
  `$CODEX_HOME/config.toml` still applies for every key ATF does not override.
  A trusted project also loads the repository's `.codex/config.toml`: in the
  spike, a project MCP server ran outside the sandbox with
  `trust_level="trusted"` and did not with `"untrusted"`. The sandbox keeps
  `.codex/` read-only for the agent, so this is repository content, not agent
  writes. Requirement: sandboxed Codex jobs do not pass the trusted-project
  override, and A2 either runs Codex with an ATF-owned `CODEX_HOME` (ATF-written
  `config.toml` plus the user's auth) or makes A3 check the user config for
  keys that widen the sandbox. Until one of these is built, A3 must not report
  an effective `sandboxed` for Codex.

Codex's legacy `-s workspace-write [--add-dir]` is **not** used: it leaves every
path readable, makes `/tmp` and `$TMPDIR` writable unless
`sandbox_workspace_write.exclude_slash_tmp`/`exclude_tmpdir_env_var` are set,
and refuses `--add-dir <gd>` because it treats every `.git` directory as
read-only. The permission profile above (`default_permissions`) fixes all
three; it cannot be combined with `-s`.

### Guarantees that hold (Linux, both CLIs, the invocations tested in the spike)

1. **Shell writes are confined to an explicit boundary**: the workspace (cwd),
   the git allowances in guarantee 2, the cache directories in guarantee 6,
   and for Claude its per-user temp (`TMPDIR=/tmp/claude-<uid>`). Everything
   else, including `$HOME` and other `/tmp` paths, fails with `Read-only file
   system`. Claude's Write/Edit tools outside the
   cwd are denied by `dontAsk` (they are not covered by the bash sandbox; the
   `Edit(./**)` rule is what limits them).
2. **`git commit` works in an ATF worktree.** Codex needs the four
   `<gd>/{worktrees/<name>,objects,refs,logs}` writable entries; Claude allows
   the linked worktree's git dir on its own. In both, `<gd>/hooks` and
   `<gd>/config` stay read-only. That is **not** a complete git boundary: the
   worktree admin dir `<gd>/worktrees/<name>` must be writable for the index,
   so its `commondir` and `config.worktree` are writable too (verified for
   Codex). See "Git metadata attacks" below. (Codex prints a harmless
   `packed-refs.lock: Read-only file system` warning; the commit succeeds.)
3. **Reads are blocked only for listed paths.** Both CLIs leave the rest of
   the disk readable by shell commands. `~/.ssh` (or any other listed path) is
   blocked: Codex `"~/.ssh"="none"`, Claude `sandbox.filesystem.denyRead`.
   Claude's Read tool outside the cwd is denied by `dontAsk`.
4. **MCP and local sockets.** Both CLIs start stdio MCP servers *outside* the
   sandbox, so `atf mcp` reaches `daemon.sock` either way. Codex refuses MCP
   calls under `approval never` unless the server sets
   `default_tools_approval_mode="approve"`; Claude `dontAsk` needs the
   `mcp__agentteamforge` allow rule. The agent's **shell** needs no socket:
   Claude without a socket allowance blocks `AF_UNIX` (per-path
   `allowUnixSockets` did not work on Linux; `allowAllUnixSockets` would open
   every host socket, such as Docker and the SSH agent, and is not used).
   Codex blocks `AF_UNIX` while network is off; **with Codex network on, host
   unix sockets are reachable from the shell** (spike: `pong`). Native wake is
   daemon → agent and is not affected.
5. **No approval prompt in the tested invocations**: `codex exec`, the Codex
   TUI with `-a never` **and** the trusted-project override, `claude -p`, and
   the Claude TUI with `dontAsk` and `CLAUDE_CODE_SANDBOXED=1`. The Codex TUI
   *without* the trust override, as the table requires, is not yet tested and
   may show a trust prompt. Claude `acceptEdits` is **not** enough in the TUI: a
   request to an unlisted domain shows "Network request outside of sandbox —
   Do you want to allow this connection?" and blocks the job.
   `network.allowManagedDomainsOnly` would suppress it but is honoured only
   from managed (system) settings. `dontAsk` auto-denies instead (curl gets
   `403` from the sandbox proxy).
6. **Network is off by default** in both CLIs (the tested CLI behaviour; ATF
   turns it on, see the A5 implementation notes). When on: Claude per domain
   (`allowedDomains`, through its proxy); Codex v1 deliberately uses
   **unrestricted direct network** (`permissions.<p>.network.enabled=true`).
   Codex's domain-limited proxy (`permissions.<p>.network.domains` with
   `features.network_proxy`) exists but is deferred, not evaluated here.
   Package restores also need writable caches: `npm install` needs `~/.npm`;
   `dotnet restore` needs `~/.nuget/packages`, `~/.local/share/NuGet`
   (or `$XDG_DATA_HOME/NuGet`) and **`/tmp/.dotnet`** (.NET named-mutex shm,
   independent of `TMPDIR`); the directories must exist before launch. With
   those, both restores succeeded inside both sandboxes.
7. **Linux prerequisites**: bubblewrap with unprivileged user namespaces
   (both CLIs), plus `socat` for Claude's network proxy. On this machine
   `/usr/bin/bwrap` 0.12.0 works, `unshare -Ur` works, LSMs are
   `lockdown,capability,landlock,yama,bpf` (no AppArmor, so the Ubuntu 24.04
   `apparmor_restrict_unprivileged_userns` block does not apply here; it must
   be detected on Ubuntu). Claude `failIfUnavailable: true` makes Claude exit
   instead of silently running unsandboxed.
8. **`CLAUDE_CODE_SANDBOXED=1` stays**, but only as the workspace-trust skip it
   is: Claude reads it solely to treat the folder as trusted. It does not turn
   the sandbox on. Without it the interactive trust dialog appears even with
   the sandbox enabled. It must not be read as evidence that a job is
   sandboxed; the mode and A3's self-test are.

### Guarantees that do not hold

- **Reads are not confined.** Source, other repositories, `~/.aws`,
  `~/.config/gh`, browser profiles etc. stay readable unless listed. The deny-read list is a maintained list, not a boundary. The PRFactory
  warning text must match what holds, for example:

  > *Sandboxed:* when the worker can enforce it, the agent's shell can write
  > only to this job's workspace, its git data and package caches and a
  > private temp directory, and reaches the network only as configured. It can
  > still read most files on the machine and send what it reads to the model
  > provider. A worker or backend that cannot sandbox runs the job with full
  > access and reports that, and child jobs the agent starts may run with full
  > access in the same way.
- **The sandbox limits shell writes and shell egress, nothing more.** Other
  channels stay open and are not claimed:
  - *Sockets:* with Codex network on, every host unix socket (Docker, SSH
    agent, D-Bus) is reachable from the shell.
  - *Environment:* the agent inherits the daemon's environment, including
    credentials such as `AWS_*`, `GITHUB_TOKEN` or cloud SDK variables, unless
    A2 scrubs them (Codex's `shell_environment_policy` default exclusions are
    a partial filter, not a guarantee).
  - *Model output:* anything the agent can read it can send to the model
    provider and print into job output and PRFactory.
  - *Unsandboxed MCP:* MCP servers run outside the sandbox with the user's
    full rights; only `atf mcp` is configured, and it can submit jobs.
- **Network, when enabled for Codex, is unrestricted** (v1). With network
  off, shell network egress is blocked; that says nothing about model output,
  MCP calls or child jobs, which remain channels out.
- **Child jobs are an accepted containment limit.** A sandboxed agent can
  ask ATF through MCP for a child job on any backend. The child inherits the
  *requested* mode `sandboxed`, but under the downgrade rule a Pi, Cursor or
  Droid child (or any child on a machine whose self-test failed) runs with
  full access. Inheritance therefore does **not** prevent escape; it makes the
  downgrade visible. Each child reports its requested and effective mode.
- **Git common data is shared.** The agent can rewrite any ref or object in
  `<gd>`, including other branches of the operator's checkout; hooks and config
  are protected, history is not.
- macOS and Windows guarantees are unverified until the A6 run.

### Git metadata attacks on later daemon git runs

The daemon runs git in the job worktree after the agent finishes (commit,
push, status). With today's allowances an agent can prepare:

- **`commondir` redirection:** rewriting `<gd>/worktrees/<name>/commondir`
  points the worktree at another "common dir" the agent controls (inside the
  workspace), with its own `config`, `hooks` and refs.
- **`config.worktree`:** honoured when `extensions.worktreeConfig` is on; can
  set `core.fsmonitor`, `core.hooksPath`, `credential.helper`, filter or diff
  drivers — all of which run commands.
- **Symlinks:** the workspace or admin dir can hold symlinks that make a later
  daemon git write or read outside the workspace.
- `-c core.hooksPath=/dev/null` alone does not stop `core.fsmonitor`,
  credential helpers or filter/diff helpers from a redirected or worktree
  config.

For a registered sandboxed worktree, daemon git (`JobWorktree.RunAsync` and the
other git starts) validates, pins and hardens as described in "Implementation
notes (A2)". For every other directory it sets only `GIT_TERMINAL_PROMPT=0`.

### Windows (documentation only)

- Codex: **yes, feasible** — Codex ships a native Windows sandbox
  (`windows.sandbox`, with an elevated setup and a legacy variant).
- Claude Code: **no** native Windows sandbox; only under WSL2.
- Claude on Windows therefore has no enforceable sandbox.

### A worker without a sandbox downgrades (PO decision)

- A worker that cannot enforce a sandbox for a job **runs a Sandboxed job with
  full access**. It never refuses the job.
- It reports the downgrade on durable acceptance: `effectiveSafetyMode`
  (`fullAccess`) and `safetyDowngradeReason`:
  - `sandbox_unavailable`: the backend has a sandbox but this machine cannot
    enforce it (Claude on native Windows, or an A3 self-test that failed, for
    example no bubblewrap or blocked user namespaces);
  - `sandbox_unsupported_backend`: the backend has no enforceable sandbox at
    all (Pi, Cursor, Droid), on every OS.
- An enforced job reports `effectiveSafetyMode=sandboxed` and no reason.
- `sandbox-v1` means "this worker understands the `safetyMode` contract and
  reports the mode it enforced". It does **not** mean "this worker can always
  enforce a sandbox". A Windows worker reports `sandbox-v1` too.
- The A3 startup self-test decides `effectiveSafetyMode` for each CLI on the
  machine. It does not decide whether `sandbox-v1` is reported.
- This replaces the Epic 52 S2 plan's reading of `409 sandbox_v1_required` as
  "worker cannot sandbox". The 409 still applies to a worker that does not
  speak the contract at all (no `sandbox-v1`, for example ATF v0.1.8), and
  PRFactory shows the reported downgrade instead of blocking it.

## Consequences

- A2 adds `SafetyMode` per job, carried through follow-up and revive, and
  builds the arguments above from a per-job settings file. For Pi, Cursor and
  Droid it does not refuse a Sandboxed job: it launches with the ADR 0006
  flags and records `effectiveSafetyMode=fullAccess`,
  `safetyDowngradeReason=sandbox_unsupported_backend`.
- A2: **child jobs that a sandboxed agent starts over MCP** (`submit_job`,
  `follow_up` and the like through `atf mcp`, which runs outside the sandbox)
  **inherit the requested mode `sandboxed`**; a child request cannot lower it.
  Every job, child or not, records and reports both its requested and its
  effective mode. This makes downgrades visible; it does not contain them
  (accepted limit above).
- A2: **the git hooks and config stay read-only** for the agent. Only
  `<gd>/worktrees/<name>`, `objects`, `refs` and `logs` are writable for Codex;
  `<gd>/hooks` and `<gd>/config` must never be added. Claude already keeps them
  read-only.
- A2: **validate the worktree admin files and harden daemon git** before any
  daemon git run in a worktree a sandboxed agent touched: check that
  `<gd>/worktrees/<name>/commondir` still resolves to the recorded common dir
  and that no `config.worktree` (or unexpected symlink in the admin dir)
  appeared, failing the job otherwise; and run git with
  `-c core.hooksPath=/dev/null -c core.fsmonitor= -c credential.helper=`
  (plus ATF's own credential path where a push needs one) and without filter
  or diff drivers from repository config.
- A2: **settings sources** as required above (Claude `--setting-sources ""`,
  `disableAllHooks`, `--strict-mcp-config`; Codex without project trust and
  with an ATF-owned or checked user config) and an environment scrub for
  credential variables.
- A3's self-test must check, per CLI: an outside write fails, a
  `~/.ssh`-style read fails, the CLI did not fall back to unsandboxed, and the
  settings-source policy above is in force. Its result is the
  `effectiveSafetyMode` for Sandboxed jobs on that CLI.
- A4 adds `effectiveSafetyMode` and `safetyDowngradeReason` to the acceptance
  report, and sends `sandbox-v1` whenever ATF implements the contract.
- ADR 0006 stays the rule for `fullAccess` jobs and local jobs by default.
- The network rule (A5, Q4) chooses Codex network on/off and the Claude
  domain list; the cache directories are part of it. Codex's domain proxy is a
  later option.

## Implementation notes (A2)

- The requested mode is stored per job as `;sandbox=1` and the effective mode as
  `;sandbox_effective=1|0[;sandbox_downgrade=<reason>]` in the job options. A
  full-access job stores nothing (absent keys mean `fullAccess`, so fingerprints of
  earlier jobs are unchanged). Follow-ups and revives copy all three keys.
- The effective mode is decided once, at acceptance, by `SandboxCapability.Default`
  and the state-dir rule below: Claude and Codex enforce on Linux for headless
  launches only; Pi, Cursor and Droid give `sandbox_unsupported_backend`; everything
  else gives `sandbox_unavailable`. That covers Claude and Codex on macOS and Windows
  (until the A6 run verifies them), and **every interactive launch (Herdr, Windows
  Terminal, macOS terminal)**: a pane outlives its turn and ATF has no reliable exit
  signal, so daemon git could race a live writer, and Herdr starts the canonical
  executable in a shell whose startup files run first, so no environment allow-list
  can be enforced there. Where the worker cannot prove environment containment, it
  downgrades and reports (PO decision: fail closed means downgrade plus report). The interactive argument builders of the table are
  implemented and tested, and stay unreachable until pane lifetime and a launch
  wrapper are provable. A3 replaces the capability with the startup self-test
  (bubblewrap, `socat`, outside write and `~/.ssh` read probes).
- Codex settings sources: a sandboxed `codex exec` (and `exec resume`) runs with
  `--ignore-user-config --ignore-rules`, so the user's `config.toml` (sandbox keys,
  project trust, MCP servers) and execpolicy rules never apply. The interactive Codex
  has no such flag in 0.163, which is one more reason interactive launches are
  downgraded. ATF does not run Codex with its own `CODEX_HOME`, because sessions and
  transcripts live there.
- A managed child's submit is resolved through the daemon's own
  `managed-child:<job>` lead binding, never from a caller-supplied job id; if that
  job requested `sandboxed`, the child does too, whatever `safety_mode` it passes.
- Daemon git in a sandboxed worktree. ATF records the admin dir and common dir of
  every linked worktree that a sandboxed launch gets a writable admin dir for (job
  worktrees from their origin checkout when created; any other linked worktree at its
  first sandboxed launch) in `<state>/sandbox/worktrees.json`, which survives a
  restart. Every daemon git start there (`JobWorktree.RunAsync`, the attachment diff,
  the Herdr workspace label) then: validates `.git`, the admin dir, `commondir`,
  `config.worktree` and symlinks against the record, and refuses otherwise; refuses
  while a headless agent of the worktree is alive (its marked processes are reaped at
  run end); is pinned with `GIT_DIR`, `GIT_COMMON_DIR`, `GIT_WORK_TREE` and
  `GIT_CONFIG_NOSYSTEM` so it never reads the mutable pointers; and runs with
  `core.hooksPath=/dev/null`, `core.fsmonitor=`, `diff.external=`, `core.pager=cat`,
  `credential.helper=` (not for network commands, which keep the operator's helper
  until A4's own credential path) and every filter, diff and merge driver blanked.
  Drivers are recomputed for every invocation from the pinned config, never cached.
  Conditional includes are neutralised by reading every included file, whatever its
  `includeIf` condition, and blanking by driver name, so a branch switch cannot turn a
  driver on; this is simpler than building a global config without includes, and it
  keeps the operator's identity and credential settings untouched. An include file
  inside a writable root makes git refuse. The one exception to blanking is the LFS
  filter, kept only when every definition of each of its commands, in any
  included file, is exactly git-lfs's grammar (`<exe> clean -- %f`,
  `<exe> smudge -- %f`, `<exe> filter-process`) and `<exe>` resolves through links to a
  path outside every writable root. A bare `git-lfs` is searched on an all-absolute
  `PATH` with executable-access semantics (a non-executable file is skipped, as the
  shell skips it; a `PATH` entry inside a writable root refuses the filter), and the
  commands are then pinned to the resolved absolute path, so the shell never searches
  `PATH` again. User and global git config are
  not blanked: the sandbox cannot write them, and ATF does not set `GIT_CONFIG_GLOBAL`
  (PO decision): the user's `~/.gitconfig` keeps applying, and the hardening stays in
  the `-c` flags and pinned environment above. A job whose worktree fails validation at
  completion fails with `sandbox_worktree_tampered`.
- State-dir overlap: a sandboxed launch whose writable roots (the workspace, the
  worktree's git allowances, cache directories) contain the ATF state dir or reach into
  it anywhere but a job worktree under `<state>/worktrees` would let the agent edit the
  database, the registry, the settings or the managed tokens. At acceptance such a job
  (for example `worktree=false` with `cwd` at `$HOME`) is downgraded with
  `sandbox_unavailable`; a stored sandboxed job that still overlaps at dispatch fails
  with `sandbox_state_overlap` before launch. Containment is root-aware: a workspace
  of `/` contains the state dir.
- Trusted git layout: every sandboxed launch is bound to the layout recorded for the
  directory it runs in (job worktrees from their origin at creation; any other
  directory, linked worktree or plain checkout, at its first sandboxed launch, first
  record wins). When a layout was recorded, the launch's git allowances are built from
  it and not rediscovered from disk. For a validated "no git layout" baseline,
  `SandboxLaunch.Create` still scans the directory, but dispatch has already refused a
  layout that appeared after the baseline, so that scan finds none. A nested `.git` (with its own `commondir`) that the agent
  planted in a subdirectory no longer matches the recorded root, admin dir and common
  dir, so the next launch fails with `sandbox_worktree_tampered` before any backend
  starts. A directory with no git layout at its first sandboxed launch is recorded
  as a validated "no git layout" baseline (an empty common dir in the registry); a
  `.git` that appears there later is a mismatch and fails the same way.
- Launch mode changes: dispatch checks the stored decision against the backend that will
  actually launch it. A job or follow-up that was accepted as sandboxed and now reaches
  an interactive backend (the daemon was reconfigured, or a session is resumed under a
  new mode) fails with `sandbox_unavailable` before any launch, so a job that reports
  sandboxed never runs without the sandbox.
- The working directory of a sandboxed job in its own worktree is resolved through
  symlinks and must stay inside the worktree root; otherwise the job fails with
  `sandbox_worktree_tampered` and nothing launches. The resolved path is what the agent
  gets.
- The network rule, package caches and extra deny-read paths are `SandboxPolicy` and
  belong to A5. Network rule (PRFactory contract): network stays on in a sandboxed
  launch. Codex gets `network.enabled=true` (unrestricted); Claude gets
  `sandbox.network.allowedDomains`, because Claude refuses `*` there. The per-machine
  opt-in `"sandbox_network": false` in `profile.json` turns the network off for both.
- Per-machine lists (PO decision): `profile.json` keeps `sandbox_allowed_domains`
  (Claude's domain list) and `sandbox_cache_paths` (writable package caches); absent
  keys mean the defaults in `SandboxPolicy`. The web console Settings edits them over
  IPC (`sandbox_settings_get`/`_put`), which validates and rewrites only these keys.
  The dispatcher reads the profile at each sandboxed launch, so a change applies to
  the next launch without a restart; an unreadable or invalid profile keeps the last
  good settings, seeded with the profile the daemon validated at start (never the
  defaults), and the Settings view reports the read error. Domain entries are a host or `*.` plus a registered domain; `*`, `*.*`,
  `*.<tld>`, schemes, paths and ports are refused. A cache path becomes a Codex
  `"<path>"="write"` filesystem entry and a Claude `sandbox.filesystem.allowWrite`
  entry. Hard rule: a cache path that equals, contains or lies inside `~/.ssh`,
  `~/.aws`, `~/.config/gh` or `~/.netrc`, the root,
  the home directory or one of its parents, or that overlaps the ATF state dir is
  refused at save and dropped again at launch. Both sides (cache, secrets, home, state
  dir) are compared as written and as resolved through every symlink; an unresolvable
  path is refused. The launch emits the validated physical path, never the configured
  spelling, so retargeting a cache link after `SandboxLaunch.Create` cannot move the
  grant. Each entry is resolved once, and that one snapshot is both checked and
  granted, so a retarget between check and grant cannot slip in; a cache inside another writable root (the workspace, git allowances, another
  cache) is dropped, since its parents are agent-writable. `~/.dotnet` is deliberately not a default (its `tools` directory is
  on `PATH`). Null or NUL list entries are validation errors. A cache path that does not exist is
  skipped, not created (Codex refuses a nonexistent path). Codex stays unrestricted
  when the network is on.
  Only two modes exist, Sandboxed and Full access (PO decision); a read-only mode is a
  later change to this ADR.
- Deny-read (PO decision): the shell cannot read `~/.ssh`, `~/.aws`, `~/.config/gh` and
  `~/.netrc`. A path that does not exist on the machine is left out of the Codex
  profile and the Claude settings, so Codex does not fail on a nonexistent path.
- Environment: a sandboxed headless launch hands the agent an allow-list (`PATH`,
  `HOME`, locale, `XDG_*`, proxy and CA variables, `CODEX_HOME`, `CLAUDE_CONFIG_DIR`,
  and the agent's own `ANTHROPIC_*`, `CLAUDE_CODE_*`, `OPENAI_*`, `CODEX_*`), not a
  deny-list: the child's environment is rebuilt from it. `AWS_*` and `GOOGLE_*` stay
  off the list (PO decision), so a Claude that signs in through Bedrock or Vertex
  (`CLAUDE_CODE_USE_BEDROCK` or `CLAUDE_CODE_USE_VERTEX` set) cannot run sandboxed: the
  job downgrades to full access and reports `sandbox_unavailable`. The macOS and Windows terminal wrappers
  also unset or remove everything off the list before starting the agent, for when
  those launch paths become reachable.

## Implementation notes (A3, A4)

- **A3 self-test.** A headless daemon on Linux runs `SandboxSelfTest` once at
  start; `atf doctor` runs the same probe. Codex is probed with
  `codex sandbox` and ATF's permission profile, Claude with bubblewrap (and the
  presence of socat). The shell probe must write in its workspace, must fail
  to write in `$HOME`, and, for Codex, must fail to list each existing secret
  path. No model is called. A failed probe adds `sandbox_unavailable` for that
  CLI on top of `SandboxCapability.Default`. Claude's read-deny rules come from
  ATF's settings file and are not probed; a full Claude probe needs a model
  turn and is left to the A6 live run.
- **A4 contract.** See [docs/prfactory-connector.md](../prfactory-connector.md)
  ("Safety mode"). The acceptance report is decided before any job exists,
  from the same probe the jobs use (`AcceptJob.SandboxDowngrade`).

## Evidence (Linux spike, `/tmp/atf-a1-spike`)

A probe script run by the agent's shell tool; outputs abridged.

```text
codex exec -s workspace-write (exclude /tmp), with --add-dir <gd> or <checkout>:
  P1 outside/home write: BLOCKED/BLOCKED   P2 git commit: FAILED (.git/worktrees/... read-only)
  P3 ~/.ssh/id_ed25519 (dummy): ALLOWED    P4 unix socket: PermissionError (network off)
  MCP call: "MCP tool call requires approval, but approval policy is never"
codex exec and codex TUI (-a never), permission profile above, network on:
  P1 BLOCKED/BLOCKED   P2 git commit: OK   .git/hooks, .git/config: BLOCKED
  P3 BLOCKED   P4 unix socket: pong   MCP socket_probe: pong   P6 https: 200
claude -p and claude TUI, sandbox on, dontAsk (P4 run with allowAllUnixSockets, now dropped):
  P1 BLOCKED/BLOCKED   P2 OK   hooks/config BLOCKED   P3 BLOCKED   P4 pong   P6 200
  Read tool / Write tool outside cwd: denied   MCP socket_probe: pong
claude TUI, acceptEdits, unlisted domain:
  "Network request outside of sandbox ... Do you want to allow this connection?"
  same with dontAsk: curl gets 403 from the sandbox proxy, no prompt
claude TUI without CLAUDE_CODE_SANDBOXED=1: workspace trust dialog
claude -p with the final arguments (--setting-sources "", settings above, --strict-mcp-config):
  P1 BLOCKED/BLOCKED   P2 OK   hooks/config BLOCKED   P3 BLOCKED
  P4 shell unix socket: PermissionError   P6 200   MCP socket_probe: pong
workspace .claude/settings.json SessionStart hook writing outside the workspace:
  default setting sources: ran   --setting-sources "": did not run
workspace .codex/config.toml MCP server writing outside the workspace:
  project trust_level="trusted": ran   "untrusted": did not run
codex sandbox, <gd>/worktrees/<name> writable:
  commondir and config.worktree: writable
dotnet restore in the Codex sandbox without /tmp/.dotnet writable:
  mkdir("/tmp/.dotnet/shm/session1") == -1; errno == EROFS
```

Reproduce: create a repo plus `git worktree add`, a dummy key under a fake
`HOME` (`HOME=<fake> CODEX_HOME=~/.codex` or `CLAUDE_CONFIG_DIR=~/.claude`),
and run each command line above with a one-line prompt that runs the probe.
