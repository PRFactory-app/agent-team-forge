# Easy installation for v0.0.1

## Decision and scope

Ship Linux x64 first: one download/install command, then one setup command. No SDK, clone, manual extraction, or hand-written MCP config for end users. Backend CLI installation and provider login remain prerequisites, not ATF responsibilities. macOS arm64 and Windows x64 remain tester-only until exercised on those machines.

Proposed public path, **after a release exists**:

```sh
curl -fsSL https://github.com/PRFactory-app/agent-team-forge/releases/latest/download/install.sh | sh
"$HOME/.local/bin/atf" setup
```

Use the absolute second command so a missing `~/.local/bin` in PATH does not add a third required step. Setup should apply by default, offer an explicit launch-mode choice, and register installed clients. Keep `--mode headless --apply` working for unattended use. Do not combine network bootstrap and interactive setup: piped stdin makes that fragile, and mode must remain an explicit choice (AGENTS.md:39-41). No package manager integrations, updater daemon, or automatic installation of agent CLIs/Herdr in this release.

## Current fresh-user walkthrough

Read-only review of main at `61f2665`; no release exists yet (owner-provided release status). Paths below are relative to this repository unless labelled reference.

1. Install and authenticate whichever backend CLI(s) will be used. Quickstart incorrectly says to install both clients (`docs/quickstart.md:46-49`); implementation treats Claude, Codex, and Pi as optional (`ClientSetup.cs:38-44,109-124`, under `src/AgentTeamForge.Host/Features/Setup/`).
2. Obtain ATF. `docs/install.md:3-4` says “install the release bundle” but provides no download command. The prominent quickstart instead requires a clone, pinned .NET 11 SDK, compiler/linker prerequisites, build, publish, and PATH export (`docs/quickstart.md:22-39`). Native AOT release users need neither SDK nor .NET runtime; distribution native-library compatibility still needs smoke coverage.
3. With a release, download and execute `install.sh`. **Latest-release discovery, version inference, archive download, and SHA256 verification already exist** (`install.sh:94-128`). `--archive`, `--checksum`, and `--version` are not mandatory for network installation; offline archives infer version and adjacent checksum filename. Do not rebuild this functionality.
4. Make `atf` discoverable or use its absolute path. Installer writes `~/.local/bin/atf` but never changes PATH (`install.sh:29-34,134,156-161`). Its final instruction contains unquoted `headless|herdr|terminal`, which is explanatory notation, not a copy-pasteable shell command.
5. Choose a mode and run `atf setup --mode headless --apply`. This already combines state creation and registration; separate setup/apply invocations are unnecessary. Without `--apply`, setup still creates state and writes mode, then only prints Claude/Codex registration commands (`SetupCommand.cs:83-120`). Bare `setup` is rejected even with a saved mode except for special autostart handling (`SetupCommand.cs:47-65`). Herdr mode availability checks OS, not whether Herdr is installed (`SetupCommand.cs:676-683`).
6. Restart/reload existing client sessions, verify MCP connection, then use ATF. Daemon starts lazily (`docs/install.md:3-5`); setup's “Run: … start” output makes a manual start look necessary (`SetupCommand.cs:123-129`). Claude/Codex registration already uses client CLIs; Pi already installs its adapter plus bundled wake extension and merges MCP/state config (`ClientSetup.cs:34-125,128-215`). No manual MCP registration is normally required.

**Step count:** excluding backend install/login, a prepared shell needs two ATF commands today (installer + explicit setup), plus client reload. Missing PATH adds one shell action. Following the source-oriented quickstart entails five shell commands in its build block plus setup (six), plus obtaining the clone/SDK/toolchain and reloading the client. As published today the release path cannot complete: no assets exist. Main friction is discoverability, defaults, and lifecycle polish—not missing download machinery.

### What was actually checked

- Ran `sh -n install.sh` and `bash -n scripts/release-build.sh`: passed.
- Ran existing installer without arguments with HOME/XDG paths isolated under `/tmp/atf-install-plan-U4HJNi`. It exited 1 with `atf installer: latest release tag must begin with v`. This confirms poor first-release error UX, not a successful install. No backend CLI, setup, or daemon was launched; owner's client/home configuration was untouched.
- No local AOT bundle was built and no successful end-to-end installation is claimed. Existing `tests/AgentTeamForge.Tests/Features/Setup/InstallScriptTests.cs:13-71` covers fake-bundle upgrade/uninstall and checksum rejection, not real downloaded release installation.

### Reference comparison

Reference repository: `/home/mikael/code/github/agentic-coder-teams-mcp` (read-only).

- Its installation is not a one-liner: Python/uv/git prerequisites, clone, `uv sync`, then registration (`INSTALL.md:22-57`). Copy its proven absolute executable paths and Claude user-scope registration (`INSTALL.md:73-97`), not its source-install burden.
- Codex uses a manually documented TOML block (`INSTALL.md:148-180`); ATF's existing `codex mcp add` is already simpler.
- Pi needs `pi-mcp-adapter`; reference writes lead/worker MCP configuration during spawning (`README.md:144-173`). ATF already reconciles adapter, wake extension, and MCP configuration at setup time.
- Reference upgrades use `git pull`, `uv sync`, and client reload; stable launch paths avoid re-registration (`INSTALL.md:386-405`). Preserve this same stable-path principle in ATF's versioned binary install.

## Six implementation slices

### 1. Finish release bootstrap and publish the real entry point

**Problem:** Download logic exists, but there is no release to consume, absent-release errors are opaque, and the advertised one-liner is missing. Linux publication currently waits for every platform.

**Evidence:** `install.sh:103-128`; `scripts/release-build.sh:31-60`; `.github/workflows/release.yml:3-4,80-93` (tag trigger, all-platform dependency, merged upload, `gh release create`). macOS has a separate checksum filename (`scripts/release-build.sh:9-10`); Windows has `SHA256SUMS-win-x64` (`release.yml:76-78`).

**Fix:** Keep existing downloader and per-platform checksum names; resolve one tag then fetch archive and checksum from that same tag. Improve missing-release/missing-asset/network errors and supported-platform messages. Publish installer as a release asset as already packaged. Separate Linux release availability from optional tester jobs so macOS/Windows failures cannot block Linux; attach tester assets separately when ready. Tag/publish v0.0.1 only with owner authorization. Explain that hashes fetched from the same HTTPS release detect corruption, not independent publisher authenticity; a piped installer itself is trusted before archive verification. Document a pinned-tag alternative.

**Test:** Isolated HOME with deterministic curl fixtures for redirect/latest, pinned version, absent release, missing archive/checksum, bad hash, and unsupported RID. Fail before installed state changes on download/hash failure. Run a real published Linux bundle on a clean supported Linux environment without `dotnet`; install and `atf --version` must work. Reuse existing installer tests, adding focused cases rather than another release framework.

### 2. Make one setup command explicit and sufficient

**Problem:** Bare setup fails; apply semantics are surprising; mode recommendation and Herdr preflight are absent.

**Evidence:** `SetupCommand.cs:24-39,47-65,83-129,676-683`; `docs/quickstart.md:46-55`; AGENTS.md:39-41 requires explicit mode selection and forbids silent headless fallback.

**Fix:** Make `atf setup` apply by default. On first interactive setup show detected choices: recommend Herdr if its executable/preflight succeeds, otherwise recommend headless and say why. Require confirmation of that choice; absence of Herdr is not permission for a silent fallback. Noninteractive fresh setup requires `--mode`; explicit `--mode headless` is the concise unattended path. Preserve a configured mode on rerun, never switch it automatically. Retain `--apply` as compatible alias and `--check` as nonmutating validation. Print registration summary and “daemon starts on first use,” not a mandatory-looking start command. Leave autostart off by default.

**Test:** Redirected stdin never hangs; no mode + no terminal yields actionable nonzero exit. Explicit headless succeeds without Herdr; requested unavailable Herdr fails before writing mode. Interactive selection persists, rerun preserves it, and `--check` changes nothing. Existing autostart-only invocations still work.

### 3. Surface existing Claude/Codex/Pi setup, with useful failures

**Problem:** Docs imply two mandatory clients and omit automatic Pi setup. Partial registration/network failures lack useful CLI stderr. A successful setup with no installed clients can look ready to use.

**Evidence:** `docs/quickstart.md:46-49,110-119`; `ClientSetup.cs:38-44,66-81,84-124,187-214`; `SetupCommand.cs:693-725` drains and discards stderr. Stable binary registration is already implemented (`ClientSetup.cs:14-31`).

**Fix:** Keep existing reconciliation rather than introducing new config writers. Report each installed/skipped/failed client, stable executable path, state path, and client reload requirement. Explain Claude `crossSessionInbound` change and Pi adapter installation/network requirement. On failure show bounded useful CLI error and rerun instruction; preserve unrelated settings. If no clients are installed, say installation succeeded but install/login to at least one backend then rerun setup. Do not add a mandatory login probe or install all backends. Manual registration becomes troubleshooting only.

**Test:** Focused fake-runner cases for Claude-only, Codex-only, Pi-only, absent clients, adapter download failure, and partial failure followed by successful rerun. Verify no duplicate registrations/package entries and unrelated configuration remains intact. One real-client smoke per available backend in isolated test profiles; do not call these verified until run.

### 4. Document two commands and remove PATH/source-build traps

**Problem:** User-facing docs start with developer tooling; installer completion text is not safely copyable; PATH behavior is unstated.

**Evidence:** `docs/install.md:1-17`; `docs/quickstart.md:22-59`; `install.sh:29-34,160-161`; `scripts/release-build.sh:13-24` pins SDK for builders only.

**Fix:** Put the two-command path above all build instructions in install/quickstart. Print an executable, correctly quoted absolute setup command from installer. Detect missing PATH and offer optional shell-specific guidance without silently editing shell startup files; absolute setup remains sufficient. State exact initial support: Linux x64 on tested glibc distributions; no Linux arm64/musl or Intel Mac claim. Move build commands to a developer section. Include backend prerequisites, mode meaning, client reload, `atf doctor`, and links to upgrade/uninstall. No separate mandatory `start`, `init`, or manual MCP commands.

**Test:** Follow docs literally from a clean HOME with spaces and PATH lacking `.local/bin`, using a real bundle and one installed authenticated backend. Exactly two ATF commands reach registered configuration; first client use starts daemon. Check docs/installer examples agree with actual CLI. No shell rc changes without user choice.

### 5. Make upgrade repeatable and uninstall complete

**Problem:** Same-version rerun fails, final output suggests reselecting mode on every upgrade, and uninstall leaves client registrations/autostart pointing to removed binaries. Older version directories remain, but this is not a safe database rollback guarantee.

**Evidence:** `install.sh:46-49,52-90,134-161`; `InstallCommand.cs:7-33`; `ClientSetup.cs:14-31`; `docs/install.md:7-16`. Existing installer tests cover retained unowned files but not integration cleanup (`InstallScriptTests.cs:13-39`).

**Fix:** Upgrade by rerunning the same installer; explicit `--version` pins a target. Same active version becomes a verified no-op, not an error. Preserve profile/mode/state and stable MCP paths; normally no setup rerun, only client reload. Keep refusal when owned daemon cannot stop; explain that upgrade stops running work and advise waiting for jobs to finish. Before removing binaries, remove ATF-owned MCP/Pi wake entries and autostart through existing C# setup mechanisms; preserve shared adapter packages, unrelated settings, and ambiguous entries. Keep state by default; `--purge` remains explicit destructive opt-in. Document custom `--state-dir` use. Defer automatic rollback, release pruning, and new updater commands.

**Test:** Existing upgrade/mismatch tests plus same-version rerun, saved-mode retention, stable registration path, failed-stop leaves current release unchanged, and teardown of only owned client/autostart entries. Modified files and default state survive uninstall; explicit purge removes only validated chosen state. Real bundle upgrade with owned daemon stopped and client reconnected.

### 6. Bring the same path to macOS/Windows testers, after Linux

**Problem:** macOS installer exists but is unvalidated; Windows gets a zip with no installer, and `atf uninstall` assumes `sh` and bundled `install.sh`.

**Evidence:** `install.sh:21-27,138-155`; `scripts/release-build.sh:9-10,50`; `.github/workflows/release.yml:40-78`; `InstallCommand.cs:9-20`; `docs/quickstart.md:3-20`. No tracked `install.ps1` exists.

**Fix:** Validate existing macOS arm64 path rather than adding Homebrew packaging. Recommend Terminal.app or detected Herdr through explicit setup choice; keep platform-specific limitations visible. For Windows add a small user-scope `install.ps1`: latest/pinned release resolution, zip + `SHA256SUMS-win-x64` verification, versioned payload with stable apphost path, no admin requirement, clear PATH/restart guidance, and owned cleanup/upgrade behavior. Account for Windows executable locks and avoid requiring privileged symlinks; fix uninstall dispatch accordingly. Keep setup/registration logic in C#, not duplicated PowerShell. Label both platforms tester-only until native runs pass; no claim from cross-compilation alone.

**Test:** Actual macOS arm64 and Windows x64 machines: fresh install without SDK, setup, client MCP connection, one job in selected mode, upgrade, and uninstall preserving state/unrelated configuration. Verify macOS quarantine/Gatekeeper behavior and Windows file locks/PowerShell command quoting with spaced/non-ASCII home paths. Linux release remains unblocked if these runs are unavailable.

## Validation for this planning change

Only this plan file changed. Shell syntax checks above passed; `git diff --check` passed after writing. No Markdown-specific formatter/linter is configured; add one only if documentation volume warrants it. `scripts/verify.sh` is the implementation gate (format, build, tests, Linux publish/smoke), not evidence supplied by this read-only planning task.
