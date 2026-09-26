using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.FakeBackend;
using AgentTeamForge.Host.Features.Jobs;
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
            database = JobDatabase.Open(state.Database, limits.BusyTimeout);
        }
        catch (StorageException ex)
        {
            Log($"error: storage_{ex.Failure}");
            return 70;
        }

        var store = new JobStore(database, checkpoints);
        var quarantined = new RecoverOnStartup(store).Execute();
        Log($"recovery: quarantined {quarantined.Count} uncertain attempt(s)");

        var backendEnv = new Dictionary<string, string>();
        if (profile.TestProfile)
        {
            backendEnv[FakeBackendCommand.BarrierDirVariable] = state.BarrierDir;
        }

        var backends = BackendCatalog.Create(
            new FakeProcessBackend(Environment.ProcessPath!, ["fake-backend"], backendEnv, limits), profile.RealAgents);
        Log($"backends: {string.Join(',', backends.Names)}");
        var admission = new AdmissionGate();
        using var dispatcher = new DispatchJob(store, backends, limits, checkpoints, admission, Log);
        var accept = new AcceptJob(store, profile.Bound, limits, profile.TestProfile, admission, dispatcher.Signal, backends.Names);
        var endpoint = new JobsEndpoint(accept, new GetJob(store, profile.Bound), new FollowUpJob(store, profile.Bound, accept), checkpoints);

        var credential = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(StateDirectory.ReadPrivateFile(state.CredentialFile)).Trim());
        using var server = new IpcServer(state.Socket, credential, profile.Bound, limits, endpoint.Handle, Log);

        using var lifetime = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; lifetime.Cancel(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; lifetime.Cancel(); });

        using var listener = server.Bind();
        Log($"ready pid={Environment.ProcessId}");
        var serving = server.ServeAsync(listener, lifetime.Token);
        var dispatching = dispatcher.RunAsync(lifetime.Token);
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
}
