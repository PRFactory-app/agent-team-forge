using System.Diagnostics;
using System.Security.Cryptography;
using System.Formats.Tar;
using System.IO.Compression;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Setup;

public sealed class InstallScriptTests
{
    static readonly string Script = FindInstallScript();

    [Fact]
    public void UpgradeStopsDaemonAndUninstallKeepsUnownedFiles()
    {
        using var temp = new TempStateDir();
        var home = temp.File("my home");
        var state = Path.Combine(home, "state dir", "agentteamforge");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "profile.json"), "{}");
        File.WriteAllText(Path.Combine(state, "operator.key"), "k");

        Assert.Equal(0, Run(home, "--archive", Bundle(temp, "0.0.1")));
        Assert.Equal(0, Run(home, "--archive", Bundle(temp, "0.0.2")));
        var stops = File.ReadAllLines(Path.Combine(home, "stops"));
        Assert.Equal([$"stop --state-dir {state}"], stops);
        var releases = Path.Combine(home, ".local", "share", "agentteamforge", "releases");
        Assert.Equal("releases/0.0.2", new DirectoryInfo(Path.Combine(home, ".local", "share", "agentteamforge", "current")).LinkTarget);

        var userFile = Path.Combine(releases, "0.0.1", "notes.txt");
        File.WriteAllText(userFile, "mine");
        Assert.Equal(0, Run(home, "--uninstall", "--purge"));

        Assert.True(File.Exists(userFile));
        Assert.False(File.Exists(Path.Combine(releases, "0.0.1", "atf")));
        Assert.False(Directory.Exists(Path.Combine(releases, "0.0.2")));
        Assert.Null(new FileInfo(Path.Combine(home, ".local", "bin", "atf")).LinkTarget);
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public void ChecksumMismatchInstallsNothing()
    {
        using var temp = new TempStateDir();
        var home = temp.File("my home");
        var archive = Bundle(temp, "0.0.1");
        File.AppendAllText(archive, "tampered");

        Assert.NotEqual(0, Run(home, "--archive", archive));
        Assert.False(Directory.Exists(Path.Combine(home, ".local", "share", "agentteamforge", "releases", "0.0.1")));
        Assert.False(Directory.Exists(Path.Combine(home, ".local", "bin")));
    }

    [Fact]
    public void CompletionPrintsQuotedAbsoluteSetupCommand()
    {
        using var temp = new TempStateDir();
        var home = temp.File("my ' home");
        var (exit, output) = RunWithOutput(home, "--archive", Bundle(temp, "0.0.1"));
        Assert.Equal(0, exit);
        Assert.Contains($"Run: '{home.Replace("'", "'\\''", StringComparison.Ordinal)}/.local/bin/atf' setup", output);
    }

    static string Bundle(TempStateDir temp, string version)
    {
        var dir = temp.File($"rel {version}");
        var payload = Path.Combine(dir, "payload");
        Directory.CreateDirectory(Path.Combine(payload, "sub dir"));
        File.WriteAllText(Path.Combine(payload, "atf"),
            $"#!/bin/sh\ncase \"$1\" in --version) echo 'atf {version}';; stop) echo \"$*\" >> \"$HOME/stops\";; esac\n");
        File.SetUnixFileMode(Path.Combine(payload, "atf"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Copy(Script, Path.Combine(payload, "install.sh"));
        File.WriteAllText(Path.Combine(payload, "sub dir", "a b.txt"), "x");
        var archive = Path.Combine(dir, $"atf-{version}-linux-x64.tar.gz");
        using (var gz = new GZipStream(File.Create(archive), CompressionLevel.Fastest))
        {
            TarFile.CreateFromDirectory(payload, gz, includeBaseDirectory: false);
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive)));
        File.WriteAllText(Path.Combine(dir, "SHA256SUMS"), $"{hash}  {Path.GetFileName(archive)}\n");
        return archive;
    }

    static int Run(string home, params string[] args) => RunWithOutput(home, args).ExitCode;

    static (int ExitCode, string Output) RunWithOutput(string home, params string[] args)
    {
        var start = new ProcessStartInfo("sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Script);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["HOME"] = home;
        start.Environment["XDG_STATE_HOME"] = Path.Combine(home, "state dir");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "install.sh timed out");
        output.Wait();
        return (process.ExitCode, output.Result);
    }

    static string FindInstallScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "install.sh");
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "AgentTeamForge.slnx")))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("install.sh not found above test output");
    }
}
