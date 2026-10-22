using System.Net;
using System.Text;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

/// <summary>`atf login` against a scripted device-link server (PRFactory device-link contract).</summary>
public sealed class LoginTests
{
    const string Token = "prfw_secret-worker-token";
    const string Url = "https://prfactory.example.test";

    static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static HttpResponseMessage Code() => Json(HttpStatusCode.OK, """
        {"device_code":"dev-code-1","user_code":"WDJB-MJHT","verification_uri":"https://prfactory.example.test/link",
         "verification_uri_complete":"https://prfactory.example.test/link?code=WDJB-MJHT","expires_in":600,"interval":5}
        """);

    static HttpResponseMessage Error(string code, string? extra = null) =>
        Json(HttpStatusCode.BadRequest, "{\"error\":\"" + code + "\",\"error_description\":\"Text for " + code + ".\"" + (extra ?? "") + "}");

    static HttpResponseMessage Linked() => Json(HttpStatusCode.OK, "{\"access_token\":\"" + Token + "\",\"token_type\":\"Bearer\","
        + "\"worker_token_id\":\"7d5c\",\"tenant_id\":\"t1\",\"tenant_name\":\"Acme\",\"approved_by\":\"anna.berg@acme.example\",\"machine_name\":\"box\"}");

    sealed class Server(params Func<HttpResponseMessage>[] tokenReplies) : HttpMessageHandler
    {
        int next;
        public List<(string Path, string Body, string? ContentType)> Requests { get; } = [];
        public Func<HttpResponseMessage> CodeReply { get; init; } = Code;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.AbsolutePath, body, request.Content?.Headers.ContentType?.MediaType));
            return request.RequestUri.AbsolutePath == "/api/device-link/code" ? CodeReply() : tokenReplies[Math.Min(next++, tokenReplies.Length - 1)]();
        }
    }

    sealed record Run(int Exit, string Output, string Error, List<TimeSpan> Waits, List<string> Opened);

    static async Task<Run> Login(StateDirectory state, Server server, Dictionary<string, string>? options = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var waits = new List<TimeSpan>();
        var opened = new List<string>();
        var exit = await PRFactoryLogin.RunAsync(state, options ?? new() { ["url"] = Url }, output, error, server, opened.Add,
            (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        return new(exit, output.ToString(), error.ToString(), waits, opened);
    }

    [Fact]
    public async Task Pending_then_slow_down_then_success_saves_an_owner_only_token_and_names_the_approver()
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(dir.Path);
        var server = new Server(() => Error("authorization_pending"), () => Error("slow_down", ",\"interval\":10"), () => Error("authorization_pending"), Linked);

        var run = await Login(state, server);

        Assert.Equal(0, run.Exit);
        Assert.Equal([5, 5, 10, 10], run.Waits.Select(w => (int)w.TotalSeconds));
        Assert.Contains("WDJB-MJHT", run.Output, StringComparison.Ordinal);
        Assert.Contains("Linked to Acme as anna.berg@acme.example.", run.Output, StringComparison.Ordinal);
        Assert.Equal(["https://prfactory.example.test/link?code=WDJB-MJHT"], run.Opened);
        Assert.DoesNotContain(Token, run.Output + run.Error, StringComparison.Ordinal);
        var code = server.Requests[0];
        Assert.Equal("application/x-www-form-urlencoded", code.ContentType);
        Assert.Contains("client_id=atf", code.Body, StringComparison.Ordinal);
        Assert.All(server.Requests.Skip(1), r => Assert.Contains("device_code=dev-code-1", r.Body, StringComparison.Ordinal));

        Assert.Equal(Token, PRFactoryConnection.ReadToken(state));
        var settings = PRFactoryConnection.LoadSettings(state)!;
        Assert.Equal(Url, settings.Url);
        Assert.True(settings.TenantWideToken);
        Assert.Equal("Acme", PRFactoryConnection.LoadLink(state)!.TenantName);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir.Path, "prfactory.token")));
        }
    }

    [Fact]
    public async Task Slow_down_without_an_interval_adds_five_seconds_up_to_sixty()
    {
        using var dir = new TempStateDir();
        var server = new Server([.. Enumerable.Repeat<Func<HttpResponseMessage>>(() => Error("slow_down"), 13), Linked]);

        var run = await Login(StateDirectory.Open(dir.Path), server);

        Assert.Equal(0, run.Exit);
        Assert.Equal(60, (int)run.Waits.Max().TotalSeconds);
        Assert.Equal([5, 10, 15, 20], run.Waits.Take(4).Select(w => (int)w.TotalSeconds));
    }

    [Theory]
    [InlineData("expired_token")]
    [InlineData("access_denied")]
    [InlineData("invalid_grant")]
    public async Task A_final_error_stops_and_saves_nothing(string code)
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(dir.Path);

        var run = await Login(state, new Server(() => Error(code)));

        Assert.Equal(1, run.Exit);
        Assert.Contains("Text for " + code + ". Run atf login again.", run.Error, StringComparison.Ordinal);
        Assert.Null(PRFactoryConnection.LoadSettings(state));
        Assert.False(File.Exists(Path.Combine(dir.Path, "prfactory.token")));
    }

    [Fact]
    public async Task Too_many_requests_waits_the_retry_after()
    {
        using var dir = new TempStateDir();
        var limited = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        };

        var run = await Login(StateDirectory.Open(dir.Path), new Server(limited, Linked) { CodeReply = Code });

        Assert.Equal(0, run.Exit);
        Assert.Equal([5, 7], run.Waits.Select(w => (int)w.TotalSeconds));
    }

    [Fact]
    public async Task A_machine_connected_to_another_server_is_not_relinked()
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(dir.Path);
        Assert.Equal(0, PRFactoryConnection.Run(state, "connect", new Dictionary<string, string> { ["url"] = "https://other.example.test", ["token-scope"] = "tenant-wide" },
            [], new StringReader("other-token")));
        var server = new Server(Linked);

        var run = await Login(state, server);

        Assert.Equal(1, run.Exit);
        Assert.Empty(server.Requests);
        Assert.Equal("other-token", PRFactoryConnection.ReadToken(state));
    }

    [Fact]
    public async Task Connect_reuses_the_linked_credential_only_for_its_server()
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(dir.Path);
        using var repo = new TempStateDir();
        Assert.Equal(0, (await Login(state, new Server(Linked))).Exit);
        var repository = Guid.NewGuid();

        Assert.Equal(0, PRFactoryConnection.Run(state, "connect", new Dictionary<string, string>(), ["prfactory", "connect", "--repo", $"{repository:D}={repo.Path}"],
            new StringReader("")));

        Assert.Equal(Token, PRFactoryConnection.ReadToken(state));
        var settings = PRFactoryConnection.LoadSettings(state)!;
        Assert.Equal(Url, settings.Url);
        Assert.Equal(repository, Assert.Single(settings.Repositories).Id);
        Assert.NotNull(PRFactoryConnection.LoadLink(state));

        // Another server never receives the linked token: without a token of its own the connect fails.
        Assert.Equal(64, PRFactoryConnection.Run(state, "connect", new Dictionary<string, string> { ["url"] = "https://other.example.test" },
            ["prfactory", "connect", "--repo", $"{repository:D}={repo.Path}"], new StringReader("")));
        Assert.Equal(Url, PRFactoryConnection.LoadSettings(state)!.Url);
    }

    [Fact]
    public async Task No_browser_and_a_plain_http_page_never_open_a_browser()
    {
        using var dir = new TempStateDir();
        var run = await Login(StateDirectory.Open(dir.Path), new Server(Linked), new() { ["url"] = Url, ["no-browser"] = "true" });
        Assert.Equal(0, run.Exit);
        Assert.Empty(run.Opened);

        using var other = new TempStateDir();
        var plain = new Server(Linked)
        {
            CodeReply = () => Json(HttpStatusCode.OK, """
                {"device_code":"d","user_code":"WDJB-MJHT","verification_uri":"http://evil.example/link","verification_uri_complete":"http://evil.example/link?code=x","expires_in":600,"interval":5}
                """),
        };
        Assert.Empty((await Login(StateDirectory.Open(other.Path), plain)).Opened);
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task No_wait_outlasts_the_code()
    {
        using var dir = new TempStateDir();
        var clock = new Clock();
        var limited = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        };
        var waits = new List<TimeSpan>();
        var error = new StringWriter();

        var exit = await PRFactoryLogin.RunAsync(StateDirectory.Open(dir.Path), new Dictionary<string, string> { ["url"] = Url, ["no-browser"] = "true" },
            new StringWriter(), error, new Server(limited), _ => { }, (wait, _) => { waits.Add(wait); clock.Now += wait; return Task.CompletedTask; }, clock, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Contains("expired", error.ToString(), StringComparison.Ordinal);
        Assert.True(waits.Aggregate(TimeSpan.Zero, (a, b) => a + b) <= TimeSpan.FromSeconds(600));
    }

    [Fact]
    public async Task A_token_bound_to_another_server_is_not_read_for_this_one()
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(dir.Path);
        Assert.Equal(0, (await Login(state, new Server(Linked))).Exit);

        Assert.Equal(Token, PRFactoryConnection.ReadToken(state, Url));
        Assert.Null(PRFactoryConnection.ReadToken(state, "https://other.example.test"));
    }

    [Fact]
    public async Task Connect_without_a_repository_reuses_the_tenant_wide_link()
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(dir.Path);
        Assert.Equal(0, (await Login(state, new Server(Linked))).Exit);

        Assert.Equal(0, PRFactoryConnection.Run(state, "connect", new Dictionary<string, string> { ["github-user"] = "alice" }, ["prfactory", "connect"],
            new StringReader("")));
        Assert.Equal("alice", PRFactoryConnection.LoadSettings(state)!.GitHubUser);
        Assert.Equal(Token, PRFactoryConnection.ReadToken(state, Url));
    }
}
