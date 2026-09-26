using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.WebConsole;

/// <summary>Real processes: daemon, the separate <c>atf web</c> client role, and HTTP against it (fake backend only).</summary>
[Trait("Category", "Scenario")]
public sealed class WebConsoleScenarios
{
    [Fact]
    public async Task Web_console_lists_gets_and_follows_up_through_the_daemon()
    {
        using var rig = new SpikeRig();
        using var processes = new OwnedProcesses();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var first = await rig.ClientAsync("submit", "--key", "turn-1", "--instruction", "first", "--backend", "fake", "--cwd", rig.StateDir);
        var parent = (await rig.WaitForStatusAsync(first.Job!.JobId, JobStatus.Completed)).Job!;

        var info = new ProcessStartInfo(SpikeRig.Binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "web", "--state-dir", rig.StateDir, "--port", "0" })
        {
            info.ArgumentList.Add(arg);
        }

        var web = processes.Start(info);
        web.ErrorDataReceived += (_, _) => { };
        web.BeginErrorReadLine();
        var url = await ReadField(web, "url");
        var token = await ReadField(web, "token");
        Assert.NotEqual(rig.Credential, token);

        using var http = new HttpClient { BaseAddress = new Uri(url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var raw = new List<string>();
        async Task<IpcResponse> Call(HttpRequestMessage request)
        {
            using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            raw.Add(text);
            return JsonSerializer.Deserialize(text, IpcJson.Default.IpcResponse)!;
        }

        var listed = await Call(new HttpRequestMessage(HttpMethod.Get, "api/jobs"));
        Assert.Equal([parent.JobId], listed.Page!.Jobs.Select(j => j.JobId));

        var post = new HttpRequestMessage(HttpMethod.Post, $"api/jobs/{parent.JobId}/follow-up")
        {
            Content = new StringContent("""{"instruction":"second","idempotency_key":"web-1"}""", Encoding.UTF8, "application/json"),
        };
        post.Headers.Add("Origin", url.TrimEnd('/'));
        var next = await Call(post);
        Assert.Equal("accepted", next.Outcome);

        var child = await Bounded.Until(async () =>
        {
            var got = await Call(new HttpRequestMessage(HttpMethod.Get, $"api/jobs/{next.Job!.JobId}"));
            return got.Job is { Status: JobStatus.Completed } job ? job : null;
        }, "follow-up completion");
        Assert.Equal((parent.JobId, parent.SessionId, "fake-result: second"), (child.ParentJobId, child.SessionId, child.Result));
        Assert.DoesNotContain(raw, text => text.Contains(rig.Credential, StringComparison.Ordinal));

        // Stopping the web role does not touch accepted work or the daemon.
        OwnedProcesses.KillAbruptly(web);
        Assert.Equal(JobStatus.Completed, (await rig.GetAsync(child.JobId)).Job!.Status);
    }

    [Fact]
    public async Task Web_console_refuses_a_missing_port_and_never_starts_a_daemon()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();

        var (exit, _, _) = await rig.RunToExitAsync(["web", "--state-dir", rig.StateDir]);

        Assert.Equal(64, exit);
        Assert.False(File.Exists(rig.SocketPath));
    }

    static async Task<string> ReadField(Process process, string name)
    {
        using var deadline = new CancellationTokenSource(Bounded.ScenarioDeadline);
        var line = await process.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new InvalidOperationException("web exited");
        Assert.StartsWith(name + " ", line, StringComparison.Ordinal);
        return line[(name.Length + 1)..];
    }
}
