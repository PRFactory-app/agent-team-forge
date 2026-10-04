using AgentTeamForge.Business.Features.Processes;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Wake;

public sealed class CodexQueueExecutableTests
{
    [Fact]
    public void Real_0160_receipt_is_parsed_for_the_expected_thread()
    {
        // Captured from our isolated, idle 0.160.0 TUI, not an owner session.
        const string output = "Queued message 01a10678-1cfa-7830-be6c-8ed826e034d9 for thread 01a10677-1d6a-7063-af22-c137b5d654b2.\n";
        Assert.Equal("01a10678-1cfa-7830-be6c-8ed826e034d9", CodexQueueWake.SubmissionId(output, "01a10677-1d6a-7063-af22-c137b5d654b2"));
        Assert.Null(CodexQueueWake.SubmissionId(output, "different-thread"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_helpers_preserve_output_and_timeout_without_credentials(bool timeout)
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var dir = new TempStateDir();
        var executable = dir.File("codex");
        File.WriteAllText(executable, "#!/bin/sh\necho queue-started\necho 'token=private-credential queue-rejected' >&2\n" + (timeout ? "exec sleep 30\n" : "exit 7\n"));
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = await CodexQueueWake.SubmitAsync("scratch", dir.Path, "test", executable, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(result.Started);
        Assert.Null(result.SubmissionId);
        Assert.Contains("queue-started", result.Diagnostic);
        Assert.Contains("queue-rejected", result.Diagnostic);
        Assert.DoesNotContain("private-credential", result.Diagnostic);
        Assert.Contains($"timeout={timeout}", result.Diagnostic);
        if (!timeout) { Assert.Contains("exit_code=7", result.Diagnostic); }
    }

    [Fact]
    public void Diagnostic_tails_are_bounded_and_redacted_before_truncation()
    {
        var result = CodexQueueWake.Diagnostic(1, false, new string('x', 9000), "Bearer secret-value sk-secret123 api_key=private \"token\": \"Bearer quoted-credential\" password: Basic basic-credential", "failed");
        Assert.True(result.Length < 2400);
        Assert.DoesNotContain("secret-value", result);
        Assert.DoesNotContain("sk-secret123", result);
        Assert.DoesNotContain("private", result);
        Assert.DoesNotContain("quoted-credential", result);
        Assert.DoesNotContain("basic-credential", result);
    }

    [Fact]
    public async Task Unparsed_success_and_start_failure_both_have_diagnostics()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var dir = new TempStateDir();
        var executable = dir.File("codex");
        File.WriteAllText(executable, "#!/bin/sh\necho unexpected-receipt\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = await CodexQueueWake.SubmitAsync("scratch", dir.Path, "test", executable, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(result.Started);
        Assert.Null(result.SubmissionId);
        Assert.Contains("exit_code=0", result.Diagnostic);
        Assert.Contains("receipt_missing", result.Diagnostic);
        Assert.Contains("unexpected-receipt", result.Diagnostic);
        result = await CodexQueueWake.SubmitAsync("scratch", dir.Path, "test", dir.File("missing"), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(result.Started);
        Assert.Contains("exit_code=unknown", result.Diagnostic);
    }

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
