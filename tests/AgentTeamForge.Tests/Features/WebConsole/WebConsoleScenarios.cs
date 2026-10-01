using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.WebConsole;

/// <summary>Real daemon and CLI processes with HTTP against the daemon-hosted console (fake backend only).</summary>
[Trait("Category", "Scenario")]
public sealed class WebConsoleScenarios
{
    [Fact]
    public async Task Two_lead_sessions_render_as_live_teams_in_an_isolated_browser()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true",
            "Chromium on GitHub runners is the snap wrapper and cannot render headless here");
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/chromium"))
        {
            return;
        }
        var chromiumTarget = new FileInfo("/usr/bin/chromium").ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        Assert.SkipUnless(
            chromiumTarget is null || !chromiumTarget.StartsWith("/snap/", StringComparison.Ordinal),
            "Chromium resolves under /snap and cannot render headless here");
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var port = FreePort();
        ConfigurePort(rig.StateDir, port);
        await rig.StartDaemonAsync();
        var (_, first) = await rig.StartBridgeAsync("web-first");
        var (_, second) = await rig.StartBridgeAsync("web-second");
        var firstSession = (await SpikeRig.CallAsync(first, "session_info", [])).Session!;
        var secondSession = (await SpikeRig.CallAsync(second, "session_info", [])).Session!;
        Assert.NotEqual(firstSession.SessionId, secondSession.SessionId);
        foreach (var (bridge, key) in new[] { (first, "web-one"), (second, "web-two") })
        {
            var submitted = await SpikeRig.CallAsync(bridge, "submit_job", new()
            {
                ["backend"] = "fake",
                ["idempotency_key"] = key,
                ["instruction"] = key,
                ["hold"] = true,
            });
            Assert.True(submitted.Ok);
        }
        var (exit, output, _) = await rig.RunToExitAsync(["web", "--state-dir", rig.StateDir]);
        Assert.Equal(0, exit);
        var url = output.Trim()["url ".Length..];
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new Uri(url).Fragment["#token=".Length..]);
        var listed = await Bounded.Until(async () =>
        {
            var json = await http.GetStringAsync("api/jobs", TestContext.Current.CancellationToken);
            var page = JsonSerializer.Deserialize(json, IpcJson.Default.IpcResponse)?.Page;
            return page?.Jobs.Count(j => j.LeadSessionId is not null) == 2 ? page : null;
        }, "two web teams");
        Assert.Equal(2, listed.Jobs.Select(j => j.LeadSessionId).Distinct().Count());

        async Task<string> DumpAsync(string target)
        {
            var chromium = new ProcessStartInfo("/usr/bin/chromium")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[]
            {
                "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
                "--no-first-run", "--no-default-browser-check", "--disable-background-networking",
                "--virtual-time-budget=7000", "--dump-dom", "--user-data-dir=" + Path.Combine(rig.StateDir, "browser-" + Guid.NewGuid().ToString("N")[..6]), target,
            })
            {
                chromium.ArgumentList.Add(arg);
            }
            using var browser = Process.Start(chromium)!;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var domTask = browser.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderrTask = browser.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var timedOut = false;
            try
            {
                await browser.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                timedOut = true;
            }
            finally
            {
                if (!browser.HasExited)
                {
                    try
                    {
                        OwnedProcessTermination.Kill(browser);
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited between the check and kill.
                    }
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await browser.WaitForExitAsync(stop.Token);
                }
            }
            var dom = await domTask;
            var stderr = await stderrTask;
            if (timedOut)
            {
                throw new TimeoutException($"Chromium did not finish rendering the web console within 30 seconds. Exit code: {browser.ExitCode}. Stderr: {stderr}. Partial DOM: {dom}");
            }
            Assert.True(browser.ExitCode == 0, $"Chromium exited with code {browser.ExitCode}. Stderr: {stderr}");
            return dom;
        }

        // Chat is the default view; the card dashboard is one tab away and renders as before.
        var chatDom = await DumpAsync(url);
        Assert.Contains("class=\"row lead-row", chatDom, StringComparison.Ordinal);
        var dom = await DumpAsync(url + "&v=jobs");
        Assert.Contains("Lead " + firstSession.SessionId[..8], dom, StringComparison.Ordinal);
        Assert.Contains("Lead " + secondSession.SessionId[..8], dom, StringComparison.Ordinal);
        Assert.Contains("class=\"team-toggle\"", dom, StringComparison.Ordinal);
        Assert.Contains("class=\"team-content\"", dom, StringComparison.Ordinal);

        // Drive the chat composer in a real page over the DevTools protocol: type a follow-up and send it.
        var profile = Path.Combine(rig.StateDir, "browser-composer");
        var driven = new ProcessStartInfo("/usr/bin/chromium") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[]
        {
            "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--no-first-run",
            "--no-default-browser-check", "--remote-debugging-port=0", "--user-data-dir=" + profile, url,
        })
        {
            driven.ArgumentList.Add(arg);
        }
        using var drivenBrowser = Process.Start(driven)!;
        _ = drivenBrowser.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        _ = drivenBrowser.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            var debugPort = await Bounded.Until(
                () => Task.FromResult(File.Exists(portFile) && File.ReadAllLines(portFile) is [{ Length: > 0 } line, ..] ? line : null),
                "chromium debugging port");
            using var devtools = new HttpClient();
            var socketUrl = await Bounded.Until(async () =>
            {
                var targets = JsonDocument.Parse(await devtools.GetStringAsync($"http://127.0.0.1:{debugPort}/json/list", TestContext.Current.CancellationToken));
                return targets.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() == "page")
                    .Select(t => t.GetProperty("webSocketDebuggerUrl").GetString()).FirstOrDefault();
            }, "chromium page target");
            using var socket = new System.Net.WebSockets.ClientWebSocket();
            await socket.ConnectAsync(new Uri(socketUrl), TestContext.Current.CancellationToken);
            var script = """
                (async () => {
                  const wait = async (ok) => { for (let i = 0; i < 150; i++) { const v = ok(); if (v) return v; await new Promise(r => setTimeout(r, 100)); } return null; };
                  if (!await wait(() => document.getElementById('composer')?.hidden === false && document.getElementById('target-name').textContent)) return 'no composer';
                  const draft = document.getElementById('draft');
                  draft.value = 'composer follow-up';
                  draft.dispatchEvent(new Event('input', { bubbles: true }));
                  document.getElementById('send').click();
                  return await wait(() => /^Queued/.test(document.getElementById('composer-status').textContent) && document.getElementById('composer-status').textContent) || 'not queued: ' + document.getElementById('composer-status').textContent;
                })()
                """;
            string? status = null;
            var buffer = new byte[64 * 1024];
            // The first navigation may still be settling; retry while the execution context is replaced.
            for (var attempt = 1; status is null; attempt++)
            {
                var request = $$$$"""{"id":{{{{attempt}}}},"method":"Runtime.evaluate","params":{"expression":"{{{{JsonEncodedText.Encode(script)}}}}","awaitPromise":true,"returnByValue":true}}""";
                await socket.SendAsync(Encoding.UTF8.GetBytes(request), System.Net.WebSockets.WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
                while (status is null)
                {
                    using var message = new MemoryStream();
                    System.Net.WebSockets.WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
                        message.Write(buffer, 0, received.Count);
                    }
                    while (!received.EndOfMessage);
                    var reply = JsonDocument.Parse(message.ToArray()).RootElement;
                    if (!reply.TryGetProperty("id", out var id) || id.GetInt32() != attempt)
                    {
                        continue;
                    }

                    if (!reply.TryGetProperty("result", out var evaluated))
                    {
                        Assert.True(attempt < 20, "protocol error: " + reply.GetRawText());
                        await Task.Delay(250, TestContext.Current.CancellationToken);
                        break;
                    }

                    status = evaluated.GetProperty("result").TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : "evaluate failed: " + reply.GetRawText();
                }
            }

            Assert.True(status.StartsWith("Queued", StringComparison.Ordinal), status);
            // The follow-up reached the daemon as a new job chained to the held one.
            await Bounded.Until(async () =>
            {
                var json = await http.GetStringAsync("api/jobs", TestContext.Current.CancellationToken);
                return json.Contains("\"parent_job_id\":\"", StringComparison.Ordinal) ? json : null;
            }, "composer follow-up job");
        }
        finally
        {
            if (!drivenBrowser.HasExited)
            {
                OwnedProcessTermination.Kill(drivenBrowser);
            }
        }
    }

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
    public async Task Web_command_uses_default_state_directory_for_all_forms()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempStateDir();
        var statePath = temp.File("agentteamforge");
        StateDirectory.CreatePrivateDirectory(statePath);
        var openerDir = temp.File("bin");
        Directory.CreateDirectory(openerDir);
        var opener = Path.Combine(openerDir, OperatingSystem.IsMacOS() ? "open" : "xdg-open");
        File.WriteAllText(opener, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(opener, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        async Task<string> Run(params string[] args)
        {
            var info = new ProcessStartInfo(SpikeRig.Binary)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("web");
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }
            info.Environment["XDG_STATE_HOME"] = temp.Path;
            info.Environment["PATH"] = openerDir + Path.PathSeparator + info.Environment["PATH"];
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await error);
            return (await output).Trim();
        }

        var link = await Run();
        Assert.StartsWith("url http://127.0.0.1:8765/#token=", link, StringComparison.Ordinal);
        Assert.Equal(link, await Run("--open"));
        var rotated = await Run("--rotate-token");
        Assert.StartsWith("url http://127.0.0.1:8765/#token=", rotated, StringComparison.Ordinal);
        Assert.NotEqual(link, rotated);
        Assert.Equal(rotated["url http://127.0.0.1:8765/#token=".Length..], File.ReadAllText(Path.Combine(statePath, "web-console.key")));
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

    [Fact]
    public async Task Test_daemon_without_launch_mode_does_not_bind_the_default_console_port()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();

        var listening = Assert.Single(rig.DaemonLog, line => line.Contains("web console listening on http://127.0.0.1:", StringComparison.Ordinal));
        Assert.DoesNotContain(":8765", listening, StringComparison.Ordinal);
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
