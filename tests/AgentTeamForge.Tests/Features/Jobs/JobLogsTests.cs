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
            new StopJob(f.Store, JobFixture.Operator, _ => { }), new DurabilityCheckpoints(null), () => { }, logs: logs);
        var response = endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobOutput, JobId = job.JobId, Offset = 0 });
        Assert.True(response.Ok);
        Assert.Contains("hello\n", response.Output!.Text);
        Assert.Contains("warning\n", response.Output.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(response.Output.Text), Convert.FromBase64String(response.Output.DataBase64));

        var denied = new JobsEndpoint(accept, f.Get(new("someone-else", "spike-team", "fake-agent")),
            new FollowUpJob(f.Store, JobFixture.Operator, accept), f.List(), new StopJob(f.Store, JobFixture.Operator, _ => { }), new DurabilityCheckpoints(null), () => { }, logs: logs)
            .Handle(new IpcRequest { Op = IpcProtocol.JobOutput, JobId = job.JobId });
        Assert.False(denied.Ok);
        Assert.Equal(JobErrors.NotFound, denied.Error);
    }

    [Fact]
    public void Cap_trims_to_a_bounded_tail_and_keeps_absolute_offsets()
    {
        using var f = new JobFixture();
        var state = Path.GetDirectoryName(f.DatabasePath)!;
        var logs = new JobLogs(state);
        var write = logs.BeginRun("job", "run", "codex");
        write("stdout", "first\n"u8.ToArray());
        var chunk = new byte[JobLogs.MaxReadBytes];
        Array.Fill(chunk, (byte)'x');
        var written = logs.Read("job").EndOffset;
        for (var i = 0; i < JobLogs.MaxLogBytes / chunk.Length + 2; i++)
        {
            write("stdout", chunk);
            written += chunk.Length;
        }
        write("stderr", "tail"u8.ToArray());
        written += "\n[stderr]\ntail".Length;

        var path = Path.Combine(state, "logs", "job.log");
        var bodyLength = new FileInfo(path).Length - 29;
        Assert.InRange(bodyLength, JobLogs.TrimmedLogBytes, JobLogs.MaxLogBytes);
        var read = logs.Read("job", 0, 16);
        Assert.True(read.Truncated);
        Assert.Equal(written, read.EndOffset);
        Assert.Equal(read.EndOffset - bodyLength, read.StartOffset);
        Assert.Equal(read.StartOffset + 16, read.NextOffset);
        Assert.Equal("tail", logs.Read("job", read.EndOffset - 4).Text);
        Assert.DoesNotContain("first", logs.Read("job", read.StartOffset).Text);
    }

    [Fact]
    public void Activity_call_checks_job_access_and_pages_plain_capture()
    {
        using var f = new JobFixture();
        var job = f.Submit("activity");
        var logs = new JobLogs(Path.GetDirectoryName(f.DatabasePath)!, plainOutput: true);
        var write = logs.BeginRun(job.JobId, "run-1", "claude");
        write("stdout", "first\nsecond\n"u8.ToArray());
        var accept = f.Accept();
        var endpoint = new JobsEndpoint(accept, f.Get(), new FollowUpJob(f.Store, JobFixture.Operator, accept), f.List(),
            new StopJob(f.Store, JobFixture.Operator, _ => { }), new DurabilityCheckpoints(null), () => { }, logs: logs);
        var first = endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobActivity, JobId = job.JobId, Limit = 1 });
        Assert.True(first.Ok);
        Assert.Equal("first", Assert.Single(first.Activity!.Entries).Text);
        var next = endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobActivity, JobId = job.JobId, AfterCursor = first.Activity.NextCursor, Limit = 1 });
        Assert.Equal("second", Assert.Single(next.Activity!.Entries).Text);

        var denied = new JobsEndpoint(accept, f.Get(new("someone-else", "spike-team", "fake-agent")),
            new FollowUpJob(f.Store, JobFixture.Operator, accept), f.List(), new StopJob(f.Store, JobFixture.Operator, _ => { }),
            new DurabilityCheckpoints(null), () => { }, logs: logs)
            .Handle(new IpcRequest { Op = IpcProtocol.JobActivity, JobId = job.JobId });
        Assert.Equal(JobErrors.NotFound, denied.Error);
    }
}
