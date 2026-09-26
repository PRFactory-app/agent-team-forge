using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
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
    public void WrapperKeepsSpecialCharactersAsData()
    {
        const string value = "a\\b\"c ‘quote’ \u201Dquote\u201D";
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", "/tmp/a'b\\c", null, null, "/tmp/atf.launch.sh");
        var wrapper = MacTabControl.WrapperText(launch, value + "'; echo unsafe", "/tmp/atf.pid", "/tmp/atf'binary");
        Assert.Contains("cd '/tmp/a'\"'\"'b\\c'", wrapper);
        Assert.Contains("'/tmp/atf'\"'\"'binary' terminal-token --pid \"$$\"", wrapper);
        Assert.Contains(MacTabControl.ShellQuote(value + "'; echo unsafe"), wrapper);
        Assert.Contains("exec ", wrapper);
    }

    [Fact]
    public void WrapperQuotesResolvedArgumentsAndResume()
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", "/tmp/repo", "native-1", null, "/tmp/atf.launch.sh")
            .WithSelection("model=gpt-6-sol;effort=xhigh");
        var wrapper = MacTabControl.WrapperText(launch, "task", "/tmp/atf.pid", "/tmp/atf");
        Assert.Contains("'-m' 'gpt-6-sol' '-c' 'model_reasoning_effort=\"xhigh\"' 'resume' 'native-1'", wrapper);
    }

    [Fact]
    public void ClaudeTerminalWrapperExportsPerLaunchWorkspaceTrust()
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", "/tmp/new dir", null, null, "/tmp/atf.launch.sh");
        var wrapper = MacTabControl.WrapperText(launch, "task", "/tmp/atf.pid", "/tmp/atf");

        Assert.Contains("export CLAUDE_CODE_SANDBOXED='1'", wrapper);
        Assert.Contains("'--settings' '{\"skipDangerousModePermissionPrompt\":true}'", wrapper);
    }

    [Fact]
    public void UnixCodexLaunchKeepsUserHooks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", "/tmp/repo", null, null, "/tmp/atf.launch.sh");
        var args = WtTabControl.AgentArguments(launch, "task");
        Assert.DoesNotContain("--dangerously-bypass-hook-trust", args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("hooks.", StringComparison.Ordinal));
    }

    [Fact]
    public void LaunchersUseArgumentListsAndDoNotEmbedPromptInAppleScript()
    {
        const string wrapper = "/tmp/a'\"‘\u201D $(x).sh";
        var terminal = MacTabControl.LaunchInfo("terminal", null, null, wrapper, "atftest");
        Assert.Equal("/usr/bin/osascript", terminal.FileName);
        Assert.Equal(["-e", "on run argv", "-e", "tell application \"Terminal\" to do script (item 1 of argv)", "-e", "end run",
            "exec /bin/sh " + MacTabControl.ShellQuote(wrapper)], terminal.ArgumentList);
        Assert.Throws<BackendNotStartedException>(() => MacTabControl.LaunchInfo("terminal", null, null, "/tmp/a\\b.sh", "atftest"));
        Assert.Throws<BackendNotStartedException>(() => MacTabControl.LaunchInfo("terminal", null, null, "/tmp/a\nb.sh", "atftest"));

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
