using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.WebConsole;

/// <summary>Real daemon and CLI processes with HTTP against the daemon-hosted console (fake backend only).</summary>
[Trait("Category", "Scenario")]
public sealed class WebConsoleScenarios
{
    [Fact]
    public async Task Web_console_lists_gets_and_follows_up_through_the_daemon()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var port = FreePort();
        ConfigurePort(rig.StateDir, port);
        await rig.StartDaemonAsync();
        var first = await rig.ClientAsync("submit", "--key", "turn-1", "--instruction", "first", "--backend", "fake", "--cwd", rig.StateDir);
        var parent = (await rig.WaitForStatusAsync(first.Job!.JobId, JobStatus.Completed)).Job!;

        var (exit, output, _) = await rig.RunToExitAsync(["web", "--state-dir", rig.StateDir]);
        Assert.Equal(0, exit);
        var url = Assert.Single(output.Trim().Split('\n'))["url ".Length..];
        var token = new Uri(url).Fragment["#token=".Length..];
        Assert.Equal(port, new Uri(url).Port);
        Assert.NotEqual(rig.Credential, token);
        Assert.Equal("#token=" + token, new Uri(url).Fragment);
        Assert.Equal(string.Empty, new Uri(url).Query);

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
        Assert.Equal("grey", listed.Page.Jobs[0].Light);
        Assert.Contains("\"light\":\"grey\"", raw[^1], StringComparison.Ordinal);

        var post = new HttpRequestMessage(HttpMethod.Post, $"api/jobs/{parent.JobId}/follow-up")
        {
            Content = new StringContent("""{"instruction":"second","idempotency_key":"web-1"}""", Encoding.UTF8, "application/json"),
        };
        post.Headers.Add("Origin", new Uri(url).GetLeftPart(UriPartial.Authority));
        var next = await Call(post);
        Assert.Equal("accepted", next.Outcome);

        var child = await Bounded.Until(async () =>
        {
            var got = await Call(new HttpRequestMessage(HttpMethod.Get, $"api/jobs/{next.Job!.JobId}"));
            return got.Job is { Status: JobStatus.Completed } job ? job : null;
        }, "follow-up completion");
        Assert.Equal((parent.JobId, parent.SessionId, "fake-result: second"), (child.ParentJobId, child.SessionId, child.Result));
        Assert.DoesNotContain(raw, text => text.Contains(rig.Credential, StringComparison.Ordinal));

        var (rotateExit, rotatedOutput, _) = await rig.RunToExitAsync(["web", "--state-dir", rig.StateDir, "--rotate-token"]);
        Assert.Equal(0, rotateExit);
        var rotatedUrl = Assert.Single(rotatedOutput.Trim().Split('\n'))["url ".Length..];
        var rotatedToken = new Uri(rotatedUrl).Fragment["#token=".Length..];
        Assert.NotEqual(token, rotatedToken);
        using (var rejected = await http.GetAsync("api/jobs", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotatedToken);
        Assert.True((await Call(new HttpRequestMessage(HttpMethod.Get, "api/jobs"))).Ok);
        Assert.Equal(JobStatus.Completed, (await rig.GetAsync(child.JobId)).Job!.Status);
    }

    [Fact]
    public async Task Web_command_prints_default_link_without_starting_a_daemon()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();

        var (exit, output, _) = await rig.RunToExitAsync(["web", "--state-dir", rig.StateDir]);

        Assert.Equal(0, exit);
        Assert.StartsWith("url http://127.0.0.1:8765/#token=", output, StringComparison.Ordinal);
        var (againExit, againOutput, _) = await rig.RunToExitAsync(["web", "--state-dir", rig.StateDir]);
        Assert.Equal((exit, output), (againExit, againOutput));
        var tokenFile = Path.Combine(rig.StateDir, "web-console.key");
        Assert.Equal(43, File.ReadAllText(tokenFile).Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tokenFile));
        }
        Assert.False(File.Exists(rig.SocketPath));
    }

    [Fact]
    public async Task Occupied_web_port_does_not_stop_jobs()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        ConfigurePort(rig.StateDir, port);
        await rig.StartDaemonAsync();

        var submitted = await rig.SubmitAsync("port-taken", "still works");
        Assert.Equal(JobStatus.Completed, (await rig.WaitForStatusAsync(submitted.Job!.JobId, JobStatus.Completed)).Job!.Status);
        Assert.Contains(rig.DaemonLog, line => line.Contains($"web console unavailable on 127.0.0.1:{port}", StringComparison.Ordinal));
    }

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static void ConfigurePort(string stateDir, int port)
    {
        var path = Path.Combine(stateDir, "launch-mode.json");
        File.WriteAllText(path, $$"""{"mode":"headless","web_port":{{port}}}""");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
