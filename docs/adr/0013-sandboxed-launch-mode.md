# 0013. Sandboxed launch mode

Status: proposed (draft for the product owner). Amends [ADR 0006](0006-bypass-permissions.md).

## Context

ADR 0006 launches every managed agent with its backend's bypass flags, so an
agent can do anything its OS user can do. PRFactory Epic 52 adds a per-repository
safety mode: a repository marked *Sandboxed* must only run on a worker that
reports `sandbox-v1` and launches the agent inside the CLI's own sandbox.
Before ATF claims that capability, it needs to know which arguments give which
guarantees on each backend and platform. This record is the result of a spike
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
| Codex TUI | same `-c` set plus `-a never -C <wt>` and the existing trusted-project override, instead of `--dangerously-bypass-approvals-and-sandbox` | same, not verified | not verified |
| Claude headless (`claude -p`) | `-p --output-format stream-json --verbose --permission-mode dontAsk --settings <settings>` instead of `--dangerously-skip-permissions` | same; Seatbelt — not verified | **no sandbox** (Claude Code's sandbox supports macOS, Linux and WSL2, not native Windows): downgrades to full access, see below |
| Claude interactive | `--permission-mode dontAsk --settings <settings>` instead of `bypassPermissions` + `skipDangerousModePermissionPrompt`; keep env `CLAUDE_CODE_SANDBOXED=1` | same, not verified | no sandbox: downgrades to full access |
| Pi, Cursor, Droid | no sandbox: downgrade to full access (today's ADR 0006 flags), reason `sandbox_unsupported_backend` | same | same |

Claude `<settings>`:

```json
{"permissions": {"allow": ["Edit(./**)", "Read(./**)", "mcp__agentteamforge"]},
 "sandbox": {"enabled": true, "autoAllowBashIfSandboxed": true,
   "allowUnsandboxedCommands": false, "failIfUnavailable": true,
   "filesystem": {"denyRead": ["~/.ssh"], "allowWrite": ["<cache dirs>"]},
   "network": {"allowedDomains": ["<Q4>"], "allowAllUnixSockets": true}}}
```

Codex's legacy `-s workspace-write [--add-dir]` is **not** used: it leaves every
path readable, makes `/tmp` and `$TMPDIR` writable unless
`sandbox_workspace_write.exclude_slash_tmp`/`exclude_tmpdir_env_var` are set,
and refuses `--add-dir <gd>` because it treats every `.git` directory as
read-only. The permission profile above (`default_permissions`) fixes all
three; it cannot be combined with `-s`.

### Guarantees that hold (Linux, both CLIs, all four launch paths)

1. **Shell writes outside the workspace fail** (`Read-only file system`), for
   `$HOME` and for other `/tmp` paths. Claude's Write/Edit tools outside the
   cwd are denied by `dontAsk` (they are not covered by the bash sandbox; the
   `Edit(./**)` rule is what limits them).
2. **`git commit` works in an ATF worktree.** Codex needs the four
   `<gd>/{worktrees/<name>,objects,refs,logs}` writable entries; Claude allows
   the linked worktree's git dir on its own. In both, `<gd>/hooks` and
   `<gd>/config` stay **read-only**, so an agent cannot plant a hook or
   `core.hooksPath` that later runs unsandboxed. (Codex prints a harmless
   `packed-refs.lock: Read-only file system` warning; the commit succeeds.)
3. **Reads are blocked only for listed paths.** Both CLIs leave the rest of
   the disk readable by shell commands. `~/.ssh` (or any other listed path) is
   blocked: Codex `"~/.ssh"="none"`, Claude `sandbox.filesystem.denyRead`.
   Claude's Read tool outside the cwd is denied by `dontAsk`.
4. **MCP and local sockets.** Both CLIs start stdio MCP servers *outside* the
   sandbox, so `atf mcp` reaches `daemon.sock` either way. Codex refuses MCP
   calls under `approval never` unless the server sets
   `default_tools_approval_mode="approve"`; Claude `dontAsk` needs the
   `mcp__agentteamforge` allow rule. A unix socket opened **from the agent's
   shell** needs Codex network enabled (seccomp blocks `AF_UNIX` otherwise)
   or Claude `allowAllUnixSockets` (per-path `allowUnixSockets` does not work
   on Linux). Native wake is daemon → agent and is not affected.
5. **No approval prompt** with the arguments above. Codex TUI ran unattended
   with `-a never`. Claude `acceptEdits` is **not** enough in the TUI: a
   request to an unlisted domain shows "Network request outside of sandbox —
   Do you want to allow this connection?" and blocks the job.
   `network.allowManagedDomainsOnly` would suppress it but is honoured only
   from managed (system) settings. `dontAsk` auto-denies instead (curl gets
   `403` from the sandbox proxy).
6. **Network is off by default** in both. Allowed: Codex all-or-nothing
   (`permissions.<p>.network.enabled`); Claude per domain (`allowedDomains`).
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
  `~/.config/gh`, browser profiles etc. stay readable unless listed. The
  PRFactory warning text must say "cannot write outside the workspace", not
  "cannot see your machine". The deny-read list is a maintained list, not a
  boundary.
- **Network, when enabled for Codex, is unrestricted.** Only Claude can limit
  it per domain. A data-exfiltration guarantee exists only with network off.
- **MCP servers run unsandboxed.** A sandboxed agent can ask ATF through MCP
  to submit a child job; children must inherit the parent's mode (A2), or the
  sandbox is escaped through ATF itself.
- **Git common data is shared.** The agent can rewrite any ref or object in
  `<gd>`, including other branches of the operator's checkout; hooks and config
  are protected, history is not.
- macOS and Windows guarantees are unverified until the A6 run.

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
  **inherit `sandboxed`**. A child request cannot lower the mode; otherwise the
  sandbox is escaped through ATF itself.
- A2: **the git hooks and config stay read-only** for the agent. Only
  `<gd>/worktrees/<name>`, `objects`, `refs` and `logs` are writable for Codex;
  `<gd>/hooks` and `<gd>/config` must never be added. Claude already keeps them
  read-only. The daemon still runs its own git in such worktrees with hooks
  disabled (`-c core.hooksPath=/dev/null`).
- A3's self-test must check, per CLI: an outside write fails, a
  `~/.ssh`-style read fails, and the CLI did not fall back to unsandboxed. Its
  result is the `effectiveSafetyMode` for Sandboxed jobs on that CLI.
- A4 adds `effectiveSafetyMode` and `safetyDowngradeReason` to the acceptance
  report, and sends `sandbox-v1` whenever ATF implements the contract.
- ADR 0006 stays the rule for `fullAccess` jobs and local jobs by default.
- The network rule (A5, Q4) chooses Codex network on/off and the Claude
  domain list; the cache directories are part of it.

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
claude -p and claude TUI, sandbox on, dontAsk, final settings:
  P1 BLOCKED/BLOCKED   P2 OK   hooks/config BLOCKED   P3 BLOCKED   P4 pong   P6 200
  Read tool / Write tool outside cwd: denied   MCP socket_probe: pong
claude TUI, acceptEdits, unlisted domain:
  "Network request outside of sandbox ... Do you want to allow this connection?"
  same with dontAsk: curl gets 403 from the sandbox proxy, no prompt
claude TUI without CLAUDE_CODE_SANDBOXED=1: workspace trust dialog
dotnet restore in the Codex sandbox without /tmp/.dotnet writable:
  mkdir("/tmp/.dotnet/shm/session1") == -1; errno == EROFS
```

Reproduce: create a repo plus `git worktree add`, a dummy key under a fake
`HOME` (`HOME=<fake> CODEX_HOME=~/.codex` or `CLAUDE_CONFIG_DIR=~/.claude`),
and run each command line above with a one-line prompt that runs the probe.
