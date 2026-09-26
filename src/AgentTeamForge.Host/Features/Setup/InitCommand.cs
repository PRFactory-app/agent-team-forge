using AgentTeamForge.DAL.Files;
using System.Security.Cryptography;
using System.Text.Json;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>
/// Creates a private state directory: profile (real agents by default, fake only
/// for a test profile or --backends fake), operator
/// credential (0600, never printed), and a fresh database. Refuses to reuse one.
/// </summary>
public static class InitCommand
{
    public static int Run(string stateDir, bool testProfile, int? queueLimit, int? maxRuntimeSeconds, string? backends = null)
    {
        backends ??= testProfile ? SpikeProfileFile.FakeBackends : SpikeProfileFile.AgentBackends;
        if (backends is not (SpikeProfileFile.FakeBackends or SpikeProfileFile.AgentBackends) || (testProfile && backends != SpikeProfileFile.FakeBackends))
        {
            Console.Error.WriteLine("error: profile_invalid_backends");
            return 2;
        }

        if (!SpikeProfileFile.LimitsAreValid(queueLimit, maxRuntimeSeconds))
        {
            Console.Error.WriteLine("error: profile_invalid_limits");
            return 2;
        }

        if (Directory.Exists(stateDir) && Directory.EnumerateFileSystemEntries(stateDir).Any())
        {
            Console.Error.WriteLine("error: state_dir_not_empty");
            return 2;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(stateDir);
        }
        else
        {
            Directory.CreateDirectory(stateDir, StateDirectory.PrivateDir);
        }
        if (OperatingSystem.IsWindows())
        {
            WindowsPrivatePaths.Protect(stateDir);
        }
        else
        {
            File.SetUnixFileMode(stateDir, StateDirectory.PrivateDir);
        }

        var state = StateDirectory.Open(stateDir);

        var profile = new SpikeProfileFile
        {
            Principal = "local-operator",
            Team = "spike-team",
            Agent = "fake-agent",
            Backend = backends,
            TestProfile = testProfile,
            QueueLimit = queueLimit,
            MaxFakeRuntimeSeconds = maxRuntimeSeconds,
        };
        WritePrivate(state.ProfileFile, JsonSerializer.SerializeToUtf8Bytes(profile, SetupJson.Default.SpikeProfileFile));
        WritePrivate(state.CredentialFile, System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        if (testProfile)
        {
            StateDirectory.CreatePrivateDirectory(state.BarrierDir);
        }

        JobDatabase.Create(state.Database, profile.Limits.BusyTimeout);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(state.Database, StateDirectory.PrivateFile);
        }

        Console.Out.WriteLine(state.Path);
        return 0;
    }

    static void WritePrivate(string path, byte[] content)
    {
        using var stream = new FileStream(path, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write));
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }
}
