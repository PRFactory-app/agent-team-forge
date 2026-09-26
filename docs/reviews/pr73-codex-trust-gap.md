# PR #73: Codex folder-trust gap audit

2026-09-26. Baseline: ATF `cd5a2d2`; separately inspected Windows fix `df8e298`.
Reference: [PR #73](https://github.com/mikaelliljedahl/agentic-coder-teams-mcp/pull/73),
head `3c19d2020e7e2ce0fe5a698d56e6e4134ba177da`. Read AGENTS.md and section 1 of
`downstream-delivery-gap.md` first, then PR description, full diff, checked-in
plan/review/smoke record. GitHub comments, reviews and inline comments were empty.
The gh wrapper polluted token capture; retried with the installed gh binary and
explicit `auth token --user mikaelliljedahl`. No auxiliary account was used.

**Keep the process-only override. `df8e298` shares it across interactive launchers,
but does not close path identity, Codex shim transport or isolated-home gaps.**
No evidence supports introducing persistent trust writes or a new approval gate.

Citation key: `B:` = `src/AgentTeamForge.Business/Features/`; `T:` =
`tests/AgentTeamForge.Tests/Features/`; unqualified ATF citations use baseline.
`F:` = Business `Agents/Terminals/` at `df8e298` (not merged-baseline line numbers).
`R:` = reference paths at the pinned PR head. Reference repository was read-only.
Verdicts distinguish source coverage from live validation; no build, tests,
agent launches, setup, push or merge were run for this documentation-only audit.

## Findings and smallest fixes

1. **Interactive launch coverage — Partial on main; Solved in shared arguments.**
   `B:Agents/Terminals/HerdrAgentControl.cs:196` already emits `-c projects={...}`;
   main `WtTabControl.cs:291` lacks it, and `MacTabControl.cs:149` uses that builder.
   `F:InteractiveAgentCommand.cs:20`, `F:WtTabControl.cs:296` and
   `F:MacTabControl.cs:149` close the missing-argument gap for all three providers.
   This is source closure, not Windows/macOS runtime proof. Apply the shared fix;
   complete the native Windows smoke after the fixes below.

2. **Canonical paths, symlinks and trailing separators — Gap.**
   Admission only calls `Path.GetFullPath` (`B:Jobs/AcceptJob.cs:181`); the builder
   uses that spelling unchanged (`F:InteractiveAgentCommand.cs:23`). It neither
   resolves symlink/junction components nor explicitly trims trailing separators.
   PR resolves once and validates the same key (`R:src/claude_teams/backends/codex.py:474`).
   Its source research says canonical cwd is checked before raw cwd
   (`R:docs/features/codex-trust-prompt/plan.md:27`): a canonical `untrusted` entry
   can defeat ATF's trusted alias even where raw-cwd fallback normally works.
   Fix: one launch-time trust-key helper resolving the existing directory and
   all link components, preserving filesystem roots while removing surplus
   separators. Test a symlink with a canonical untrusted entry and `cwd/`.

3. **Windows case, drive letters and UNC; macOS case — Gap / Partial.**
   `F:InteractiveAgentCommand.cs:23` preserves case; its new test even expects
   uppercase `C:` (`df8e298:T:Agents/Terminals/WtInteractiveBackendTests.cs:139`).
   Codex's lowercase Windows key wins before case-insensitive fallback, so an
   existing lowercase `untrusted` entry can beat ATF's mixed-case override
   (`R:docs/features/codex-trust-prompt/plan.md:28`). PR ASCII-lowercases only on
   Windows (`R:src/claude_teams/backends/codex.py:490`). Fix: apply that mapping
   after canonicalization; preserve non-ASCII characters. Cover drive roots,
   UNC shares and extended-path spelling against native Codex normalization.
   UNC runtime support is unproven in both audits; TOML backslash escaping alone
   does not prove matching. On macOS preserve case and resolve actual filesystem
   spelling; do not blindly lowercase (volumes can be case-sensitive). PR has no
   macOS live evidence. Add a mixed-case alias smoke there, without blocking Linux.

4. **TOML and shell escaping — Solved for native transports; shim Gap.**
   `B:Agents/Terminals/HerdrAgentControl.cs:241` and
   `F:InteractiveAgentCommand.cs:59` escape backslashes, double quotes, C0 and DEL
   in a TOML basic-string key inside the value, avoiding dotted-key splitting.
   `T:Agents/Terminals/HerdrInteractiveBackendTests.cs:343` covers quotes/dots.
   PR instead uses literal strings and rejects quote/control paths. Its smart-quote
   finding is already handled by `B:Agents/Terminals/PowerShellText.cs:9`;
   native WT uses explicit Windows argv quoting (`F:WtTabControl.cs:262`, `:322`).
   However, native resolution can fall back to `.cmd` (`F:WtTabControl.cs:434`),
   invoking PowerShell's shim path (`:275`). `WindowsCliLaunch.cs:42` under
   `B:Agents/Backends/` rejects cmd metacharacters but not embedded TOML quotes;
   it does not guarantee their round-trip through PowerShell 5.1/cmd.
   PR refuses this transport (`R:src/claude_teams/backends/codex.py:478`).
   Fix: refuse interactive Codex `.cmd` fallback before tab creation with a native
   `codex.exe` remedy. ATF's wrapper avoids the PR's direct-WT hazard already.

5. **Versions and persistent config fallback — Partial validation; writes N/A.**
   PR research inspected Codex `rust-v0.156.1`, with installed CLI `0.157.1`
   (`R:docs/features/codex-trust-prompt/plan.md:4`, `:182`). The Linux smoke passed
   fresh/untrusted cwd and resumed follow-up, with unchanged config bytes
   (`R:docs/features/codex-trust-prompt/implementation.md:74`, `:85`).
   No reviewed comment/diff reports a version ignoring `projects=`, a minimum
   supported version, or any version requiring config writes. Windows smoke is
   still TODO (`:86`); Windows CI fixes were test portability fixes (`:121`).
   ATF emits the flag without a version check (`F:InteractiveAgentCommand.cs:20`).
   Fix: record the exact native CLI version in Windows smoke results and repeat
   fresh/untrusted/resume plus config byte comparison on upgrades. On failure,
   diagnose key/transport/policy first; do not silently persist trust.

6. **Resume, follow-up and headless — Solved / N/A for the trust-screen cause.**
   Interactive resume reuses the same trust arguments before `resume <id>`
   (`B:Agents/Terminals/HerdrAgentControl.cs:223`; `F:InteractiveAgentCommand.cs:50`).
   Retained Herdr sessions keep their original invocation (`HerdrInteractiveBackend.cs:47`
   under `B:Agents/Terminals/`); replacement launches rebuild arguments.
   Headless fresh/resume has no projects override (`B:Agents/Backends/CodexExecBackend.cs:55`),
   and `--skip-git-repo-check` is not project trust. Codex exec has no folder-trust
   UI according to PR research (`R:docs/features/codex-trust-prompt/plan.md:25`).
   PR explicitly refuses headless trust because it changes project loading without
   clearing a prompt. Do not add the flag to exec merely for startup parity;
   project-config parity would be a separate deliberate behavior change.

7. **Exact cwd, worktrees and parent trust — Solved for narrow key scope.**
   Both emit only cwd, not git root or its ancestors (`F:InteractiveAgentCommand.cs:23`;
   `R:src/claude_teams/backends/codex.py:486`). PR research distinguishes active
   cwd trust from per-directory config-layer trust and main-checkout fallback
   (`R:docs/features/codex-trust-prompt/plan.md:27`). Trusting a nested cwd does not
   itself trust ancestor `.codex` layers; a linked worktree may still load
   main-checkout hooks. No fix should replace cwd with a broader git root.
   Add nested-cwd/linked-worktree cases to the canonicalization smoke; do not
   describe this setting as an execution boundary around the exact directory.

8. **Opt-in versus always-on — N/A as a policy gap; document the consequence.**
   PR defaults off and persists opt-in for follow-ups (`R:src/claude_teams/server_simple.py:3648`).
   ATF always trusts interactive Codex cwd (`F:InteractiveAgentCommand.cs:20`),
   consistent with AGENTS.md's unattended bypass direction. Trust can enable
   repository config, hooks and policies before the task prompt; bypass approvals
   alone does not grant directory trust. Managed enforcement can still constrain
   behavior. [Official configuration guidance](https://learn.chatgpt.com/docs/config-file/config-basic)
   confirms CLI precedence and trusted project layers. Small documentation fix:
   state that accepted interactive jobs trust their checkout for that invocation,
   including explicit user-level untrusted overrides; no new approval ceremony.

9. **Isolated CODEX_HOME and follow-up binding — Partial, with a real Gap.**
   ATF reader honors the variable (`B:Agents/Terminals/InteractiveTranscriptReader.cs:83`)
   and Linux allowlists it (`LaunchEnvironment.cs:15`), unlike PR's original reader.
   But relative homes are not anchored once, and an existing WT window's wrapper
   does not export the daemon's home (`F:WtTabControl.cs:248`); macOS also inherits
   terminal environment (`F:MacTabControl.cs:147`). Reused Herdr tabs explicitly
   receive bootstrap/trust environment, not CODEX_HOME (`B:Agents/Terminals/HerdrTerminal.cs:168`).
   PR's live failure was `binding_unverified`, fixed by shared absolute-home
   resolution and propagation (`R:docs/features/codex-trust-prompt/implementation.md:83`).
   Fix: anchor configured CODEX_HOME at daemon startup and pass the same absolute
   value to every child and transcript reader. Test relative home and an already
   open terminal with a different home, including resumed follow-up.

**Before Windows re-test:** apply `df8e298`, fix trust-key normalization/casing,
refuse the Codex shim, and propagate absolute CODEX_HOME. Then smoke native WT
with fresh and mixed-case-untrusted cwd, spaces/smart quotes, junction/trailing
slash/UNC cases, follow-up and byte-identical config. Record unsupported cases
honestly; neither PR's Linux smoke nor ATF's argv tests establish Windows support.
