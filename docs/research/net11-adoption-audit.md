# .NET 11 adoption audit

Audit date: 2026-09-26. Baseline: `main` at `332c59a`. Read-only product audit; only this report is committed. Paths and line numbers below refer to that baseline unless the concurrent-main update is explicitly identified. Read `AGENTS.md` and [the earlier process research](net11-process-api.md) first. Tests were isolated C# projects under `/tmp/atf-net11-audit`; no spikes or product files were modified.

**Answer:** ATF targets .NET 11 and already uses AOT, JSON source generation and modern locking, but does not yet use the new process lifecycle APIs or C# unions in production. There are useful, narrow adoption opportunities. The best is replacing daemon-launch plumbing with explicit detach and standard-handle control. **Do not delete Linux pidfd protection:** this RC's `SafeProcessHandle` is not a pidfd. **Unions really compile in this SDK with `15.0` and `latest`, without `preview`.** Start with internal acceptance outcomes, not a wire-protocol rewrite.

## Summary

“Verified” means observed locally in the pinned compiler/reference/runtime binaries; execution scope is stated below. “Announced” means documentation/source evidence only, not a demonstrated ATF improvement.

| Feature | Available in pinned SDK? | ATF fit | Recommendation |
| --- | --- | --- | --- |
| `StartDetached`, standard file handles, `InheritedHandles` | Verified; Linux JIT/AOT detach smoke passed | `SetupCommand` shell/setsid launch; Windows daemon native launch | **Now:** Linux replacement slice with lifetime/EOF tests. Windows after actual Windows validation |
| `SafeProcessHandle.Start/Open/TryOpen`, wait/kill lifecycle | Verified; start/open/signal/wait exercised | Short owned helpers; not a replacement for orphan pinning | **No** broad backend rewrite; preserve `Pidfd.cs` |
| `Signal`, `ProcessExitStatus`, status waits | Verified; SIGTERM produces exit 143, `SIGTERM`, `Canceled=false` | Direct-child termination diagnostics | **Now** when modifying termination; process exit is not job success |
| `TryGetProcessById(int, out Process?)` | Verified | Expected missing-process lookup | Opportunistic **now**; no ownership guarantee |
| Process groups / tree kill | `CreateNewProcessGroup` verified Windows-only; `Kill(bool)` exists | Headless descendant cleanup | **No** claim of new portable process-group supervision; retain existing cleanup |
| `StartSuspended` / `Resume` | Verified surface; Windows/macOS-only; Linux throws | Bind Windows child to Job Object before execution | **After v0.0.1**, on target OS; not Linux infrastructure |
| `KillOnParentExit` | Verified surface; Linux/Windows/Android attributes | Explicit daemon-owned headless lifetime | **After v0.0.1**, only if crash policy calls for it; no blanket interactive setting |
| Run/capture/read helpers | Verified; capture exercised JIT/AOT | Bounded short probes; possibly git helper | **Now** only where bounds and cancellation survive; **no** replacement of streaming backends/Herdr bounded drains |
| `Process.Start(info, handles)` | **Absent**; negative compile confirmed | Handle whitelist | Use `info.InheritedHandles`; do not invent an overload |
| C# union declarations / closed hierarchies | Verified C# 15, no preview flag; union JIT/AOT smoke passed | `AcceptOutcome`, `JobResult`, existing evidence hierarchy | **Now:** one internal acceptance slice; broader results **after v0.0.1** |
| JSON union/closed-type support | Verified generator handles union; ambiguous object cases warn | IPC migration would require classifiers and compatibility work | **After v0.0.1**; keep current source-generated DTOs |
| `TimeProvider` | Verified, predates .NET 11 | Deterministic combined clock/timer testing | Opportunistic **now**; **no** wholesale replacement of adequate clock delegates |
| `System.Threading.Lock`, params spans, `field`, extension members | Verified, pre-C#-15 features | Lock already adopted; small local simplifications | Keep Lock; others **no** migration campaign |
| Collection arguments, extension indexers, labeled jumps | Verified under `15.0` | No demonstrated project problem | **No** adoption slice |
| Memory-safety pointer relaxations | Verified preview-only in probe | Existing native interop | **After v0.0.1**; no stability risk for syntax alone |
| Native AOT interface dispatch/size improvements; runtime async | Announced; AOT publishing itself verified | Automatic toolchain benefit, potentially async-heavy daemon | **No** speculative optimization slice; measure published ATF before performance claims |

## SDK facts and reproducible evidence

Used the absolute executable `/home/mikael/code/github/agent-team-forge/.tools/dotnet11/dotnet`, not the ambient `DOTNET_ROOT` (which points elsewhere). `--info` reports SDK `11.0.100-rc.1.26425.128`, SDK/host commit `3551975be0`, runtime `11.0.0-rc.1.26425.128`, RID `linux-x64`, MSBuild `18.11.0-1.26425.128`. Reference pack: `.tools/dotnet11/packs/Microsoft.NETCore.App.Ref/11.0.0-rc.1.26425.128/ref/net11.0/`; both Core and ASP.NET reference packs and the Linux host pack were present.

`dotnet <sdk>/Roslyn/bincore/csc.dll -langversion:?` lists `15.0 (default)`, `latest`, and `preview`. `global.json:3` pins this SDK with roll-forward disabled. `Directory.Build.props:3` targets `net11.0`, line 4 selects `latest`, line 16 enables production AOT compatibility analysis. `src/AgentTeamForge.Host/AgentTeamForge.Host.csproj:8` enables `PublishAot`.

**Important evidence trap:** the installed `System.Diagnostics.Process.xml` is incomplete for the new APIs. It documents `CreateNewProcessGroup` but omits `StartDetached`, lifecycle methods, and many others that are present in the binary. Similarly, grepping JSON XML alone misses new union support. Absence from XML is not absence from this SDK. Checked runtime public members by reflection, then compiled direct API calls against the actual reference pack. SHA-256 identities:

- `System.Diagnostics.Process.dll` reference: `10f35d4827c9359b939a64cc2d6943544c59857ca85f996effec6cb2f2546abb`.
- `System.Text.Json.dll` reference: `25ba3f4092894197b5865bba5509388c51a2c32463369e6cfa4ae6fa55f20be2`.

Probe project settings were `TargetFramework=net11.0`, `OutputType=Exe`, `ImplicitUsings=enable`, and explicit `LangVersion`. Commands: `dotnet build <project> -p:LangVersion=15.0` (also `latest` and `preview` for unions); `dotnet run --project <project>`; `dotnet publish <project> -r linux-x64 -c Release -p:PublishAot=true -o <temporary-output>`, then invoke the native executable.

### Language and JSON probes

This complete core example compiled under **all three** language settings, ran JIT, and ran Native AOT:

```csharp
Result r = new Success("yes");
Console.WriteLine(r switch { Success s => s.Value, Failure f => f.Error });
public record Success(string Value);
public record Failure(string Error);
public union Result(Success, Failure);
```

Removing the `Failure` switch arm produced **CS8509 warning**, not a build error by default. Unions provide useful exhaustiveness diagnostics, not unconditional runtime validity or automatic enforcement of every domain invariant. Do not describe the feature as unavailable, preview-flag-only, or equivalent to every F# discriminated-union facility.

Adding `[JsonSerializable(typeof(Result))] partial class Context : JsonSerializerContext` and serializing with `Context.Default.Result` succeeded in JIT and AOT, output `{"Value":"yes"}`. It also emitted **SYSLIB1227**: both record cases serialize as JSON objects and need a custom classifier for disambiguation. This was a serialization smoke, **not a successful union deserialization/round-trip claim**. Reflection also verified `JsonSourceGenerationOptionsAttribute.TypeClassifiers` and `InferClosedTypePolymorphism`. A new wire format is not justified by the existence of these features.

Separate C# 15 probes compiled and ran: `closed record` hierarchy with exhaustive switch; collection expression `[with(capacity: 4), 1, 2]`; an extension indexer; labeled `break`. The trivial loop emitted expected unreachable-code warning CS0162. Baseline probes compiled and ran `params ReadOnlySpan<int>`, a `field`-backed property, extension property, `lock(new System.Threading.Lock())`, `TimeProvider.System.GetUtcNow()`, and ordinary JSON source generation. These last features are available but are not all .NET 11 inventions: TimeProvider is .NET 8, Lock/params collections are .NET 9/C# 13, and field/extension members are C# 14.

With `AllowUnsafeBlocks=true`, `int x=1; int* p=&x;` outside an unsafe context fails under `15.0` with CS0214 and compiles under `preview`. That confirms the preview boundary for this pointer relaxation, not the whole proposed memory-safety ruleset. Do not enable preview across ATF for it. The [C# 15 reference](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15) describes the broader features; the local probes establish which tested constructs this compiler accepts.

### Process probes and exact surface

Reflection verified these new public families in the installed runtime; direct calls tested the subset marked executed:

- `SafeProcessHandle.Start(ProcessStartInfo)`, `Open(int)`, `TryOpen(int, out SafeProcessHandle?)`, `ProcessId`, `Kill()`, `Signal(PosixSignal)`, `WaitForExit()`, `TryWaitForExit(TimeSpan, out ProcessExitStatus?)`, `WaitForExitAsync(CancellationToken)`, `WaitForExitOrKillOnTimeout(TimeSpan)`, `WaitForExitOrKillOnCancellationAsync(CancellationToken)`, and `Resume()`.
- `Process.TryGetProcessById(int, out Process?)`, `Signal(PosixSignal)`, `WaitForExitStatus()`, `TryWaitForExitStatus(TimeSpan, out ProcessExitStatus?)`, `WaitForExitStatusAsync(CancellationToken)`.
- `Process.StartAndForget(ProcessStartInfo)` and filename/argument overload; `Run`/`RunAsync`, `RunAndCaptureText`/async; instance `ReadAllText`, `ReadAllBytes`, `ReadAllLines` and async counterparts. Capture returns `ProcessTextOutput` with output strings, process ID, and `ProcessExitStatus`.
- `ProcessStartInfo.StartDetached`, `StartSuspended`, `KillOnParentExit`, `InheritedHandles` (`IList<SafeHandle>`), and the three `Standard*Handle` properties (`SafeFileHandle`). `CreateNewProcessGroup` is annotated **Windows**, not a new Linux group-management API.

Executed on Linux, JIT **and Native AOT**: capture `/bin/sh` emitting separate stdout/stderr (`out/err/0`); start `/bin/sleep` through `SafeProcessHandle`, signal SIGTERM, await status (`143/SIGTERM/false`); start `/bin/true` with `StartDetached=true, InheritedHandles=[]`, await exit 0; `StartAndForget` of `/bin/true`; lookup/open the audit's own PID; attempt suspended `/bin/true`, which throws `PlatformNotSupportedException`. No unrelated process was signaled. `InheritedHandles=[]` was compile/execution-tested, **not a comprehensive descriptor-leak test**. Parent-death, tree escape, callback launch, and Windows/macOS semantics were not runtime-tested.

The deliberate unsupported suspended call produces CA1416. An initial AOT introspection harness failed with NullReferenceException when name-based property reflection encountered trimmed metadata; after removing diagnostic reflection, the direct process API executable passed. This is a probe limitation, not evidence of a Process API failure. Union AOT emitted SYSLIB1227; no claim that the whole audit is warning-free. No ATF build/test suite was run for this documentation-only change.

`Process.Start(info, Array.Empty<SafeHandle>())` fails compilation with CS1503: there is no such overload. The handle list belongs on `ProcessStartInfo`. The earlier research's `UnixProcessStartArguments.Start`/`WindowsProcessStartArguments.Start` callbacks are source-backed candidates, not locally exercised here and not recommended for ordinary ATF launches.

## Where process adoption helps—and where it weakens ATF

All source references in this section are relative to the repository root.

### Keep pidfd and orphan identity checks

`src/AgentTeamForge.Business/Features/Agents/Backends/Pidfd.cs:13` opens a Linux pidfd and line 19 signals it with a protected SafeHandle lifetime. The two generated libc syscall imports are at lines 36–40. This is only about 40 lines, with a concrete correctness benefit.

`OrphanedBackendProcess.cs:158` opens the pidfd **before** reading `/proc/PID/environ` at line 171, checks the correlation marker, then signals at line 183. If the PID is reused after pinning, the signal still targets the original kernel process reference. `src/AgentTeamForge.Host/Features/Setup/SetupCommand.cs:349` and line 367 use the same protection for daemon stop.

The [RC-tagged Unix SafeProcessHandle implementation](https://github.com/dotnet/runtime/blob/v11.0.0-rc.1.26425.128/src/libraries/System.Diagnostics.Process/src/Microsoft/Win32/SafeHandles/SafeProcessHandle.Unix.cs), fetched during this audit, uses `kill(pid, 0)` in `TryOpenCore` (line 79), creates a managed wait-state holder, and uses `Interop.Sys.Kill(ProcessId, signalNumber)` in `SignalCore` (line 120). **It does not use pidfd for these operations.** This implementation evidence is distinct from local API availability and was not syscall-traced. SafeHandle resource management does not imply Linux kernel identity pinning. `TryGetProcessById` likewise is a convenient lookup, not a safe adoption protocol. Keep persisted correlation/start-identity checks even on Windows, where the native process handle is stronger.

**Concurrent-main update:** `OwnedProcessTermination.cs` was absent at the worktree baseline, but appeared on main during the audit. At inspected main `f3e9806aa35605710fbffc592d430386b7f80768`, `src/AgentTeamForge.Business/Features/Agents/Backends/OwnedProcessTermination.cs:8` centralizes owned-child termination: `HasExited` check, `Kill(entireProcessTree: true)` at line 14, and expected exit/ESRCH/aggregate-exit exception filtering at lines 17–19. Claude/Codex/Pi now delegate to it. Keep this helper: `Process.Signal` does not provide its tree semantics, and a SafeProcessHandle swap does not pin Linux identity. The duplication-removal part of any proposed termination slice is therefore already done on newer main. Related test code is `tests/AgentTeamForge.Tests/Support/OwnedProcesses.cs:20`/`:30`, which tracks only its own started children.

On macOS, `OrphanedBackendProcess.cs:129` checks a creation token before `DarwinProcess.SignalIfSame`; `DarwinProcess.cs:153` still checks then signals by PID. New `Signal` could remove the raw kill declaration, but cannot eliminate that identity race or replace process metadata inspection. No correctness gain sufficient to justify such a swap alone.

### Headless backend launch and termination

`ClaudeCodeBackend.cs:26`, `CodexExecBackend.cs:26`, and `PiBackend.cs:27` redirect all three streams; starts are at lines 46, 45, and 46 respectively. Their run objects tee output into logs, decode backend protocol evidence, and maintain session identity. Termination calls `Kill(entireProcessTree: true)` at lines 162, 231, and 155, followed by bounded waits in disposal (Claude line 204, Codex 244, Pi 168).

New `Process.Signal`/status waits can provide structured signal-death diagnostics for a **still-owned direct Unix child**. They cannot identify a successful agent turn, replace the parser, or prove that grandchildren died. Plain wait cancellation is not process cancellation; explicit kill-on-cancellation APIs deliberately have different semantics. Do not bind accepted work to a bridge request's cancellation token. Switching from `Process` to `SafeProcessHandle` would also complicate the existing managed standard-stream access for little deletion.

`Kill(entireProcessTree: true)` is an existing tree traversal operation, not a new stable containment primitive. `CreateNewProcessGroup` is Windows-only. Neither a group ID nor `StartDetached` provides cgroup/Job Object supervision or recovery of lost pipes. `KillOnParentExit` changes crash policy; it does not replace orphan reconciliation, especially for detached/terminal-owned agents. Its reflected platform attributes include Linux/Windows/Android and exclude macOS.

### Daemon launch: strongest simplification candidate

`src/AgentTeamForge.Host/Features/Setup/SetupCommand.cs:246` first uses the installed systemd user service on Linux. Preserve that ownership path. The manual fallback at line 279 invokes `setsid sh -c` (macOS backgrounds through `sh`), sets umask, redirects `/dev/null` and daemon.log, scrubs environment, and waits for readiness at line 298.

Use `ProcessStartInfo.StartDetached` with explicit safe file handles for stdin/stdout/stderr to remove the fallback shell/setsid dependency. Preserve log append behavior, private permissions currently supplied by umask, environment scrubbing, endpoint/lock-based readiness, and concurrent lazy-start arbitration. Prefer retained `Process` until readiness is established; `StartAndForget` intentionally loses observation and is not automatically the better option. Detach smoke success is not a test of survival after MCP closure or systemd cgroup teardown.

`WindowsDaemonLauncher.cs:9` explains the present no-handle-inheritance requirement. Line 32 calls native `CreateProcess` with `inheritHandles=false`; the file manually builds environment blocks, quotes command lines, and closes native handles. `StartDetached`, explicit standard handles and `InheritedHandles=[]` could delete most of this roughly 110-line file. **An empty whitelist does not mean no standard handles are inherited.** The [tagged StartInfo remarks](https://github.com/dotnet/runtime/blob/v11.0.0-rc.1.26425.128/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessStartInfo.cs) restrict list entries to file/pipe handles and warn that concurrent starts sharing the same listed handle are unsupported because inheritance flags are temporarily changed. Test MCP pipe EOF and credential/pipe leaks on Windows before replacement; do not assume `CreateNoWindow` alone detaches or isolates handles.

`src/AgentTeamForge.Business/Features/Agents/Terminals/WindowsTabProcess.cs:19` requests a real new console; lines 47–68 manage a kill-on-close Job Object and assign by PID. A detached process is not a visible interactive console. Suspended launch/resume may close the start-before-assignment window for directly launched wrappers, but cannot transparently control children actually created by Windows Terminal. Keep this separate from daemon detachment. Windows/macOS were not tested by this audit.

### Herdr and git helpers

`HerdrProcessRunner.cs:36` has a whole-operation deadline covering both exit and stream drains, and line 159 bounds retained bytes while continuing to drain. This protects against descendants holding pipe writers open. `RunAndCaptureText` accumulates output and supplies no equivalent byte cap: replacing this code is **not worth it**. Its own `StartDetachedAsync` at line 61 waits for a launcher that detaches its payload; it is not a wrapper around the .NET detach flag. Preserve launcher exit validation and terminal-provider semantics.

`src/AgentTeamForge.Business/Features/Jobs/JobWorktree.cs:66` runs git, starts a concurrent unbounded stdout read at line 92, waits with timeout, tree-kills on timeout, then synchronously awaits the read at line 99. **Observed structural risk:** a hook's descendant retaining stdout after git exits can keep that final read pending beyond the intended timeout. A new capture API is useful only if the replacement enforces the deadline across drains and preserves descendant cleanup. Otherwise use the proven bounded-drain approach already present in Herdr. This is a correctness slice, not a general process abstraction project.

## Internal outcomes, JSON and other language features

`src/AgentTeamForge.DAL/Features/Jobs/JobRecords.cs:81` is an enum plus nullable job (`AcceptOutcome`); `src/AgentTeamForge.Business/Features/Jobs/AcceptJob.cs:153`–156 switches on the tag and dereferences `outcome.Job!`. A union of `Accepted(JobRecord)`, `Existing(JobRecord)`, and explicit rejection cases would make missing success payloads harder to represent and expose unhandled cases through CS8509. This is the clearest small migration. It fixes a representable-invalid-state class, **not a demonstrated production incident**. Ensure exhaustive-switch warnings are enforced for migrated code; a catch-all arm defeats the benefit. Default/null values and domain validation still deserve attention.

`JobRecords.cs:88` has nullable job plus two booleans in `CancelOutcome`; `src/AgentTeamForge.Business/Features/Jobs/JobContracts.cs:94` has `JobResult(Job?, Outcome?, Error?)` with factories but a public constructor; `ExternalTeam.cs:10` has many optional payloads and treats no error as success. These are plausible later union candidates. Do not replace every string outcome or durable SQL status with a new type system. Delivery/acknowledgement semantics still require persistence and correlation; a union cannot establish delivery.

`src/AgentTeamForge.Business/Features/Agents/Backends/IJobBackend.cs:16` already models evidence as an abstract record with sealed nested cases. A C# 15 **closed hierarchy** can be a smaller change than wrapping all those cases in another union. No need to rewrite a working hierarchy simply to use the keyword.

`src/AgentTeamForge.Host/Transport/IpcMessages.cs:61` is a broad response envelope with `Ok`, error/outcome, and optional payloads. It can represent contradictory combinations, but its JSON shape is part of the client contract. Keep the envelope and map stronger internal outcomes into it. The JSON source-generated context is already at lines 124–134; Claude's result context is at `ClaudeCodeBackend.cs:362`, and other contexts exist for wake/setup/tier data. New union support does not remove a demonstrated AOT serialization gap. Classifier behavior and old-client round trips must be tested before changing IPC.

`System.Threading.Lock` is already used, for example `src/AgentTeamForge.Business/Features/Jobs/AdmissionGate.cs:12`, `TierMap.cs:17`, and `HerdrInteractiveBackend.cs:112`. Keep it. `ExternalTeam.cs:17`–19 already injects a clock delegate; replacing it with TimeProvider alone adds no value. TimeProvider helps when one test must advance both time and delays (for example dispatch polling at `DispatchJob.cs:222`), but there is no reason for a clock-only refactor. Params spans could save a tiny argument-array allocation in `JobWorktree.Git`, dwarfed by process startup. Field/extension syntax offers no measured bug or substantial deletion here. Not worth dedicated slices.

## AOT and announced benefits

The owner's [RC1 overview](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview) lists process, JSON, C# and runtime improvements. It is a discovery index, not evidence of a particular binary or platform. [Runtime notes](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime) announce Native AOT interface-dispatch improvements and runtime-async changes. Treat throughput/size benefits as **announced, unmeasured for ATF**. JIT optimizations are not automatically Native AOT gains; runtime-async support/performance under ATF's publishing configuration was not established here. Existing SDK/AOT compilation can deliver automatic benefits without source rewrites.

Small Native AOT probes prove these tested constructs can publish and run on this Linux machine. They do not prove ATF's MCP registration, SQLite native dependencies, every serializer, startup time, binary size, or Windows/macOS compatibility. For any implementation slice, run the relevant focused tests and published ATF binary; compare size/startup only if optimizing those properties. No preview switch or new reflection-heavy abstraction is justified before v0.0.1.

## Prioritized slices (maximum six)

1. **Now — Linux daemon fallback launch.** `SetupCommand.cs` (manual fallback only). Replace shell/setsid with `StartDetached` and explicit file handles; delete roughly 15–25 lines of shell/argument plumbing while preserving secure file creation, append semantics and readiness. Test a published daemon started by a short-lived bridge: bridge EOF, daemon survival, logs, paths with spaces, concurrent starts. Keep systemd path intact.
2. **Now — git whole-operation timeout.** `JobWorktree.cs`, reusing the approach in `HerdrProcessRunner.cs` where useful. Delete the unbounded read/final wait sequence; a framework capture call is conditional on proven drain and cleanup semantics. Test a test-owned git hook descendant holding stdout open, timeout/cancellation, and large output. No custom P/Invoke deletion promised.
3. **Now — internal acceptance outcome union.** `JobRecords.cs`, acceptance store producers, `AcceptJob.cs`. Delete `AcceptKind`/nullable-success combinations and success-arm null-forgiving operators; retain external JSON/SQL shape. Test accepted/existing/rejected payloads and ensure a missing case fails the intended build check. Publish AOT; no serializer migration needed.
4. **After v0.0.1/on Windows — managed daemon handle control.** `WindowsDaemonLauncher.cs`, `SetupCommand.cs`. Potentially delete most native CreateProcess structures/imports, quoting and environment-buffer code. Validate published Windows child survives bridge exit without holding MCP pipes or unrelated handles; check concurrent starts and standard-handle behavior. Do not claim this passed on Linux.
5. **After v0.0.1 — direct-child lifecycle consolidation only if useful.** Three backend termination/disposal methods; optional structured exit diagnostics. The concurrent-main `OwnedProcessTermination.cs` already removes duplicate tree-kill code; do not redo that work. Remove further direct-child signal/wait plumbing only where semantics match; retain tree cleanup, stream parsers, pidfd and reconciliation. Test signal death versus authoritative result, cancellation, reaping and descendants. Consider parent-death/suspended behavior separately per OS; do not introduce an inheritance hierarchy for the helper.
6. **After v0.0.1 — additional internal outcomes/closed evidence, if touched.** `JobContracts.cs`, `ExternalTeam.cs`, `IJobBackend.cs`, adapters to `IpcMessages.cs`. Remove invalid nullable/boolean combinations rather than redesigning IPC. Test old JSON compatibility and all success/error cases in published AOT. No wholesale union or TimeProvider migration.

The deliberate non-slice is deleting `Pidfd.cs`: the pinned SDK supplies no equivalent identity-safe Linux signaling primitive. Keep that small interop layer.
