using System.Text;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class JobLogsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Short_header_after_daemon_kill_does_not_break_listing_and_is_repaired_on_append(int bytes)
    {
        using var f = new JobFixture();
        var job = f.Submit("interrupted-log");
        var state = Path.GetDirectoryName(f.DatabasePath)!;
        var directory = Path.Combine(state, "logs");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, job.JobId + ".log"), new byte[bytes]);
        var logs = new JobLogs(state);

        Assert.Equal(job.JobId, Assert.Single(new ListJobs(f.Store, JobFixture.Operator, logs)
            .Execute(new ListJobsRequest()).Page!.Jobs).JobId);
        Assert.Null(logs.LastActivity(job.JobId, "fake"));

        var write = logs.BeginRun(job.JobId, "run-2", "fake");
        write("stdout", "resumed\n"u8.ToArray());
        Assert.Contains("resumed", logs.Read(job.JobId).Text);
    }

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
        var mcp = endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobGet, JobId = job.JobId });
        Assert.Null(mcp.Job!.Instruction);
        var web = endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobGet, JobId = job.JobId, IncludeInstruction = true });
        Assert.NotNull(web.Job!.Instruction);
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
    public void Last_activity_is_the_reported_reason_not_the_trailing_stderr_line()
    {
        using var f = new JobFixture();
        var logs = new JobLogs(Path.GetDirectoryName(f.DatabasePath)!);
        var write = logs.BeginRun("job", "run", "pi");
        write("stderr", "No API key found for the selected model.\nUse /login. See:\n  /opt/pi/docs/models.md\n"u8.ToArray());
        Assert.Equal("/opt/pi/docs/models.md", logs.LastActivity("job", "pi"));

        write("status", "Pi has no login or API key for the selected model; run `pi` and /login.\n"u8.ToArray());

        Assert.StartsWith("Pi has no login", logs.LastActivity("job", "pi"), StringComparison.Ordinal);
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
        chunk[^1] = (byte)'\n';
        for (var i = 0; i < JobLogs.MaxLogBytes / chunk.Length + 2; i++)
        {
            write("stdout", chunk);
        }
        write("stderr", "tail"u8.ToArray());
        write("stderr", ReadOnlyMemory<byte>.Empty);
        var written = logs.Read("job").EndOffset;

        var path = Path.Combine(state, "logs", "job.log");
        var bodyLength = new FileInfo(path).Length - 29;
        Assert.InRange(bodyLength, JobLogs.TrimmedLogBytes, JobLogs.MaxLogBytes);
        var read = logs.Read("job", 0, 16);
        Assert.True(read.Truncated);
        Assert.Equal(written, read.EndOffset);
        Assert.Equal(read.EndOffset - bodyLength, read.StartOffset);
        Assert.Equal(read.StartOffset + 16, read.NextOffset);
        Assert.Equal("tail\n", logs.Read("job", read.EndOffset - 5).Text);
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
