using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Setup;

public sealed class SystemdUserTests
{
    [Fact]
    public void ServicePrefix_IsAnOwnKillModeProcessCollectedServiceWithAUniqueUnitAndNoSecrets()
    {
        var first = SystemdUser.ServicePrefix("/state", SystemdUser.NewUnitName(), "/state/daemon.log");
        var second = SystemdUser.ServicePrefix("/state", SystemdUser.NewUnitName(), "/state/daemon.log");

        Assert.Contains("--collect", first);
        Assert.DoesNotContain("--scope", first);
        Assert.Contains("KillMode=process", first);
        Assert.Contains("Restart=no", first);
        Assert.Equal("--", first[^1]);
        Assert.Contains("EnvironmentFile=-/state/daemon.env", first);
        Assert.DoesNotContain(first, a => a is "-E" or "--setenv" || a.StartsWith("--setenv=", StringComparison.Ordinal));
        var unit = Assert.Single(first, a => a.StartsWith("--unit=agentteamforge-daemon-", StringComparison.Ordinal));
        Assert.NotEqual(unit, Assert.Single(second, a => a.StartsWith("--unit=", StringComparison.Ordinal)));
    }

    // Follows systemd's env-file rules: inside double quotes only \" \\ \` \$ are escapes, anything else after a
    // backslash stays as written, and raw newlines belong to the value.
    static Dictionary<string, string> ParseEnvironmentFile(string text)
    {
        var result = new Dictionary<string, string>();
        var i = 0;
        while (i < text.Length)
        {
            var eq = text.IndexOf('=', i);
            var key = text[i..eq];
            i = eq + 1;
            Assert.Equal('"', text[i++]);
            var value = new System.Text.StringBuilder();
            while (text[i] != '"')
            {
                if (text[i] == '\\')
                {
                    i++;
                    if ("\"\\`$".Contains(text[i], StringComparison.Ordinal)) { value.Append(text[i++]); }
                    else { value.Append('\\'); }
                    continue;
                }
                value.Append(text[i++]);
            }
            i++;
            Assert.Equal('\n', text[i++]);
            result[key] = value.ToString();
        }
        return result;
    }

    [Fact]
    public void EnvironmentFile_RoundTripsAwkwardValuesAndSkipsInvalidNames()
    {
        var values = new Dictionary<string, string?>
        {
            ["PEM"] = "-----BEGIN-----\nabc\r\ndef\n-----END-----",
            ["QUOTES"] = "a \"b\" \\ c \\n literal $HOME `x` %h",
            ["EMPTY"] = "",
            ["BAD-NAME"] = "x",
            ["1BAD"] = "x",
            ["NULL"] = null,
        };

        var parsed = ParseEnvironmentFile(SystemdUser.EnvironmentFileContent(values));

        Assert.Equal(["PEM", "QUOTES", "EMPTY"], parsed.Keys);
        Assert.Equal(values["PEM"], parsed["PEM"]);
        Assert.Equal(values["QUOTES"], parsed["QUOTES"]);
        Assert.Equal("", parsed["EMPTY"]);
    }

    // Splits an Exec line the way systemd does (double quotes with C escapes) and undoes % and $ doubling.
    static List<string> SplitExec(string line)
    {
        var words = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == ' ') { i++; continue; }
            var word = new System.Text.StringBuilder();
            while (i < line.Length && line[i] != ' ')
            {
                var quote = line[i] is '\'' or '"' ? line[i++] : '\0';
                if (quote == '\0') { word.Append(line[i++]); continue; }
                while (line[i] != quote)
                {
                    if (line[i] == '\\' && quote == '"') { i++; word.Append(line[i] == 'n' ? '\n' : line[i]); i++; }
                    else { word.Append(line[i++]); }
                }
                i++;
            }
            words.Add(word.ToString().Replace("%%", "%", StringComparison.Ordinal).Replace("$$", "$", StringComparison.Ordinal));
        }
        return words;
    }

    [Fact]
    public void ServicePrefix_EscapesEachPropertyForItsOwnSyntax()
    {
        const string State = "/run/user/1000/it's $x\\dir 100%";
        var args = SystemdUser.ServicePrefix(State, "agentteamforge-daemon-x", State + "/daemon.log", "/work/it's $y");

        // Plain paths: only % is special.
        Assert.Contains("EnvironmentFile=-/run/user/1000/it's $x\\dir 100%%/daemon.env", args);
        Assert.Contains("StandardOutput=append:/run/user/1000/it's $x\\dir 100%%/daemon.log", args);
        Assert.Contains("WorkingDirectory=-/work/it's $y", args);
        // The exit path is exactly one Exec argument, outside the shell text.
        var stop = Assert.Single(args, a => a.StartsWith("ExecStopPost=", StringComparison.Ordinal))["ExecStopPost=".Length..];
        var words = SplitExec(stop);
        Assert.Equal(["/bin/sh", "-c"], words[..2]);
        Assert.DoesNotContain("daemon.exit", words[2], StringComparison.Ordinal);
        Assert.Equal(State + "/daemon.exit", words[^1]);
        Assert.Equal("sh", words[3]);
        Assert.Equal(5, words.Count);
    }

    [Fact]
    public void EnvironmentFile_IsOwnerOnly()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var temp = new TempStateDir();
        var path = temp.File("daemon.env");

        SystemdUser.WriteEnvironmentFile(path, new Dictionary<string, string?> { ["TOKEN"] = "secret" });

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void LastExitResult_ReadsTheRecordedExitAndFallsBackToTheUnit()
    {
        using var temp = new TempStateDir();
        Assert.Null(SystemdUser.LastExitResult(temp.Path, _ => "ignored"));

        File.WriteAllText(Path.Combine(temp.Path, SystemdUser.UnitFile), "agentteamforge-daemon-abcd1234");
        Assert.Equal("Result=signal", SystemdUser.LastExitResult(temp.Path, unit => unit == "agentteamforge-daemon-abcd1234" ? "Result=signal" : null));

        File.WriteAllText(Path.Combine(temp.Path, SystemdUser.ExitFile), "result=signal code=killed status=KILL\n");
        Assert.Equal("result=signal code=killed status=KILL", SystemdUser.LastExitResult(temp.Path, _ => "ignored"));
    }

    [Fact]
    public void ServiceAvailable_NeedsSystemdRunAndAUserManager()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var temp = new TempStateDir();
        var runtime = temp.File("run");
        Directory.CreateDirectory(Path.Combine(runtime, "systemd"));
        File.WriteAllText(Path.Combine(runtime, "bus"), "");
        string? Env(string name) => name == "XDG_RUNTIME_DIR" ? runtime : null;

        Assert.False(SystemdUser.ServiceAvailable(Env, _ => null));
        Assert.False(SystemdUser.ServiceAvailable(_ => null, _ => "/usr/bin/systemd-run"));
        Assert.True(SystemdUser.ServiceAvailable(Env, _ => "/usr/bin/systemd-run"));
    }

    [Fact]
    public void EnsureRuntimeDir_FillsOnlyAMissingValueFromALiveUserManager()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var temp = new TempStateDir();
        var runtime = temp.File("run");
        var env = new Dictionary<string, string?>();

        SystemdUser.EnsureRuntimeDir(env, runtime);
        Assert.False(env.ContainsKey("XDG_RUNTIME_DIR"));

        Directory.CreateDirectory(Path.Combine(runtime, "systemd"));
        File.WriteAllText(Path.Combine(runtime, "bus"), "");
        SystemdUser.EnsureRuntimeDir(env, runtime);
        Assert.Equal(runtime, env["XDG_RUNTIME_DIR"]);

        env["XDG_RUNTIME_DIR"] = "/elsewhere";
        SystemdUser.EnsureRuntimeDir(env, runtime);
        Assert.Equal("/elsewhere", env["XDG_RUNTIME_DIR"]);

        env["XDG_RUNTIME_DIR"] = "";
        SystemdUser.EnsureRuntimeDir(env, runtime);
        Assert.Equal(runtime, env["XDG_RUNTIME_DIR"]);
    }

    [Fact]
    public void ParseSessionEnvironment_KeepsOnlySessionKeysWithPlainValues()
    {
        var parsed = SystemdUser.ParseSessionEnvironment(
            "WAYLAND_DISPLAY=wayland-1\nDISPLAY=:0\nXDG_RUNTIME_DIR=/run/user/1000\n" +
            "DBUS_SESSION_BUS_ADDRESS=$'unix:path=/x'\nPATH=/usr/bin\nHOME=/home/u\n");

        Assert.Equal(
            new Dictionary<string, string> { ["WAYLAND_DISPLAY"] = "wayland-1", ["DISPLAY"] = ":0", ["XDG_RUNTIME_DIR"] = "/run/user/1000" },
            parsed);
    }
}
