using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.FakeBackend;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Features.WebConsole;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Hosting;

/// <summary>
/// Daemon composition and lifetime. Order: private dir → kernel lock → DB
/// (schema check) → startup recovery → bind socket → serve + dispatch.
/// Only this role composes DAL.
/// </summary>
public static class DaemonCommand
{
    public static async Task<int> RunAsync(StateDirectory state, string? crashAt, string? failAt)
    {
        if (!OperatingSystem.IsWindows())
        {
            // The old shell launcher set this before exec; keep private defaults for
            // daemon-created files even when the invoking client has a loose umask.
            _ = Native.umask(0x3F); // 077
        }
        var logPath = Environment.GetEnvironmentVariable("ATF_DAEMON_LOG") ?? Path.Combine(state.Path, "daemon.log");
        var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
        Console.SetError(new DaemonLogWriter(Console.Error, log));
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => Log($"fatal: unhandled exception: {eventArgs.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => Log($"error: unobserved task exception: {eventArgs.Exception}");
        Log($"starting pid={Environment.ProcessId}");
        var profile = ProfileFile.Load(state);
        var launchMode = SetupCommand.ConfiguredMode(state);
        if (launchMode is "herdr" or "terminal" or "wt" && !profile.RealAgents)
        {
            Log($"error: {launchMode} mode requires an agents profile");
            return 78;
        }
        if (launchMode == "wt" && !OperatingSystem.IsWindows() || launchMode == "terminal" && !OperatingSystem.IsMacOS()
            || launchMode == "herdr" && !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            Log($"error: {launchMode} mode is unavailable on this platform");
            return 64;
        }
        if ((crashAt is not null || failAt is not null) && !profile.TestProfile)
        {
            Log("test controls require an explicit test profile");
            return 64;
        }

        // Retry briefly: a client's momentary liveness probe must not make a starting daemon give up.
        var daemonLock = DaemonLock.TryAcquire(state.LockFile);
        for (var attempt = 0; daemonLock is null && attempt < 20; attempt++)
        {
            await Task.Delay(25);
            daemonLock = DaemonLock.TryAcquire(state.LockFile);
        }
        using var ownedLock = daemonLock;
        if (daemonLock is null)
        {
            // Another live daemon owns the endpoint; touch nothing.
            Log("error: daemon_already_running");
            return 75;
        }

        daemonLock.WriteOwnerPid();

        // Pin a relative CODEX_HOME to this daemon's startup directory before any
        // terminal or transcript reader captures its environment.
        Environment.SetEnvironmentVariable("CODEX_HOME", CodexPaths.LaunchHome(Environment.GetEnvironmentVariable, Environment.CurrentDirectory));

        var limits = profile.Limits;
        var checkpoints = new DurabilityCheckpoints(point =>
        {
            if (point == crashAt)
            {
                Log($"test crash at {point}");
                Process.GetCurrentProcess().Kill();
            }

            if (point == failAt)
            {
                throw new InjectedFailureException(point);
            }
        });

        JobDatabase database;
        try
        {
            StartupBackup.Run(state, limits.BusyTimeout, Log);
            database = JobDatabase.Open(state.Database, limits.BusyTimeout);
        }
        catch (StorageException ex)
        {
            Log($"error: storage_{ex.Failure}");
            return 70;
        }

        var store = new JobStore(database, checkpoints);
        var jobLogs = new JobLogs(state.Path, Log, launchMode is "herdr" or "wt");
        var externalMembers = new ExternalMemberStore(database);
        var prune = new PruneJob(new PruneJobs(database), state.Path, externalMembers);
        var wakeStore = new WakeStore(database);
        void RecoverHerdr()
        {
            if (!Directory.Exists(Path.Combine(state.Path, "herdr")))
            {
                return;
            }
            HerdrOwnedSessions.Recover(state.Path, store.FenceSession, Log);
        }
        var quarantined = new RecoverOnStartup(store, () =>
        {
            RecoverHerdr();
            if (OperatingSystem.IsMacOS())
            {
                MacInteractiveBackend.Recover(state.Path, Log);
            }
        }).Execute();
        Log($"recovery: quarantined {quarantined.Count} uncertain attempt(s)");
        var backendEnv = new Dictionary<string, string>();
        if (profile.TestProfile)
        {
            backendEnv[FakeBackendCommand.BarrierDirVariable] = state.BarrierDir;
        }

        var backends = BackendCatalog.Create(
            new FakeProcessBackend(Environment.ProcessPath!, ["fake-backend"], backendEnv, limits), profile.RealAgents && launchMode is not ("herdr" or "terminal" or "wt"));
        if (profile.RealAgents && launchMode is "herdr" or "terminal" or "wt")
        {
            backends.Register(BackendCatalog.Cursor, () => new HeadlessOnlyBackend(BackendCatalog.Cursor));
            backends.Register(BackendCatalog.Droid, () => new HeadlessOnlyBackend(BackendCatalog.Droid));
        }
        var interactiveBackends = new List<IInteractiveSessionStop>();
        HerdrTerminal? herdrTerminal = null;
        if (launchMode == "herdr")
        {
            var seed = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.Ordinal);
            herdrTerminal = new HerdrTerminal(new HerdrTerminalOptions
            {
                Environment = seed,
                SessionEnvironment = OperatingSystem.IsLinux() ? SystemdUser.ReadSessionEnvironment : null,
            });
            HerdrInteractiveBackend Interactive(InteractiveAgentKind kind) => new(herdrTerminal, kind, state.Path);
            var claude = Interactive(InteractiveAgentKind.Claude);
            var codex = Interactive(InteractiveAgentKind.Codex);
            var pi = Interactive(InteractiveAgentKind.Pi);
            interactiveBackends.AddRange([claude, codex, pi]);
            backends.Register(BackendCatalog.Claude, () => claude);
            backends.Register(BackendCatalog.Codex, () => codex);
            backends.Register(BackendCatalog.Pi, () => pi);
        }
        if (launchMode == "wt")
        {
            var claude = new WtInteractiveBackend(InteractiveAgentKind.Claude, state.Path);
            var codex = new WtInteractiveBackend(InteractiveAgentKind.Codex, state.Path);
            var pi = new WtInteractiveBackend(InteractiveAgentKind.Pi, state.Path);
            interactiveBackends.AddRange([claude, codex, pi]);
            backends.Register(BackendCatalog.Claude, () => claude);
            backends.Register(BackendCatalog.Codex, () => codex);
            backends.Register(BackendCatalog.Pi, () => pi);
        }
        if (launchMode == "terminal")
        {
            var settings = SetupCommand.ConfiguredTerminal(state)!;
            MacInteractiveBackend Interactive(InteractiveAgentKind kind) => new(kind, state.Path,
                settings.TerminalProvider!, settings.KittyAddress, settings.KittyBinary);
            var claude = Interactive(InteractiveAgentKind.Claude);
            var codex = Interactive(InteractiveAgentKind.Codex);
            var pi = Interactive(InteractiveAgentKind.Pi);
            backends.Register(BackendCatalog.Claude, () => claude);
            backends.Register(BackendCatalog.Codex, () => codex);
            backends.Register(BackendCatalog.Pi, () => pi);
        }
        Log($"backends: {string.Join(',', backends.Names)}");
        var admission = new AdmissionGate();
        var externalTeam = new ExternalTeam(externalMembers, wakeStore);
        var childContext = new ManagedChildContext(store, externalTeam, state.Path, Environment.ProcessPath!);
        using var dispatcher = new DispatchJob(store, backends, limits, checkpoints, admission, Log, jobLogs, childContext);
        dispatcher.RestoreAfterRestart(store.RestartCandidates());
        var modelDiscovery = new BackendModelDiscovery();
        var tierMap = new TierMap(state.Path, modelDiscovery.CachedModels, Log);
        var herdrPlacement = herdrTerminal is null ? null : new HerdrPlacement(state.Path, Log);
        Func<string, string?>? checkHerdrSession = herdrTerminal is null ? null : herdrTerminal.CheckExistingSession;
        var accept = new AcceptJob(store, profile.Bound, limits, profile.TestProfile, admission, backends.Names, modelDiscovery.GetModels, tierMap,
            herdrPlacement, checkHerdrSession, launchMode);
        // Remote claims have their own lead identity and cannot borrow the local MCP lead.
        var connectorAccept = new AcceptJob(store, new BoundPrincipal("prfactory", "connector", "connector-lead"),
            limits, profile.TestProfile, admission, backends.Names, modelDiscovery.GetModels, tierMap, herdrPlacement, checkHerdrSession, launchMode);
        var connectorTeams = new PRFactoryTeamStore(database);
        var connectorSessions = new AgentTeamForge.DAL.Features.Sessions.LeadSessionStore(database);
        var connectorPrincipal = new BoundPrincipal("prfactory", "connector", "connector-lead");
        var authorityRows = new PRFactoryAuthorityStore(database);
        var humanWaits = new HumanWaitStore(database);
        var claudeMailbox = new ClaudeWakeMailbox();
        dispatcher.ClaudeBridgeReady = claudeMailbox.HasRecentRelay;
        var worktreeCleanup = new WorktreeCleanup(store, backends);
        var interactiveLaunch = launchMode is "herdr" or "terminal" or "wt";
        var endpoint = new JobsEndpoint(accept, new GetJob(store, profile.Bound, interactiveLaunch), new FollowUpJob(store, profile.Bound, accept, dispatcher.InterruptRunning, reconcileIdleInteractive: dispatcher.ReconcileIdleInteractive, hasIdleInteractive: dispatcher.HasIdleInteractive, settleCompletedInteractive: dispatcher.SettleCompletedInteractive),
            new ListJobs(store, profile.Bound, jobLogs, interactiveLaunch),
            new StopJob(store, profile.Bound, dispatcher.CancelRunning, dispatcher.CloseUnclaimedFollowUp, dispatcher.StopReconciled, dispatcher.ForgetReconciledOwnership, dispatcher.InterruptRunning, dispatcher.ReleaseNative), checkpoints, dispatcher.Signal, wakeStore, prune, jobLogs, store,
            new AgentTeamForge.DAL.Features.Sessions.LeadSessionStore(database), externalTeam, new StopAgent(store, profile.Bound, backends, dispatcher.SettleCompletedInteractive), backends.Names, tierMap, modelDiscovery, herdrPlacement, claudeMailbox, launchMode,
            (token, _, _) => PRFactoryInteraction.RequestFromManagedChild(externalTeam.ManagedChildName(token)),
            externalMembers, new GetJob(store, connectorPrincipal), dispatcher.TakeNativeClaude,
            new RemoveWorktree(store, profile.Bound, worktreeCleanup), worktreeCleanup);

        var credential = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(StateDirectory.ReadPrivateFile(state.CredentialFile)).Trim());
        using var server = new IpcServer(state.Socket, credential, profile.Bound, limits, endpoint.Handle, Log, endpoint.AfterReply,
            request => request.Op is IpcProtocol.JobSubmit or IpcProtocol.JobFollowUp or IpcProtocol.JobStop ? dispatcher.PauseClaims() : null);

        using var lifetime = new CancellationTokenSource();
        using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; Log("stopping: SIGTERM"); lifetime.Cancel(); });
        using var sigint = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; Log("stopping: SIGINT"); lifetime.Cancel(); });

        using var listener = OperatingSystem.IsWindows() ? null : server.Bind();
        var serving = OperatingSystem.IsWindows() ? server.ServeWindowsAsync(lifetime.Token) : server.ServeAsync(listener!, lifetime.Token);
        WebConsoleServer? webConsole = null;
        // Test daemons must never take the operator's console port.
        var webPort = profile.TestProfile && SetupCommand.ConfiguredMode(state) is null ? 0 : SetupCommand.ConfiguredWebPort(state);
        try
        {
            WebConsoleToken.Ensure(state);
            var webClient = new IpcClient(state, limits, TimeSpan.FromSeconds(15));
            webConsole = await WebConsoleServer.StartAsync(webPort, () => WebConsoleToken.Read(state), webClient.SendAsync);
            Log($"web console listening on {webConsole.Url}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"web console unavailable on 127.0.0.1:{webPort}: {ex.GetType().Name}: {ex.Message}");
        }
        Log($"ready pid={Environment.ProcessId}");
        // Recovery may have queued connector turns before the first heartbeat. Install a
        // fail-closed gate before starting dispatch; ordinary jobs remain independent.
        dispatcher.LaunchGate = id => store.GetJob(id)?.Principal != connectorPrincipal.Principal;
        var dispatching = dispatcher.RunAsync(lifetime.Token);
        var waking = new WakeCoordinator(wakeStore, new NativeWakePoster(state.Path, claudeMailbox), Log).RunAsync(lifetime.Token);
        var restoreSweep = herdrTerminal is null ? Task.CompletedTask : RunRestoreSweepAsync(herdrTerminal, state.Path, lifetime.Token);
        var pruning = profile.AutoPrune ? RunPruneAsync(prune, worktreeCleanup, profile.PruneOlderThanDays, lifetime.Token) : Task.CompletedTask;
        var connectorStop = new StopJob(store, connectorPrincipal,
            dispatcher.CancelRunning, dispatcher.CloseUnclaimedFollowUp, dispatcher.StopReconciled, dispatcher.ForgetReconciledOwnership,
            releaseNative: dispatcher.ReleaseNative);
        var connectorStopAgent = new StopAgent(store, connectorPrincipal, backends, dispatcher.SettleCompletedInteractive);
        // The daemon is the sole writer of team workspaces; the singleton serializes Git mutations.
        var connectorWorkspaceStore = new PRFactoryWorkspaceStore(database);
        using var teamWorkspaces = new TeamWorkspace(connectorWorkspaceStore);
        var connectorWorkspaces = new PRFactoryWorkspace(teamWorkspaces);
        var workspaceRoot = Path.Combine(state.Path, "prfactory-workspaces");
        var connectorPublications = new PRFactoryPublicationStore(database);
        var connectorHandovers = new PRFactoryHandoverStore(database);
        var connectorRepositorySets = new PRFactoryRepositorySet(new PRFactoryRepositorySetStore(database),
            connectorWorkspaces, connectorWorkspaceStore, connectorHandovers);
        // One long-lived authority per connected server; ticks construct the adapter afresh.
        PRFactoryAuthority? authority = null;
        var accounts = new AccountAdmission(new AccountWindowStore(database));
        dispatcher.LaunchGate = jobId =>
        {
            if (store.GetJob(jobId) is not { } job || job.Principal != connectorPrincipal.Principal) { return true; }
            // An exhausted account blocks only its own backend's connector turns; others keep claiming.
            if (!accounts.CanStart(job.Backend, PRFactoryWorkItems.DefaultAccount, DateTimeOffset.UtcNow)) { return false; }
            // Connector turns launch only under fresh server confirmation; unmapped ones wait for their mapping.
            var owner = authorityRows.OwnerOf(jobId);
            var current = authority;
            return owner is { } o && current is not null && o.Server == current.Server && current.MayLaunch(o.WorkItemId);
        };
        dispatcher.AgentErrorObserved = (job, code, details) =>
        {
            // Backend-owned limit evidence blocks new account work. A live interactive turn keeps running;
            // other parked turns can resume after reset through their saved session.
            if (job.Principal != connectorPrincipal.Principal) { return; }
            if (code == "agent_rate_limited"
                && accounts.BlockIfLimited(job.Backend, PRFactoryWorkItems.DefaultAccount, code, details, DateTimeOffset.UtcNow))
            {
                Log($"job {job.JobId} {job.Status}: {job.Backend} account blocked by usage limit ({details})");
            }
            else if (code != "agent_rate_limited"
                && accounts.ParkIfLimited(job.JobId, job.Backend, PRFactoryWorkItems.DefaultAccount, job.SessionId,
                    AccountAdmission.EvidenceCode(job.Backend, code), details, DateTimeOffset.UtcNow))
            {
                Log($"job {job.JobId} parked: {job.Backend} account usage limit");
            }
        };
        var connectorFollowUp = new FollowUpJob(store, connectorPrincipal, connectorAccept, dispatcher.InterruptRunning,
            reconcileIdleInteractive: dispatcher.ReconcileIdleInteractive);
        var connectorInteraction = new PRFactoryInteraction(humanWaits, connectorTeams, store, connectorFollowUp.Execute, externalTeam);
        var prfactory = PRFactoryHeartbeat.RunAsync(state, lifetime.Token, log: Log,
            onConnected: async (client, settings, machineId, ct) =>
            {
                if (authority?.Server != settings.Url)
                {
                    authority?.Dispose();
                    authority = new PRFactoryAuthority(settings.Url, authorityRows, connectorTeams,
                        connectorStop.Execute, connectorStopAgent.Execute, externalTeam.RevokeMember, dispatcher.ExecutionStopped);
                }
                await new PRFactoryWorkItems(settings.Url, settings.Repositories, connectorTeams, client,
                    connectorAccept.Execute, store.GetJob, dispatcher.Signal,
                    cwd => connectorSessions.Start(cwd, "prfactory:" + settings.Url).SessionId, Log, externalTeam,
                    connectorStop.Execute,
                    connectorFollowUp.Execute, jobLogs, authority, connectorWorkspaces, workspaceRoot, accounts,
                        publications: connectorPublications, interaction: connectorInteraction, humanWaits: humanWaits,
                        allowRepoLess: settings.TenantWideToken && settings.RepoLess,
                        handovers: connectorHandovers, repositorySets: connectorRepositorySets).TickAsync(machineId, ct);
                PRFactoryConnection.PublishJoinTickets(state, connectorTeams, settings.Url);
            },
            onTokenRejected: ct => authority?.TransportFailureAsync(Guid.Empty, System.Net.HttpStatusCode.Unauthorized, ct) ?? Task.CompletedTask,
            activeWork: () => authority is { } current && connectorTeams.Pending(current.Server).Count > 0,
            onUnavailable: ct => authority?.SuspendAsync(ct) ?? Task.CompletedTask);
        var firstStopped = await Task.WhenAny(serving, dispatching);

        // Stop admission before tearing down either service. A serving fault
        // must be reported as such, including its stack in daemon.log.
        var halted = !lifetime.IsCancellationRequested;
        if (halted)
        {
            if (firstStopped == serving)
            {
                admission.Close("daemon_unhealthy");
                Log(serving.IsFaulted
                    ? $"error: serving_halted reason=serving_fault; admission stopped: {serving.Exception!.GetBaseException()}"
                    : "error: serving_halted reason=serving_stopped; admission stopped");
            }
            else
            {
                Log($"error: dispatcher_halted reason={dispatcher.HaltReason ?? "dispatcher_fault"}; admission stopped");
            }
            lifetime.Cancel();
        }

        try { await serving; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            halted = true;
            if (firstStopped != serving)
            {
                Log($"error: serving_halted reason=serving_fault; admission stopped: {ex}");
            }
        }
        await waking;
        await pruning;
        await restoreSweep;
        await prfactory;
        authority?.Dispose(); // Only after connector ticks have stopped.
        if (webConsole is not null)
        {
            await webConsole.StopAsync();
            await webConsole.DisposeAsync();
        }
        try
        {
            await dispatching;
        }
        catch (Exception ex)
        {
            halted = true;
            Log($"error: dispatcher_halted reason=dispatcher_fault: {ex}");
        }
        foreach (var backend in interactiveBackends)
        {
            // Windows tabs retain their wrapper and PID sidecars for explicit stop after restart.
            if (backend is WtInteractiveBackend) { continue; }
            try { backend.StopAllIdleSessions(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log($"idle agent cleanup failed: {ex.GetType().Name}");
            }
        }

        // Submits admitted before closure finish their bounded transaction before
        // the dispatcher they signal is disposed. A lost reply is a same-key retry.
        try
        {
            await admission.Drained.WaitAsync(limits.BusyTimeout * 2);
        }
        catch (TimeoutException)
        {
            Log("warning: admitted submissions still in flight at exit");
        }

        Log($"stopped reason={(halted ? dispatcher.HaltReason ?? "service_fault" : "requested_shutdown")}");
        return halted ? 70 : 0;
    }

    static void Log(string message) => Console.Error.WriteLine($"[atf-daemon] {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} {message}");

    sealed class DaemonLogWriter(TextWriter stderr, TextWriter file) : TextWriter
    {
        readonly Lock _gate = new();
        public override Encoding Encoding => stderr.Encoding;
        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                stderr.WriteLine(value);
                file.WriteLine(value);
            }
        }
        public override void Write(char value)
        {
            lock (_gate)
            {
                stderr.Write(value);
                file.Write(value);
            }
        }
    }

    static async Task RunRestoreSweepAsync(HerdrTerminal terminal, string statePath, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                HerdrOwnedSessions.SweepRestored(statePath,
                    (name, records) => terminal.CloseRestoredPanesAsync(name, records, cancellationToken, Log).GetAwaiter().GetResult(), Log);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log($"restore sweep failed: {ex.GetType().Name}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    static async Task RunPruneAsync(PruneJob prune, WorktreeCleanup worktrees, int days, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                int removed = 0, kept = 0;
                foreach (var path in worktrees.ListWorktrees())
                {
                    var result = await worktrees.RemoveAsync(path, force: false, dryRun: false, auto: true, cancellationToken);
                    if (result.Outcome == "removed") { removed++; } else { kept++; }
                }
                if (removed + kept > 0) { Log($"worktrees: removed {removed}, kept {kept}"); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                Log($"worktree cleanup failed: {ex.GetType().Name}");
            }

            try
            {
                var count = prune.Execute(days, dryRun: false);
                if (count > 0)
                {
                    Log($"pruned {count} expired job(s)");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log($"prune failed: {ex.GetType().Name}");
            }

            try { await Task.Delay(TimeSpan.FromDays(1), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }
}
