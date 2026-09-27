using System.Net;
using System.Text;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class ConnectorTests
{
    [Theory]
    [InlineData("tenant-wide", null, 0)]
    [InlineData("tenant-wide", "false", 64)]
    [InlineData("repository", null, 64)]
    public void Scratch_only_connection_requires_declared_tenant_scope_and_enabled_repo_less(string scope, string? enabled, int exitCode)
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(dir.Path);
        var options = new Dictionary<string, string> { ["url"] = "https://example.test", ["token-scope"] = scope };
        if (enabled is not null) { options["repo-less"] = enabled; }
        Assert.Equal(exitCode, PRFactoryConnection.Run(state, "connect", options, [], new StringReader("fake-token")));
        if (exitCode == 0)
        {
            var settings = PRFactoryConnection.LoadSettings(state)!;
            Assert.True(settings.TenantWideToken);
            Assert.True(settings.RepoLess);
            Assert.Empty(settings.Repositories);
        }
        else { Assert.Null(PRFactoryConnection.LoadSettings(state)); }
    }

    [Fact]
    public async Task Disabled_by_default_makes_no_http_request()
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(dir.Path);
        using var stop = new CancellationTokenSource();
        var created = 0;
        await PRFactoryHeartbeat.RunAsync(state, stop.Token,
            () => { created++; return new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)); },
            (_, _) => { stop.Cancel(); return Task.CompletedTask; });
        Assert.Equal(0, created);
        Assert.Null(PRFactoryConnection.LoadSettings(state));
    }

    [Fact]
    public async Task Rejected_token_stops_registration_until_explicit_reconnect()
    {
        using var dir = new TempStateDir();
        var state = Connected(dir);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        await PRFactoryHeartbeat.RunAsync(state, stop.Token,
            () => new FakeHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.Unauthorized); }),
            (_, _) =>
            {
                if (PRFactoryConnection.IsRejected(state))
                {
                    stop.Cancel();
                }
                return Task.CompletedTask;
            });
        Assert.Equal(1, calls);
        Assert.True(PRFactoryConnection.IsRejected(state));
        Assert.NotNull(PRFactoryConnection.LoadSettings(state));
    }

    [Fact]
    public async Task Heartbeat_failures_back_off_without_new_registration()
    {
        using var dir = new TempStateDir();
        var state = Connected(dir);
        using var stop = new CancellationTokenSource();
        var registrationCount = 0;
        var heartbeatCount = 0;
        var delays = new List<TimeSpan>();
        await PRFactoryHeartbeat.RunAsync(state, stop.Token,
            () => new FakeHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/register", StringComparison.Ordinal))
                {
                    registrationCount++;
                    var capabilities = System.Text.Json.JsonElement.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                        .GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToArray();
                    Assert.Equal(["authority-disposition-v1", "remote-publication-v1", "workspace-continuity-v1", "blob-attachments-v1", "base-wip-v1"], capabilities);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"machineId\":\"8ad6f5c0-a4f0-42dc-8c29-59677ea37949\",\"heartbeatIntervalSeconds\":1}", Encoding.UTF8, "application/json")
                    };
                }
                heartbeatCount++;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }),
            (span, _) =>
            {
                delays.Add(span);
                if (heartbeatCount == 2)
                {
                    stop.Cancel();
                }
                return Task.CompletedTask;
            });
        Assert.Equal(1, registrationCount);
        Assert.Equal(2, heartbeatCount);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public void Token_file_is_owner_only_and_unsafe_modes_are_rejected()
    {
        using var dir = new TempStateDir();
        var state = Connected(dir);
        var tokenPath = dir.File("prfactory.token");
        Assert.Equal(StateDirectory.PrivateFile, File.GetUnixFileMode(tokenPath));
        Assert.Equal("test-worker-token", PRFactoryConnection.ReadToken(state));
        File.SetUnixFileMode(tokenPath, StateDirectory.PrivateFile | UnixFileMode.GroupRead);
        Assert.Equal("private_file_unsafe", Assert.Throws<StateDirectoryException>(() => PRFactoryConnection.ReadToken(state)).Code);
    }

    [Fact]
    public void Token_is_refused_on_the_command_line()
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(dir.Path);
        var options = new Dictionary<string, string> { ["url"] = "https://example.test", ["token"] = "argv-token" };
        Assert.Equal(64, PRFactoryConnection.Run(state, "connect", options,
            ["--repo", $"{Guid.NewGuid():D}={dir.Path}"], new StringReader("stdin-token\n")));
        Assert.False(File.Exists(dir.File("prfactory.token")));
        Assert.Null(PRFactoryConnection.LoadSettings(state));
    }

    [Fact]
    public async Task Rejection_of_a_replaced_token_does_not_mark_the_new_token()
    {
        using var dir = new TempStateDir();
        var state = Connected(dir);
        using var stop = new CancellationTokenSource();
        var tokens = new List<string>();
        await PRFactoryHeartbeat.RunAsync(state, stop.Token,
            () => new FakeHandler(request =>
            {
                tokens.Add(request.Headers.Authorization!.Parameter!);
                if (tokens.Count == 1)
                {
                    Connect(state, dir, "new-worker-token"); // Reconnect lands while the old request is in flight.
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
                stop.Cancel();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"machineId\":\"8ad6f5c0-a4f0-42dc-8c29-59677ea37949\",\"heartbeatIntervalSeconds\":1}", Encoding.UTF8, "application/json")
                };
            }),
            (_, _) => Task.CompletedTask);
        Assert.Equal(["test-worker-token", "new-worker-token"], tokens);
        Assert.False(PRFactoryConnection.IsRejected(state));
    }

    static StateDirectory Connected(TempStateDir dir)
    {
        var state = StateDirectory.Open(dir.Path);
        Connect(state, dir, "test-worker-token");
        Assert.Equal(dir.Path, Assert.Single(PRFactoryConnection.LoadSettings(state)!.Repositories).Directory);
        return state;
    }

    static void Connect(StateDirectory state, TempStateDir dir, string token)
    {
        var options = new Dictionary<string, string> { ["url"] = "https://example.test" };
        Assert.Equal(0, PRFactoryConnection.Run(state, "connect", options,
            ["--repo", $"{Guid.NewGuid():D}={dir.Path}"], new StringReader(token + "\n")));
    }

    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
}
