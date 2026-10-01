using System.Text.RegularExpressions;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Setup;

// The launchd label is per uid, not per HOME or state directory: a setup run under another HOME
// must never unload, replace or start the job another installation loaded.
public sealed class LaunchdOwnershipTests
{
    const string Service = "gui/501/com.agentteamforge.daemon";
    const string ForeignBinary = "/Users/real/.local/bin/atf";
    const string ForeignState = "/Users/real/.local/state/agentteamforge";

    /// <summary>The shape `launchctl print gui/UID/LABEL` reports for a loaded job.</summary>
    internal static string Print(string binary, string stateDir) =>
        $"{Service} = {{\n\tactive count = 1\n\tpath = /Users/x/Library/LaunchAgents/com.agentteamforge.daemon.plist\n" +
        $"\tstate = running\n\n\tprogram = {binary}\n\targuments = {{\n\t\t{binary}\n\t\tdaemon\n\t\t--state-dir\n\t\t{stateDir}\n\t}}\n\n" +
        "\tworking directory = /\n}\n";

    /// <summary><see cref="Print"/> for the job `launchctl bootstrap` would load from the plist at <paramref name="path"/>.</summary>
    internal static string PrintPlist(string path)
    {
        var arguments = Regex.Matches(Regex.Match(File.ReadAllText(path), "<key>ProgramArguments</key><array>(.*?)</array>",
            RegexOptions.Singleline).Groups[1].Value, "<string>([^<]*)</string>").Select(match => match.Groups[1].Value).ToList();
        return Print(arguments[0], arguments[3]);
    }

    static Func<string, IReadOnlyList<string>, (int, string)> Launchd(List<string> calls, string? loaded) => (tool, args) =>
    {
        calls.Add(tool + " " + string.Join(' ', args));
        return (tool, args[0]) switch
        {
            ("id", _) => (0, "501\n"),
            ("launchctl", "print") => loaded is null ? (113, "Could not find service") : (0, loaded),
            _ => (0, ""),
        };
    };

    [Fact]
    public void Disabling_never_boots_out_a_job_that_runs_another_installation()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var calls = new List<string>();
        var runner = Launchd(calls, Print(ForeignBinary, ForeignState));

        // No plist under this HOME: nothing of ours to remove, the foreign job keeps running.
        Assert.Equal(0, LoginAutostart.Apply(home, "/tmp/atf", "/tmp/state", false, runner, "macos"));
        Assert.DoesNotContain(calls, call => call.StartsWith("launchctl bootout", StringComparison.Ordinal));

        // This HOME's own plist is removed, but the loaded job it did not start is left alone.
        var path = LoginAutostart.FilePath(home, "macos");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, LoginAutostart.MacPlist("/tmp/atf", "/tmp/state", "/usr/bin"));
        Assert.Equal(0, LoginAutostart.Apply(home, "/tmp/atf", "/tmp/state", false, runner, "macos"));
        Assert.False(File.Exists(path));
        Assert.DoesNotContain(calls, call => call.StartsWith("launchctl bootout", StringComparison.Ordinal));
    }

    [Fact]
    public void Enabling_refuses_to_replace_a_job_that_runs_another_installation()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var calls = new List<string>();
        var runner = Launchd(calls, Print(ForeignBinary, ForeignState));

        Assert.Equal(1, LoginAutostart.Apply(home, "/tmp/atf", "/tmp/state", true, runner, "macos"));
        Assert.DoesNotContain(calls, call => call.StartsWith("launchctl boot", StringComparison.Ordinal));
        Assert.False(File.Exists(LoginAutostart.FilePath(home, "macos")));

        // An unchanged plist under this HOME does not make the foreign loaded job ours either.
        var path = LoginAutostart.FilePath(home, "macos");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var mine = LoginAutostart.MacPlist("/tmp/atf", "/tmp/state", Environment.GetEnvironmentVariable("PATH") ?? "");
        File.WriteAllText(path, mine);
        Assert.Equal(1, LoginAutostart.Apply(home, "/tmp/atf", "/tmp/state", true, runner, "macos"));
        Assert.Equal(mine, File.ReadAllText(path));
        Assert.DoesNotContain(calls, call => call.StartsWith("launchctl boot", StringComparison.Ordinal));
    }

    [Fact]
    public void Owned_loaded_job_is_unloaded_by_off_and_replaced_by_on()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var calls = new List<string>();

        // The loaded job runs this binary on this state directory, even without a plist under this HOME.
        Assert.Equal(0, LoginAutostart.Apply(home, "/tmp/atf", "/tmp/state", false, Launchd(calls, Print("/tmp/atf", "/tmp/state")), "macos"));
        Assert.Contains($"launchctl bootout {Service}", calls);

        // The loaded job runs what this HOME's previous plist registered: switching state directory reloads it.
        calls.Clear();
        var path = LoginAutostart.FilePath(home, "macos");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, LoginAutostart.MacPlist("/tmp/atf", "/tmp/old-state", "/usr/bin"));
        Assert.Equal(0, LoginAutostart.Apply(home, "/tmp/atf", "/tmp/state", true, Launchd(calls, Print("/tmp/atf", "/tmp/old-state")), "macos"));
        Assert.Contains($"launchctl bootout {Service}", calls);
        Assert.Contains($"launchctl bootstrap gui/501 {path}", calls);
    }

    [Fact]
    public void Lazy_start_never_kickstarts_a_loaded_job_of_another_installation()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var path = LoginAutostart.FilePath(home, "macos");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, LoginAutostart.MacPlist("/opt/atf", "/srv/state", "/usr/bin"));

        Assert.Null(LoginAutostart.LaunchdService(home, "/opt/atf", "/srv/state", Launchd([], Print(ForeignBinary, ForeignState))));
        Assert.Equal(Service, LoginAutostart.LaunchdService(home, "/opt/atf", "/srv/state", Launchd([], Print("/opt/atf", "/srv/state"))));
    }

    [Fact]
    public void Setup_autostart_off_under_an_isolated_home_leaves_the_real_job_loaded()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        using var temp = new TempStateDir();
        var calls = new List<string>();
        Assert.Equal(0, SetupCommand.Run(new Dictionary<string, string> { ["autostart"] = "off", ["state-dir"] = temp.File("state") },
            Launchd(calls, Print(ForeignBinary, ForeignState)), "/tmp/atf", homePath: temp.File("home")));
        Assert.DoesNotContain(calls, call => call.StartsWith("launchctl bootout", StringComparison.Ordinal));
    }
}
