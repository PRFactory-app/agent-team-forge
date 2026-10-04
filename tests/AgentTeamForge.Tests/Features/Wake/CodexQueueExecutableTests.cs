using AgentTeamForge.Business.Features.Processes;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Wake;

public sealed class CodexQueueExecutableTests
{
    [Fact]
    public void Queue_bypasses_the_mise_wrapper_just_like_job_launch()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var dir = new TempStateDir();
        var wrapper = dir.File("codex");
        File.WriteAllText(wrapper, "#!/bin/sh\nexec mise x codex -- codex \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var native = dir.File("native-codex");
        File.WriteAllBytes(native, [0x7f, (byte)'E', (byte)'L', (byte)'F']);

        var executable = CodexQueueWake.QueueExecutable(name => ToolExecutable.Resolve(name, dir.Path, tool =>
        {
            Assert.Equal("codex", tool);
            return native;
        }));

        Assert.Equal(native, executable);
    }
}
