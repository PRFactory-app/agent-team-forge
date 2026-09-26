using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Host.Features.Setup;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class MacTabControlTests
{
    [Fact]
    public void ShellQuotingRoundTripsQuotesBackslashesAndUnicode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        const string value = "it's \\ a \"quote\" and ‘smart’ \u201Ddouble\u201D\nsecond line";
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("printf '%s' " + MacTabControl.ShellQuote(value));
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(value, output);
    }

    [Fact]
    public void AppleScriptAndWrapperKeepSpecialCharactersAsData()
    {
        const string value = "a\\b\"c ‘quote’ \u201Dquote\u201D";
        Assert.Equal("\"a\\\\b\\\"c ‘quote’ \u201Dquote\u201D\"", MacTabControl.AppleScriptQuote(value));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", "/tmp/a'b\\c", null, null, "/tmp/atf.launch.sh");
        var wrapper = MacTabControl.WrapperText(launch, value + "'; echo unsafe", "/tmp/atf.pid", "/tmp/atf'binary");
        Assert.Contains("cd '/tmp/a'\"'\"'b\\c'", wrapper);
        Assert.Contains("'/tmp/atf'\"'\"'binary' terminal-token --pid \"$$\"", wrapper);
        Assert.Contains(MacTabControl.ShellQuote(value + "'; echo unsafe"), wrapper);
        Assert.Contains("exec ", wrapper);
    }

    [Fact]
    public void LaunchersUseArgumentListsAndDoNotEmbedPromptInAppleScript()
    {
        var terminal = MacTabControl.LaunchInfo("terminal", null, null, "/tmp/a'\\‘.sh", "atftest");
        Assert.Equal("/usr/bin/osascript", terminal.FileName);
        Assert.Equal("-e", terminal.ArgumentList[0]);
        Assert.Equal("tell application \"Terminal\" to do script " +
            MacTabControl.AppleScriptQuote("exec /bin/sh " + MacTabControl.ShellQuote("/tmp/a'\\‘.sh")), terminal.ArgumentList[1]);

        var kitty = MacTabControl.LaunchInfo("kitty", "unix:/tmp/kitty.sock", "/usr/bin/kitty", "/tmp/run.sh", "atftest");
        Assert.Equal("/usr/bin/kitty", kitty.FileName);
        Assert.Equal(["@", "--to", "unix:/tmp/kitty.sock", "launch", "--type=tab", "--tab-title", "atftest", "/bin/sh", "/tmp/run.sh"],
            kitty.ArgumentList);
    }

    [Fact]
    public void SetupChoosesKittyOnlyWhenRemoteControlResponds()
    {
        static (int ExitCode, string Output) Ready(string tool, IReadOnlyList<string> args)
        {
            Assert.Equal("/usr/bin/kitty", tool);
            Assert.Equal(["@", "--to", "unix:/tmp/kitty.sock", "ls"], args);
            return (0, "[]");
        }

        Assert.Equal("kitty", SetupCommand.SelectMacTerminal("unix:/tmp/kitty.sock", Ready, "/usr/bin/kitty").TerminalProvider);
        Assert.Equal("terminal", SetupCommand.SelectMacTerminal("unix:/tmp/kitty.sock", (_, _) => (1, ""), "/usr/bin/kitty").TerminalProvider);
        Assert.Equal("terminal", SetupCommand.SelectMacTerminal(null, (_, _) => throw new InvalidOperationException(), "/usr/bin/kitty").TerminalProvider);
    }
}
