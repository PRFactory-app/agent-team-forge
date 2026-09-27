using System.Diagnostics;
using System.Security.Cryptography;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Nodes;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Setup;

public sealed class InstallScriptTests
{
    static readonly string Script = FindInstallScript();

    [Fact]
    public void WindowsReleaseIncludesInstallerAndChecksumsIt()
    {
        var root = Path.GetDirectoryName(Script)!;
        var installer = File.ReadAllText(Path.Combine(root, "install.ps1"));
        // Windows PowerShell 5.1 has no null-coalescing or null-conditional operators.
        Assert.DoesNotContain("??", installer);
        Assert.DoesNotContain("?.", installer);
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        Assert.Contains("Copy-Item install.ps1 $bundle", workflow);
        Assert.Contains("Copy-Item install.ps1 $release", workflow);
        Assert.Contains("$files = @($archive, 'install.ps1')", workflow);
        Assert.Contains("Get-FileHash -Algorithm SHA256", workflow);
        Assert.Contains("SHA256SUMS-win-x64", workflow);
    }

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
        var (exit, output, _) = RunWith(home, ["--archive", Bundle(temp, "0.0.1")]);
        Assert.Equal(0, exit);
        Assert.Contains($"Run: '{home.Replace("'", "'\\''", StringComparison.Ordinal)}/.local/bin/atf' setup", output);
    }

    [Fact]
    public void SameVersionRerunVerifiesFilesAndDoesNotStopDaemon()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var archive = Bundle(temp, "0.0.1");
        Assert.Equal(0, Run(home, "--archive", archive));
        Assert.Equal(0, Run(home, "--archive", archive));
        Assert.False(File.Exists(Path.Combine(home, "stops")));
        var installed = Path.Combine(home, ".local", "share", "agentteamforge", "releases", "0.0.1", "atf");
        File.AppendAllText(installed, "modified");
        Assert.Contains("installed files failed verification", RunWith(home, ["--archive", archive]).Error);
    }

    [Fact]
    public void FailedDaemonStopKeepsCurrentReleaseAndState()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var state = Path.Combine(home, "state dir", "agentteamforge");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "launch-mode.json"), "headless");
        Assert.Equal(0, Run(home, "--archive", Bundle(temp, "0.0.1")));
        var (code, _, error) = RunWith(home, ["--archive", Bundle(temp, "0.0.2")], new Dictionary<string, string> { ["ATF_STOP_FAIL"] = "1" });
        Assert.NotEqual(0, code);
        Assert.Contains("daemon did not stop", error);
        var current = Path.Combine(home, ".local", "share", "agentteamforge", "current");
        Assert.Equal("releases/0.0.1", new DirectoryInfo(current).LinkTarget);
        Assert.Equal("headless", File.ReadAllText(Path.Combine(state, "launch-mode.json")));
    }

    [Fact]
    public void ReleaseDownloadsUseOneResolvedTagAndReportFailures()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var fixture = temp.File("curl fixture");
        Directory.CreateDirectory(fixture);
        var archive = Bundle(temp, "0.0.1");
        File.Copy(archive, Path.Combine(fixture, Path.GetFileName(archive)));
        File.Copy(Path.Combine(Path.GetDirectoryName(archive)!, "SHA256SUMS"), Path.Combine(fixture, "SHA256SUMS"));
        var tools = temp.File("tools");
        Directory.CreateDirectory(tools);
        var curl = Path.Combine(tools, "curl");
        File.WriteAllText(curl, """
            #!/bin/sh
            while [ "$#" -gt 0 ]; do
              case "$1" in -o) out=$2; shift 2;; -w) shift 2;; -*) shift;; *) url=$1; shift;; esac
            done
            printf '%s\n' "$url" >> "$ATF_FIXTURE/urls"
            case "$url" in
              */latest)
                [ ! -f "$ATF_FIXTURE/network" ] || exit 7
                if [ -f "$ATF_FIXTURE/no-release" ]; then printf '404 %s' "$url"; else printf '200 https://fixture/releases/tag/v0.0.1'; fi;;
              */download/*)
                file=${url##*/}
                [ -f "$ATF_FIXTURE/$file" ] || { printf '404'; exit 0; }
                cp "$ATF_FIXTURE/$file" "$out"
                printf '200';;
              *) exit 7;;
            esac
            """ + "\n");
        File.SetUnixFileMode(curl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var env = new Dictionary<string, string>
        {
            ["PATH"] = tools + ":" + Environment.GetEnvironmentVariable("PATH"),
            ["ATF_RELEASES_URL"] = "https://fixture/releases",
            ["ATF_FIXTURE"] = fixture
        };

        Assert.Equal(0, RunWith(home, [], env).Code);
        Assert.Equal(0, RunWith(home, ["--version", "v0.0.1"], env).Code);
        var urls = File.ReadAllLines(Path.Combine(fixture, "urls"));
        Assert.Contains("https://fixture/releases/latest", urls);
        Assert.Equal(4, urls.Count(url => url.StartsWith("https://fixture/releases/download/v0.0.1/", StringComparison.Ordinal)));
        File.WriteAllText(Path.Combine(fixture, "no-release"), "");
        Assert.Contains("no release is published yet", RunWith(temp.File("absent-home"), [], env).Error);
        File.Delete(Path.Combine(fixture, "no-release"));
        File.Delete(Path.Combine(fixture, Path.GetFileName(archive)));
        Assert.Contains("missing release asset", RunWith(temp.File("missing-home"), [], env).Error);
        File.Copy(archive, Path.Combine(fixture, Path.GetFileName(archive)));
        File.Delete(Path.Combine(fixture, "SHA256SUMS"));
        Assert.Contains("missing release asset", RunWith(temp.File("missing-hash-home"), [], env).Error);
        File.WriteAllText(Path.Combine(fixture, "SHA256SUMS"), new string('0', 64) + "  " + Path.GetFileName(archive));
        Assert.Contains("archive checksum mismatch", RunWith(temp.File("bad-hash-home"), [], env).Error);
        File.WriteAllText(Path.Combine(fixture, "network"), "");
        Assert.Contains("network error resolving latest release", RunWith(temp.File("network-home"), [], env).Error);
        var uname = Path.Combine(tools, "uname");
        File.WriteAllText(uname, "#!/bin/sh\ncase \"$1\" in -s) echo FreeBSD;; -m) echo amd64;; esac\n");
        File.SetUnixFileMode(uname, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Contains("unsupported platform FreeBSD-amd64", RunWith(temp.File("unsupported-home"), [], env).Error);
    }

    [Fact]
    public void TeardownRemovesOnlyOwnedClientEntries()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var state = Path.Combine(home, "state");
        var binary = Path.Combine(home, ".local", "bin", "atf");
        var pi = Path.Combine(home, ".pi", "agent");
        Directory.CreateDirectory(pi);
        var extension = Path.Combine(home, ".local", "share", "agentteamforge", "current", "extensions", "pi-wake");
        File.WriteAllText(Path.Combine(pi, "settings.json"), new JsonObject { ["packages"] = new JsonArray("npm:pi-mcp-adapter", extension, "other") }.ToJsonString());
        File.WriteAllText(Path.Combine(pi, "mcp.json"), new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["agentteamforge"] = new JsonObject { ["command"] = binary, ["args"] = new JsonArray("mcp", "--state-dir", state) },
                ["other"] = new JsonObject { ["command"] = "other" }
            }
        }.ToJsonString());
        File.WriteAllText(Path.Combine(pi, "agentteamforge.json"), new JsonObject { ["stateDir"] = state, ["other"] = "keep" }.ToJsonString());
        var removed = new List<string>();
        (int, string) Runner(string tool, IReadOnlyList<string> args)
        {
            if (args.SequenceEqual(["mcp", "get", "agentteamforge"]))
            {
                return tool == "claude" ? (0, $"Command: {binary}\nArgs: mcp --state-dir {state}\nScope: User config\n")
                    : (0, "Command: /other/atf\nArgs: mcp --state-dir elsewhere\n");
            }
            removed.Add(tool + " " + string.Join(' ', args));
            return (0, "");
        }
        Assert.True(ClientSetup.Teardown(binary, state, home, Runner));
        Assert.Equal(["claude mcp remove agentteamforge --scope user"], removed);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(pi, "settings.json")))!;
        Assert.Equal(2, settings["packages"]!.AsArray().Count);
        Assert.Equal("npm:pi-mcp-adapter", settings["packages"]![0]!.GetValue<string>());
        var mcp = JsonNode.Parse(File.ReadAllText(Path.Combine(pi, "mcp.json")))!;
        Assert.Null(mcp["mcpServers"]!["agentteamforge"]);
        Assert.NotNull(mcp["mcpServers"]!["other"]);
        Assert.Equal("keep", JsonNode.Parse(File.ReadAllText(Path.Combine(pi, "agentteamforge.json")))!["other"]!.GetValue<string>());
    }

    [Fact]
    public void TeardownRemovesOnlyOwnedAutostart()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var binary = Path.Combine(home, ".local", "bin", "atf");
        var state = Path.Combine(home, "state");
        var unit = LoginAutostart.FilePath(home, "linux");
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        var calls = new List<string>();
        (int, string) Runner(string tool, IReadOnlyList<string> args)
        {
            calls.Add(tool + " " + string.Join(' ', args));
            return (0, "");
        }
        File.WriteAllText(unit, LoginAutostart.LinuxUnit("/other/atf", state, "/usr/bin"));
        Assert.True(LoginAutostart.RemoveOwned(home, binary, state, Runner));
        Assert.True(File.Exists(unit));
        File.WriteAllText(unit, LoginAutostart.LinuxUnit(binary, state, "/usr/bin"));
        Assert.True(LoginAutostart.RemoveOwned(home, binary, state, Runner));
        Assert.False(File.Exists(unit));
        Assert.Contains("systemctl --user disable agentteamforge.service", calls);
    }

    static string Bundle(TempStateDir temp, string version)
    {
        var dir = temp.File($"rel {version}");
        var payload = Path.Combine(dir, "payload");
        Directory.CreateDirectory(Path.Combine(payload, "sub dir"));
        File.WriteAllText(Path.Combine(payload, "atf"),
            $"#!/bin/sh\ncase \"$1\" in --version) echo 'atf {version}';; stop) echo \"$*\" >> \"$HOME/stops\"; [ -z \"${{ATF_STOP_FAIL:-}}\" ];; esac\n");
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

    static int Run(string home, params string[] args) => RunWith(home, args).Code;

    static (int Code, string Output, string Error) RunWith(string home, string[] args, Dictionary<string, string>? env = null)
    {
        var start = new ProcessStartInfo("sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Script);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["HOME"] = home;
        start.Environment["XDG_STATE_HOME"] = Path.Combine(home, "state dir");
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(home, "config");
        start.Environment["XDG_DATA_HOME"] = Path.Combine(home, "data");
        start.Environment["CODEX_HOME"] = Path.Combine(home, "codex");
        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                start.Environment[key] = value;
            }
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "install.sh timed out");
        output.Wait();
        error.Wait();
        return (process.ExitCode, output.Result, error.Result);
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
