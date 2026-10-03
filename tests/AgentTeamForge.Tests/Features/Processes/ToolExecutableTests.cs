using AgentTeamForge.Business.Features.Processes;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Processes;

public sealed class ToolExecutableTests
{
    const string Wrapper = "#!/bin/bash\nexport MISE_MINIMUM_RELEASE_AGE=0\nmise use -g \"tool\" || exit 1\nexec mise x \"tool\" -- \"tool\" \"$@\"\n";
    static readonly byte[] Elf = [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0];

    [Fact]
    public void A_mise_wrapper_on_path_resolves_to_the_native_binary_mise_names()
    {
        using var dir = new TempStateDir();
        var (bin, wrapper) = PathWithWrapper(dir);
        var real = dir.File("real-tool");
        File.WriteAllBytes(real, Elf);

        Assert.Equal(real, ToolExecutable.Resolve("tool", bin, name => name == "tool" ? "mise progress\n" + real + "\n" : null));
        // mise failing or not answering keeps the PATH hit.
        Assert.Equal(wrapper, ToolExecutable.Resolve("tool", bin, _ => null));
        Assert.Equal(wrapper, ToolExecutable.Resolve("tool", bin, _ => throw new TimeoutException()));
    }

    [Fact]
    public void A_mise_answer_that_is_the_wrapper_again_is_rejected()
    {
        using var dir = new TempStateDir();
        var (bin, wrapper) = PathWithWrapper(dir);
        var link = dir.File("tool-link");
        File.CreateSymbolicLink(link, wrapper);
        var otherWrapper = dir.File("other-wrapper");
        File.WriteAllText(otherWrapper, Wrapper);

        Assert.Equal(wrapper, ToolExecutable.Resolve("tool", bin, _ => link));
        Assert.Equal(wrapper, ToolExecutable.Resolve("tool", bin, _ => otherWrapper));
        Assert.Equal(wrapper, ToolExecutable.Resolve("tool", bin, _ => "relative/tool"));
    }

    [Fact]
    public void A_launcher_script_that_is_not_a_mise_wrapper_is_accepted()
    {
        using var dir = new TempStateDir();
        var (bin, _) = PathWithWrapper(dir);
        var launcher = dir.File("cursor-agent");
        File.WriteAllText(launcher, "#!/usr/bin/env bash\nexec node \"$(dirname \"$0\")/index.js\" \"$@\"\n");

        Assert.Equal(launcher, ToolExecutable.Resolve("tool", bin, _ => launcher));
    }

    [Fact]
    public void A_native_binary_on_path_is_used_without_asking_mise()
    {
        using var dir = new TempStateDir();
        var bin = dir.File("bin");
        Directory.CreateDirectory(bin);
        var native = Path.Combine(bin, "tool");
        File.WriteAllBytes(native, Elf);
        MakeExecutable(native);

        Assert.Equal(native, ToolExecutable.Resolve("tool", bin, _ => throw new InvalidOperationException("mise must not run")));
        Assert.Equal("missing", ToolExecutable.Resolve("missing", bin, _ => throw new InvalidOperationException("mise must not run")));
    }

    [Fact]
    public void A_failed_mise_lookup_is_retried_on_the_next_call()
    {
        using var dir = new TempStateDir();
        var (bin, wrapper) = PathWithWrapper(dir);
        var real = dir.File("real-tool");
        File.WriteAllBytes(real, Elf);
        var answer = (string?)null;

        Assert.Equal(wrapper, ToolExecutable.Resolve("tool", bin, _ => answer));
        answer = real;
        Assert.Equal(real, ToolExecutable.Resolve("tool", bin, _ => answer));
    }

    [Fact]
    public void A_user_launcher_script_on_path_is_used_as_is()
    {
        using var dir = new TempStateDir();
        var bin = dir.File("bin");
        Directory.CreateDirectory(bin);
        var launcher = Path.Combine(bin, "tool");
        File.WriteAllText(launcher, "#!/bin/sh\nexport TOOL_CONFIG=mine\nexec /opt/tool/bin/tool \"$@\"\n");
        MakeExecutable(launcher);

        Assert.Equal(launcher, ToolExecutable.Resolve("tool", bin, _ => throw new InvalidOperationException("mise must not run")));
    }

    [Fact]
    public void A_non_executable_file_earlier_on_path_is_skipped()
    {
        using var dir = new TempStateDir();
        var first = dir.File("first");
        var second = dir.File("second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllBytes(Path.Combine(first, "tool"), Elf);
        var native = Path.Combine(second, "tool");
        File.WriteAllBytes(native, Elf);
        MakeExecutable(native);

        Assert.Equal(native, ToolExecutable.Resolve("tool", first + Path.PathSeparator + second, _ => null));
    }

    static (string Bin, string Wrapper) PathWithWrapper(TempStateDir dir)
    {
        var bin = dir.File("bin");
        Directory.CreateDirectory(bin);
        var wrapper = Path.Combine(bin, "tool");
        File.WriteAllText(wrapper, Wrapper);
        MakeExecutable(wrapper);
        return (bin, wrapper);
    }

    static void MakeExecutable(string file) =>
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
