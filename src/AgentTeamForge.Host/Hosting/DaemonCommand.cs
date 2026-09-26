using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.FakeBackend;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Host.Features.Setup;
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
        var profile = SpikeProfileFile.Load(state);
        var launchMode = SetupCommand.ConfiguredMode(state);
        if (launchMode == "herdr" && !profile.RealAgents)
        {
            Log("error: herdr mode requires an agents profile");
            return 78;
        }
        if ((crashAt is not null || failAt is not null) && !profile.TestProfile)
        {
            Log("test controls require an explicit test profile");
            return 64;
        }

        using var daemonLock = DaemonLock.TryAcquire(state.LockFile);
        if (daemonLock is null)
        {
            // Another live daemon owns the endpoint; touch nothing.
            Log("error: daemon_already_running");
            return 75;
        }

        daemonLock.WriteOwnerPid();

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
        var jobLogs = new JobLogs(state.Path, Log);
        var prune = new PruneJob(new PruneJobs(database), state.Path);
        var wakeStore = new WakeStore(database);
        var quarantined = new RecoverOnStartup(store).Execute();
        Log($"recovery: quarantined {quarantined.Count} uncertain attempt(s)");

        var backendEnv = new Dictionary<string, string>();
        if (profile.TestProfile)
        {
            backendEnv[FakeBackendCommand.BarrierDirVariable] = state.BarrierDir;
        }

        var backends = BackendCatalog.Create(
            new FakeProcessBackend(Environment.ProcessPath!, ["fake-backend"], backendEnv, limits), profile.RealAgents && launchMode != "herdr");
        if (launchMode == "herdr")
        {
            var seed = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.Ordinal);
            HerdrInteractiveBackend Interactive(InteractiveAgentKind kind) =>
                new(new HerdrTerminal(new HerdrTerminalOptions { Environment = seed }), kind, state.Path);
            var claude = Interactive(InteractiveAgentKind.Claude);
            var codex = Interactive(InteractiveAgentKind.Codex);
            var pi = Interactive(InteractiveAgentKind.Pi);
            backends.Register(BackendCatalog.Claude, () => claude);
            backends.Register(BackendCatalog.Codex, () => codex);
            backends.Register(BackendCatalog.Pi, () => pi);
        }
        Log($"backends: {string.Join(',', backends.Names)}");
        var admission = new AdmissionGate();
        using var dispatcher = new DispatchJob(store, backends, limits, checkpoints, admission, Log, jobLogs);
        var accept = new AcceptJob(store, profile.Bound, limits, profile.TestProfile, admission, backends.Names);
        var endpoint = new JobsEndpoint(accept, new GetJob(store, profile.Bound), new FollowUpJob(store, profile.Bound, accept, dispatcher.InterruptRunning),
            new ListJobs(store, profile.Bound),
            new StopJob(store, profile.Bound, dispatcher.CancelRunning), checkpoints, dispatcher.Signal, wakeStore, prune, jobLogs);

        var credential = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(StateDirectory.ReadPrivateFile(state.CredentialFile)).Trim());
        using var server = new IpcServer(state.Socket, credential, profile.Bound, limits, endpoint.Handle, Log, endpoint.AfterReply,
            request => request.Op is IpcProtocol.JobSubmit or IpcProtocol.JobFollowUp ? dispatcher.PauseClaims() : null);

        using var lifetime = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; lifetime.Cancel(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; lifetime.Cancel(); });

        using var listener = server.Bind();
        Log($"ready pid={Environment.ProcessId}");
        var serving = server.ServeAsync(listener, lifetime.Token);
        var dispatching = dispatcher.RunAsync(lifetime.Token);
        var waking = new WakeCoordinator(wakeStore, new NativeWakePoster(state.Path), Log).RunAsync(lifetime.Token);
        var pruning = profile.AutoPrune ? RunPruneAsync(prune, profile.PruneOlderThanDays, lifetime.Token) : Task.CompletedTask;
        var prfactory = PRFactoryHeartbeat.RunAsync(state, lifetime.Token, log: Log);
        await Task.WhenAny(serving, dispatching);

        // The dispatcher only returns on its own when halted or faulted; it closed
        // the shared admission gate at that instant, so later submits get
        // daemon_unhealthy. Stop the listener and exit unhealthy; restart recovery
        // quarantines the run and queued work admitted before the halt runs then.
        var halted = !lifetime.IsCancellationRequested;
        if (halted)
        {
            Log($"error: dispatcher_halted reason={dispatcher.HaltReason ?? "dispatcher_fault"}; admission stopped");
            lifetime.Cancel();
        }

        await serving;
        await waking;
        await pruning;
        await prfactory;
        try
        {
            await dispatching;
        }
        catch (Exception ex)
        {
            halted = true;
            Log($"error: dispatcher_halted reason=dispatcher_fault ({ex.GetType().Name})");
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

        return halted ? 70 : 0;
    }

    static void Log(string message) => Console.Error.WriteLine($"[atf-daemon] {message}");

    static async Task RunPruneAsync(PruneJob prune, int days, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
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
