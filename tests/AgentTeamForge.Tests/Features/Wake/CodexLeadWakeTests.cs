using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Host.Features.Wake;
using AgentTeamForge.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Tests.Features.Wake;

public sealed class CodexLeadWakeTests
{
    [Fact]
    public async Task Freshly_verified_lead_is_queued_and_validation_failures_are_distinct()
    {
        using var dir = new TempStateDir();
        var thread = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(dir.File("sessions"));
        File.WriteAllText(dir.File($"sessions/rollout-2026-10-04-{thread}.jsonl"), "");
        var target = new WakeRegistration("codex:" + thread, 1, "codex", thread, "", dir.Path);
        var calls = 0;
        IWakePoster poster = new CodexQueueWake(queue: (registration, notice, _) =>
        {
            Assert.Equal(target, registration);
            calls++;
            return Task.FromResult(true);
        });
        Assert.Equal(WakePost.Ok, await poster.PostWithReasonAsync(target, "notice", TestContext.Current.CancellationToken));
        Assert.Equal("thread_unverified", await poster.PostWithReasonAsync(target with { Address = Guid.NewGuid().ToString("D") }, "notice", TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Resolved_codex_host_registers_a_verified_thread()
    {
        using var dir = new TempStateDir();
        var thread = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(dir.File("sessions"));
        System.IO.File.WriteAllText(dir.File($"sessions/rollout-2026-09-30-{thread}.jsonl"), "");
        var request = HostSessionWake.ForCodexLead(thread, (42, "codex"), pid =>
        {
            Assert.Equal(42, pid);
            return dir.Path;
        });
        Assert.NotNull(request);
        Assert.Equal("codex:" + thread, request.WakeKey);
        Assert.Equal(dir.Path, request.WakeHome);
        Assert.Equal(thread, request.WakeAddress);
    }

    [Fact]
    public void Registration_rejects_untrusted_hosts_homes_and_threads()
    {
        using var dir = new TempStateDir();
        var thread = Guid.NewGuid().ToString("D");
        var failures = new List<string>();
        Assert.Null(HostSessionWake.ForCodexLead(thread, null, _ => throw new InvalidOperationException(), failures.Add));
        Assert.Null(HostSessionWake.ForCodexLead(thread, (42, "claude"), _ => throw new InvalidOperationException(), failures.Add));
        Assert.Null(HostSessionWake.ForCodexLead(thread, (42, "codex"), _ => null, failures.Add));
        Assert.Null(HostSessionWake.ForCodexLead(thread, (42, "codex"), _ => dir.Path, failures.Add));
        Assert.Equal(new[] { "no Codex host ancestor", "no Codex host ancestor", "no trusted Codex home",
            "thread not verified under " + dir.Path }, failures);
        Assert.All(failures, message => Assert.DoesNotContain(thread, message));
    }

    [Fact]
    public void Non_linux_home_matches_daemon_resolution()
    {
        if (OperatingSystem.IsLinux()) { return; }
        Assert.Equal(CodexPaths.Home(Environment.GetEnvironmentVariable, Environment.CurrentDirectory), HostSessionWake.CodexHome(42));
    }

    [Theory]
    [InlineData("codex.exe", null)]
    [InlineData("Codex.exe", null)]
    [InlineData("node.exe", @"node C:\Users\me\node_modules\@openai\codex\bin\codex.js")]
    public void Windows_codex_ancestor_is_recognized(string image, string? command)
    {
        var rows = new Dictionary<int, WindowsHostAncestry.Row>
        {
            [10] = new(11, "atf.exe"),
            [11] = new(12, "cmd.exe"),
            [12] = new(1, image, command)
        };
        Assert.Equal((12, "codex"), WindowsHostAncestry.Resolve(10, rows));
    }

    [Fact]
    public void Windows_thread_rollout_must_be_under_sessions_on_the_same_root()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var dir = new TempStateDir();
        var thread = Guid.NewGuid().ToString("D");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dir.File("state_5.sqlite") }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE threads(id TEXT, rollout_path TEXT, archived INTEGER); INSERT INTO threads VALUES ($id, $path, 0)";
        command.Parameters.AddWithValue("$id", thread);
        var otherDrive = Path.GetPathRoot(dir.Path)!.StartsWith("Z:", StringComparison.OrdinalIgnoreCase) ? "Y:" : "Z:";
        command.Parameters.AddWithValue("$path", otherDrive + @"\elsewhere\rollout.jsonl");
        command.ExecuteNonQuery();
        var target = new WakeRegistration("codex:" + thread, 0, "codex", thread, "", dir.Path);
        Assert.False(CodexQueueWake.VerifyCodexThread(target));
        command.CommandText = "UPDATE threads SET rollout_path=$path";
        command.Parameters["$path"].Value = dir.File("sessions/2026/rollout.jsonl");
        command.ExecuteNonQuery();
        Assert.True(CodexQueueWake.VerifyCodexThread(target));
        connection.Close();
        SqliteConnection.ClearPool(connection);
        using var verifier = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dir.File("state_5.sqlite"),
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 1
        }.ToString());
        SqliteConnection.ClearPool(verifier);
    }
}
