using System.Net;
using System.Text;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class ConnectorTests
{
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

    static StateDirectory Connected(TempStateDir dir)
    {
        var state = StateDirectory.Open(dir.Path);
        var id = Guid.NewGuid();
        var options = new Dictionary<string, string>
        {
            ["url"] = "https://example.test",
            ["token"] = "test-worker-token"
        };
        Assert.Equal(0, PRFactoryConnection.Run(state, "connect", options, ["--repo", $"{id:D}={dir.Path}"]));
        Assert.Equal(dir.Path, Assert.Single(PRFactoryConnection.LoadSettings(state)!.Repositories).Directory);
        return state;
    }

    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
}
