using System.Diagnostics;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Setup;

public sealed class NativeAotScriptTests
{
    [Theory]
    [InlineData("release-build.sh", "Darwin", "x86_64", true, true)]
    [InlineData("verify.sh", "Darwin", "x86_64", true, true)]
    [InlineData("release-build.sh", "Darwin", "x86_64", false, true)]
    [InlineData("verify.sh", "Darwin", "x86_64", false, true)]
    [InlineData("release-build.sh", "Darwin", "arm64", true, false)]
    [InlineData("verify.sh", "Darwin", "arm64", true, false)]
    [InlineData("release-build.sh", "Darwin", "arm64", false, true)]
    [InlineData("verify.sh", "Darwin", "arm64", false, true)]
    [InlineData("release-build.sh", "Linux", "x86_64", false, false)]
    [InlineData("verify.sh", "Linux", "x86_64", false, false)]
    public async Task PublishSelectsLinkerForHost(string script, string os, string arch, bool fullXcode, bool useNewLinker)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // These are Unix build scripts.
        }

        using var temp = new TempStateDir();
        var root = FindRoot();
        var scripts = Directory.CreateDirectory(temp.File("scripts")).FullName;
        var tools = Directory.CreateDirectory(temp.File("tools")).FullName;
        File.Copy(Path.Combine(root, "scripts", script), Path.Combine(scripts, script));
        File.Copy(Path.Combine(root, "global.json"), temp.File("global.json"));
        var pin = System.Text.Json.JsonDocument.Parse(File.ReadAllText(temp.File("global.json")));
        using (pin)
        {
            WriteTool("dotnet", $"""
                case "$1" in
                  --version) echo '{pin.RootElement.GetProperty("sdk").GetProperty("version").GetString()}' ;;
                  format) echo 'Formatted 0 of 1 files.' ;;
                  publish) printf '%s\n' "$@" > "$ATF_PUBLISH_ARGS"; exit 42 ;;
                esac
                """);
        }
        WriteTool("uname", $"case \"$1\" in -s) echo {os};; -m) echo {arch};; esac");
        WriteTool("xcodebuild", fullXcode ? "echo 'Xcode 16.4'" : "exit 1");

        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(scripts, script));
        if (script == "release-build.sh")
        {
            start.ArgumentList.Add("0.1.7-rc1");
            start.ArgumentList.Add(temp.File("release"));
        }
        start.Environment["DOTNET"] = Path.Combine(tools, "dotnet");
        start.Environment["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment.Remove("RID");
        start.Environment["ATF_PUBLISH_ARGS"] = temp.File("publish-args");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 42, $"{await output}\n{await error}");
        var args = File.ReadAllLines(temp.File("publish-args"));
        Assert.Equal(useNewLinker, args.Contains("-p:UseLdClassicXCodeLinker=false", StringComparer.Ordinal));

        void WriteTool(string name, string body)
        {
            var path = Path.Combine(tools, name);
            File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentTeamForge.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found");
    }
}
