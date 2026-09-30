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

    [Fact]
    public async Task Non_executable_path_entry_does_not_mask_a_later_executable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("atf-path-").FullName;
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
            File.WriteAllText(Path.Combine(first, "atf-probe"), "#!/bin/sh\nexit 1\n");
            File.SetUnixFileMode(Path.Combine(first, "atf-probe"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(Path.Combine(second, "atf-probe"), "#!/bin/sh\nexit 7\n");
            File.SetUnixFileMode(Path.Combine(second, "atf-probe"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var info = new ProcessStartInfo("atf-probe") { UseShellExecute = false };
            info.Environment["PATH"] = $"{first}:{second}:/usr/bin:/bin";
            using var process = NonInteractiveProcess.Start(info)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(7, process.ExitCode);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
