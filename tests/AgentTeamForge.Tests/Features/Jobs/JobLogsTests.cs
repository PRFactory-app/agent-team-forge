using System.Text;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class JobLogsTests
{
    [Fact]
    public void Raw_output_is_available_through_authorized_endpoint_while_job_is_running()
    {
        using var f = new JobFixture();
        var job = f.Submit("logged");
        var state = Path.GetDirectoryName(f.DatabasePath)!;
        var logs = new JobLogs(state);
        var write = logs.BeginRun(job.JobId, "run-1", "codex");
        write("stdout", "hello\n"u8.ToArray());
        write("stderr", "warning\n"u8.ToArray());

        var accept = f.Accept();
        var endpoint = new JobsEndpoint(accept, f.Get(), new FollowUpJob(f.Store, JobFixture.Operator, accept), f.List(),
            new DurabilityCheckpoints(null), () => { }, logs: logs);
        var response = endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobOutput, JobId = job.JobId, Offset = 0 });
        Assert.True(response.Ok);
        Assert.Contains("hello\n", response.Output!.Text);
        Assert.Contains("warning\n", response.Output.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(response.Output.Text), Convert.FromBase64String(response.Output.DataBase64));

        var denied = new JobsEndpoint(accept, f.Get(new("someone-else", "spike-team", "fake-agent")),
            new FollowUpJob(f.Store, JobFixture.Operator, accept), f.List(), new DurabilityCheckpoints(null), () => { }, logs: logs)
            .Handle(new IpcRequest { Op = IpcProtocol.JobOutput, JobId = job.JobId });
        Assert.False(denied.Ok);
        Assert.Equal(JobErrors.NotFound, denied.Error);
    }

    [Fact]
    public void Follow_up_appends_to_parent_and_child_and_cap_keeps_absolute_tail_offsets()
    {
        using var f = new JobFixture();
        var state = Path.GetDirectoryName(f.DatabasePath)!;
        var logs = new JobLogs(state);
        logs.BeginRun("parent", "first", "codex")("stdout", "first\n"u8.ToArray());
        var child = logs.BeginRun("child", "second", "codex", ["parent"]);
        child("stdout", "second\n"u8.ToArray());
        Assert.Contains("first\n", logs.Read("parent").Text);
        Assert.Contains("second\n", logs.Read("parent").Text);
        Assert.DoesNotContain("first\n", logs.Read("child").Text);
        Assert.Contains("second\n", logs.Read("child").Text);

        var large = new byte[JobLogs.MaxLogBytes];
        Array.Fill(large, (byte)'x');
        child("stdout", large);
        child("stderr", "tail"u8.ToArray());
        var read = logs.Read("parent", 0, 16);
        Assert.True(read.Truncated);
        Assert.True(read.StartOffset > 0);
        Assert.Equal(read.StartOffset + 16, read.NextOffset);
        Assert.Equal(JobLogs.MaxLogBytes, new FileInfo(Path.Combine(state, "logs", "parent.log")).Length - 29);
        Assert.Equal("tail", logs.Read("parent", read.EndOffset - 4).Text);
    }
}
