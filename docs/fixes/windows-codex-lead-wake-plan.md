# Fix plan: Windows Codex lead native wake (`register_codex_wake`)

Status: plan only. Claude wrote the plan; Codex implements it. No runtime code has been changed.
Observed on: installed release 0.0.9, Windows 11 x64.

## Symptom

- The MCP tools work. Codex and Claude jobs complete, and Codex `follow_up` completes.
- `register_codex_wake(thread_id=<real CODEX_THREAD_ID>)` returns `invalid_request`.
- `wake_status` then reports `registered:false usable:false`, so the Codex lead gets no native notice when a job finishes.

## Findings (from the code at `main` 1186681)

### F1: root cause, the Linux-only host walker in `register_codex_wake`

`src/AgentTeamForge.Host/Features/Jobs/JobsMcpBridge.cs:311-324`:

```csharp
var host = HostSessionWake.NearestHost();                  // Linux /proc walker
var home = host?.Kind == "codex" ? HostSessionWake.CodexHome(host.Value.Pid) : null;
var target = home is null ? null : HostSessionWake.ForCodexThread(String(args, "thread_id"), home);
response = target is null ? new IpcResponse(false, JobErrors.InvalidRequest) : ...
```

`HostSessionWake.NearestHost(int? start = null, string procRoot = "/proc")` (`HostSessionWake.cs:134`) reads
`/proc/<pid>/status|cmdline|comm`. On Windows, `File.ReadAllLines("/proc/<pid>/status")` throws
`DirectoryNotFoundException`. That is an `IOException`, so the walker hits `break` and returns `null`. After that,
`home` is `null` and `target` is `null`, so the call always returns `invalid_request`. The thread ID is never checked.
**macOS has the same bug.**

Every other caller already chooses the walker by platform:

- `HostSessionWake.Resolve` (`HostSessionWake.cs:13-14`)
- `ClaudeWakeRelay.RunAsync` (`ClaudeWakeRelay.cs:23-24` and `:34-35`)

These callers use `OperatingSystem.IsLinux() ? NearestHost() : IsWindows() ? WindowsHostAncestry.NearestHost() : MacHostAncestry.NearestHost()`.
The platform ternary appears in three places. `register_codex_wake` is the one caller that skips it. No test calls
the `register_codex_wake` branch, so the gap went unnoticed.

The automatic path in `Resolve` does not cover the gap. `Resolve` wants `CODEX_THREAD_ID` in the bridge's own
environment, and the manual tool exists because Codex does not always pass that variable to MCP servers
(see the tool description and ADR 0005).

### F2: `CodexHome(pid)` on non-Linux ignores the host and differs from `CodexPaths`

On Linux, `HostSessionWake.CodexHome` (`HostSessionWake.cs:109`) reads `CODEX_HOME`/`HOME` from
`/proc/<codexpid>/environ`. That is a deliberate trust decision: the home comes from the host process and never from
a model-supplied argument. On other platforms it falls back to the bridge's own `CODEX_HOME`, or else to
`UserProfile\.codex`. It does not normalize the path or check `HOME` the way `CodexPaths.Home` does. `CodexPaths.Home`
is what the daemon (`DispatchJob.cs:45`), `WtTabControl` and the transcript reader use.

- Default profile (`%USERPROFILE%\.codex`): works once F1 is fixed.
- Custom `CODEX_HOME` on Windows: works only if Codex passes `CODEX_HOME` to the MCP server. Codex starts MCP servers
  with a filtered environment, so a custom home usually falls back to the default. Verification then fails and the
  call returns `invalid_request`. There is no cheap, safe Windows equivalent of `/proc/<pid>/environ`: reading another
  process's PEB needs `NtQueryInformationProcess` plus `ReadProcessMemory`. That is out of scope. The limit will be
  documented, with the workaround: forward `CODEX_HOME` to the `agentteamforge` MCP server through Codex `config.toml`
  (`env_vars = ["CODEX_HOME"]` or `env = { CODEX_HOME = "..." }`).

### F3: Windows gap in the rollout containment check of `VerifyCodexThread`

`CodexQueueWake.VerifyCodexThread` (`src/AgentTeamForge.Business/Features/Wake/CodexQueueWake.cs:25`) accepts a
`state_5.sqlite` row when `Path.GetRelativePath(sessions, rollout)` is not `..` and does not start with `..\`. On
Windows, `GetRelativePath` returns the **target path unchanged** when the two paths have different roots. A
`rollout_path` such as `D:\x\rollout.jsonl`, or a UNC path, would pass the check. On Linux every path shares `/`, so
the problem cannot happen there. The data comes from the user's own Codex database, so the risk is low. It is still
the check this fix relies on, so we harden it: also reject a rooted `relative` (`Path.IsPathRooted(relative)`).

### F4: Windows queue transport looks right, but no runtime test has run it

`CodexQueueWake.SubmitAsync` resolves the native `codex.exe` through `WtTabControl.WindowsAgentBinary("codex")`
(npm vendor layout, x64/arm64). It avoids `codex.cmd` where it can, so the notice text does not pass through `cmd.exe`
quoting. It sets `CODEX_HOME=home`, strips `AGENT_*`/Claude channel variables, and requires a
`Queued message <id> for thread <thread>` receipt. We found no Windows-specific defect here. It has never run on
Windows in ATF (`docs/platform-status.md:25`: "Native wake: Codex lead | Untested in ATF"). The job and `follow_up`
successes do not prove this path, because they do not queue into the lead's own thread. The smoke test below covers it.

### F5: host identity and security constraints the fix must keep

- `thread_id` comes from the model. It is accepted only as a canonical lowercase `D`-format GUID, and only if
  `VerifyCodexThread` finds it under the trusted home (`ForCodexThread`).
- The home must not come from a tool argument. Keep `register_codex_wake` without a `codex_home` parameter.
  (`external_set_wake` accepts `codex_home`, but that is the external-member flow and is out of scope. It is noted here
  only so no one "aligns" the two.)
- A registration needs a real Codex ancestor. The strict `host?.Kind == "codex"` rule stays on every platform. Do not
  copy `Resolve`'s looser `host is null && !OperatingSystem.IsLinux()` rule into the explicit tool. On Windows,
  `WindowsHostAncestry.NearestHost()` uses a Toolhelp snapshot, which gives a trustworthy PID/parent chain. CIM
  command-line lookups run only for `node.exe` rows. Native `codex.exe` (npm vendor binary, and the Desktop
  app-server) matches by image name.

### F6: minor diagnostics issue

All three failure causes return the same `invalid_request`: no Codex ancestor, no trusted home, or a thread that
fails verification. Keep the public error code so the contract does not change. Add one stderr line in the bridge
that names the failed stage, so that a failure like this can be diagnosed from the MCP log.

## Fix (minimal, cross-platform)

1. **One host resolver.** In `HostSessionWake`, add:

   ```csharp
   /// <summary>Nearest Claude, Codex or Pi ancestor using the platform's trusted process table.</summary>
   internal static (int Pid, string Kind)? CurrentHost() =>
       OperatingSystem.IsLinux() ? NearestHost()
       : OperatingSystem.IsWindows() ? WindowsHostAncestry.NearestHost() : MacHostAncestry.NearestHost();
   ```

   Replace the three existing ternaries with it: `Resolve`, and the two in `ClaudeWakeRelay`. In
   `JobsMcpBridge.cs:313`, replace `HostSessionWake.NearestHost()` with `HostSessionWake.CurrentHost()`. This one line
   is the actual bug fix. The rest keeps the bug from coming back.

2. **Pull the tool's decision out into a testable function.** In `HostSessionWake`, add:

   ```csharp
   internal static IpcRequest? ForCodexLead(string? thread, (int Pid, string Kind)? host,
       Func<int, string?> codexHome, Action<string>? reject = null)
   ```

   It returns `null` when `host?.Kind != "codex"` (calling `reject("no Codex host ancestor")`), when the home is
   `null` (`"no trusted Codex home"`), or when `ForCodexThread` fails (`"thread not verified under <home>"`).
   Otherwise it returns the `ForCodexThread` request. The bridge calls
   `ForCodexLead(String(args,"thread_id"), CurrentHost(), pid => CodexHome(pid), msg => Console.Error.WriteLine("[atf-bridge] register_codex_wake: " + msg))`.
   Do not log the thread ID or any secret. The home path is fine to log.

3. **Match `CodexPaths` for the non-Linux home.** In `CodexHome`'s non-Linux branch, return
   `CodexPaths.Home(Environment.GetEnvironmentVariable, Environment.CurrentDirectory)`. The daemon uses the same
   function, so the bridge and the daemon compute the same home from the same environment. Keep the Linux `/proc`
   environ branch unchanged. Update the XML doc comment to say that off Linux the home is the bridge's inherited
   environment, because Codex's own environment cannot be read.

4. **Harden containment (F3).** In `VerifyCodexThread`, also require `!Path.IsPathRooted(relative)`, both in the
   sqlite branch and anywhere else the same pattern appears. `grep -rn "GetRelativePath" src` and apply the same
   guard wherever the code checks that a path lies inside a directory.

Out of scope:

- Reading another process's environment on Windows or macOS.
- Changing `Resolve`'s automatic non-Linux fallback.
- Adding new error codes.
- Changing `external_set_wake`.

## Regression tests (xunit, `tests/AgentTeamForge.Tests/Features/Wake/`)

Each test targets the defect, not just coverage.

1. `WakeTests.Codex_lead_registration_uses_the_resolved_host` (all platforms, no `/proc`): create a temp home with
   `sessions/rollout-...-<thread>.jsonl`.
   - `ForCodexLead(thread, (42,"codex"), _ => home)` returns a request with `WakeKey == "codex:"+thread` and `WakeHome == home`.
   - A `(42,"claude")` host returns `null`.
   - A `null` host returns `null`.
   - A `null` home returns `null`.
   - An unverified GUID returns `null`.
   - `reject` receives the three distinct stage messages, and none of them contains the thread ID.
2. `WakeTests.Current_host_does_not_use_proc_off_linux`: on non-Linux, assert
   `HostSessionWake.NearestHost(procRoot: <nonexistent>)` returns `null`. This documents the trap. Then assert that
   `CurrentHost()` returns without throwing (under `dotnet test` it will usually be `null` or a real ancestor; either is
   fine, it must not throw). Add a source-level guard: scan `src/**/*.cs` for `HostSessionWake.NearestHost(` outside
   `HostSessionWake.cs` and assert there are none. This is the test that would have failed before the fix. It is
   cheap, and it stops a fourth caller from bypassing the dispatch.
3. `WindowsHostAncestryTests.Resolves_native_codex_exe_and_npm_codex_node_shim`: add rows for
   - `atf.exe → cmd.exe → codex.exe` returns `(pid,"codex")`
   - `atf.exe → node.exe "…\node_modules\@openai\codex\bin\codex.js"` returns `"codex"`
   - a `Codex.exe` image with mixed case returns `"codex"`

   These are the Windows lead chains the fix depends on.
4. `WakeTests.Codex_home_off_linux_matches_daemon_home` (non-Linux only): assert that
   `CodexHome(anyPid) == CodexPaths.Home(Environment.GetEnvironmentVariable, Environment.CurrentDirectory)`.
5. `WakeTests.Codex_thread_verification_rejects_rollout_on_another_root` (Windows only, early return elsewhere, as in
   the existing tests): create `state_5.sqlite` with
   `CREATE TABLE threads(id TEXT, rollout_path TEXT, archived INTEGER)`, and insert the thread with
   `rollout_path = "Z:\\elsewhere\\rollout.jsonl"`. There must be no matching file under `sessions`.
   `VerifyCodexThread` must return `false`. Add a positive twin: the same row with a rollout under `<home>\sessions\2026\…`
   returns `true`.
6. Keep the existing `Explicit_codex_registration_requires_a_verified_thread` and
   `Codex_home_comes_from_the_host_process_environment` tests unchanged. They must still pass.

Run `dotnet test` on Windows (the smoke machine) and on Linux, or in CI if that is how the repo runs. Both must pass
with no newly skipped tests.

## Release binary and runtime smoke test (Windows 11, real Codex lead)

Use a locally published build of the release artifact (same `dotnet publish` profile as the 0.0.9 release). Put it in
place of the installed `atf.exe`, or point the Codex MCP config at the new binary. Restart Codex so it starts a fresh
MCP bridge.

1. In the Codex lead: read `CODEX_THREAD_ID` with a shell tool, then call `register_codex_wake(thread_id=...)`.
   Expect `ok` with a `wake_generation`.
2. `wake_status` shows `registered:true usable:true`, kind `codex`, and home `%USERPROFILE%\.codex`.
3. Negative checks in the same lead:
   - A random GUID returns `invalid_request`, and the MCP stderr log shows `thread not verified`.
   - An uppercase copy of the real ID returns `invalid_request`.
4. Submit a short Codex job and a short Claude job, for example "reply OK". Leave the lead **idle** and do not poll. A
   native notice naming `list_jobs`/`get_job` must arrive in the lead thread for each job, and `get_job.delivery` shows
   a queued submission ID. Then do the same while the lead is busy in a long turn: the notice must run after that turn.
5. Restart: close Codex, reopen the same thread (`codex resume <id>`), and call `session_info`/`resume_session`.
   Register again, submit a job, and confirm the notice arrives.
6. Custom home (documents F2): start Codex with `CODEX_HOME=C:\tmp\codexhome`, without forwarding. Expect
   `invalid_request` with `thread not verified under C:\Users\…\.codex` on stderr. Then add
   `env_vars = ["CODEX_HOME"]` to the `agentteamforge` server in `C:\tmp\codexhome\config.toml`, restart, and confirm
   the registration succeeds.
7. Optional: macOS, steps 1-4 if a Mac is available. Otherwise leave macOS marked untested.

Record the Codex version, the ATF commit, the timestamps and the outcome for each step in `docs/platform-status.md`,
in a new dated section.

## Documentation corrections

- `docs/platform-status.md`: change the "Native wake: Codex lead" Windows cell from "Untested in ATF" to the smoke
  result, and add the dated section. Remove "Windows" from any line that says Codex wake is verified only on Linux,
  once the smoke test passes.
- `docs/usage.md` (Native wake → Codex): add one sentence saying that a custom `CODEX_HOME` must be forwarded to the
  `agentteamforge` MCP server through `env_vars`, or registration cannot find the thread. Otherwise the defaults work.
- `docs/adr/0005-native-wake.md` (Consequences): update "Codex and Pi wake are cross-platform in code but only
  verified on Linux" to the new state. Note that off Linux the Codex home comes from the bridge's inherited
  environment, because the Codex process environment cannot be read.
- `.claude/skills/agent-orchestration/SKILL.md` needs no change. Its instructions (`register_codex_wake` once, before
  submitting) stay correct.
- Changelog or release notes, if the repo keeps them: "Fix `register_codex_wake` returning `invalid_request` on
  Windows/macOS."

## Commit shape for the implementer

One commit, "Fix register_codex_wake host resolution on Windows and macOS", containing:

- `HostSessionWake.cs`: `CurrentHost`, `ForCodexLead`, and the `CodexHome` change
- `ClaudeWakeRelay.cs`
- `JobsMcpBridge.cs`
- `CodexQueueWake.cs`: the F3 guard
- the tests

A second commit contains the docs, after the smoke test passes.
