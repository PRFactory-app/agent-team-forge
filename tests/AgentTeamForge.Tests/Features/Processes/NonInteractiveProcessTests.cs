using System.Diagnostics;
using AgentTeamForge.Business.Features.Processes;

namespace AgentTeamForge.Tests.Features.Processes;

public sealed class NonInteractiveProcessTests
{
    [Fact]
    public async Task Helper_that_reads_stdin_gets_eof_without_mcp_stdio()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe", ["-NoProfile", "-Command",
                "$text = [Console]::In.ReadToEnd(); if ($text.Length -gt 0) { [Console]::Out.Write($text); exit 1 }"])
            : new ProcessStartInfo("/bin/sh", ["-c", "data=$(cat); test -z \"$data\""]);
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;

        using var process = NonInteractiveProcess.Start(info)!;
        Assert.True(info.RedirectStandardInput);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await output);
    }

    [Fact]
    public async Task Helper_group_kill_does_not_reach_the_caller()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var info = new ProcessStartInfo("/bin/sh", ["-c", "kill -9 0"]) { UseShellExecute = false };
        using var process = NonInteractiveProcess.Start(info)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(137, process.ExitCode); // The helper killed only its own group; this test process is alive to assert.
    }
}
