using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

/// <summary>pull-request-v1: the worker opens the pull request with gh; fake gh, no network.</summary>
public sealed class PullRequestTests
{
    const string Url = "https://pullrequest.example.test";
    const string Sha = "0123456789abcdef0123456789abcdef01234567";
    const string Token = "gho_FAKEFAKEFAKEFAKEFAKEFAKEFAKE0123";
    const string Remote = "https://github.com/mikaelliljedahl/atf-demo.git";
    const string BbRemote = "https://x-token-auth@bitbucket.org/ws/atf-demo.git";
    const string BbToken = "ATCTT3xFfGN0FAKEFAKEFAKEFAKEFAKEFAKE0123";
    const string AzRemote = "https://mliljedahl@dev.azure.com/mliljedahl/Sandbox/_git/GitSandbox";
    static readonly string AzToken = new string('f', 76) + "AZDO" + "abcd";

    sealed class Fixture : IDisposable
    {
        readonly TempStateDir dir = new();
        public Guid Repo = Guid.NewGuid();
        public Guid Source = Guid.NewGuid();
        public PRFactoryWorkItem Item;
        public List<ProcessSpec> Calls = [];
        public List<string> Capabilities = ["pull-request-v1"];
        public List<string> PollQueries = [];
        public List<JsonElement> Completions = [];
        public List<JsonElement> Failures = [];
        public List<string> Logs = [];
        public string RemoteHead = Sha;
        public bool ExistingPr;
        public string PrHead = Sha;
        public string HeadBranch;
        public string? CleanupWipBranch;
        public string[] ForkEntries = [];
        public bool CreateHidden;
        public string Limits = "";
        public PRFactoryWorkItem? Ordinary;
        public int OrdinaryClaims;

        public static string Entry(int number, string sha, bool cross = false, string owner = "mikaelliljedahl") =>
            "{\"number\":" + number + ",\"url\":\"https://github.com/mikaelliljedahl/atf-demo/pull/" + number + "\",\"headRefOid\":\"" + sha
            + "\",\"headRefName\":\"prfactory/PRF-1\",\"baseRefName\":\"main\",\"isCrossRepository\":" + (cross ? "true" : "false")
            + ",\"headRepository\":{\"name\":\"atf-demo\"},\"headRepositoryOwner\":{\"login\":\"" + owner + "\"}}";
        public PRFactoryPublicationStore Publications;

        public Fixture(string headBranch = "prfactory/PRF-1", string remote = Remote, bool publish = true, string sha = Sha, string baseBranch = "main")
        {
            HeadBranch = headBranch;
            var db = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
            Publications = new PRFactoryPublicationStore(db);
            if (publish)
            {
                Publications.Verify(new PublicationIntent("pub-1", Url, Source, Guid.NewGuid(), "machine", "job", Repo.ToString("D"),
                    "key", "/lead", remote, "internal", headBranch, sha, sha, null));
            }
            Item = new PRFactoryWorkItem
            {
                Id = Guid.NewGuid(),
                Type = "pullRequestCreate",
                RepositoryId = Repo,
                LeaseToken = Guid.NewGuid(),
                ContextJson = JsonSerializer.Serialize(new PRFactoryPullRequestRequest("pull-request-create", 1, Source, Repo,
                    headBranch, sha, baseBranch, "PRF-1: Title", "Body text"), PRFactoryWorkItemJson.Default.PRFactoryPullRequestRequest)
            };
        }

        public string TokenStdoutPrefix = "";
        public string TokenStderr = "";
        public string? TokenOverride;
        public Exception? TokenThrows;

        public Task<ProcessResult> Gh(ProcessSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            var args = string.Join(' ', spec.Args);
            if (args.StartsWith("auth token", StringComparison.Ordinal) && TokenThrows is not null) { return Task.FromException<ProcessResult>(TokenThrows); }
            if (args.StartsWith("auth token", StringComparison.Ordinal)) { return Task.FromResult(new ProcessResult(0, TokenStdoutPrefix + (TokenOverride ?? Token) + "\n", TokenStderr)); }
            if (args.StartsWith("api ", StringComparison.Ordinal)) { return Task.FromResult(new ProcessResult(0, RemoteHead + "\n", "")); }
            if (args.StartsWith("pr list", StringComparison.Ordinal))
            {
                var list = "[" + string.Join(',', ForkEntries.Concat(ExistingPr && !CreateHidden ? [Entry(7, PrHead)] : [])) + "]";
                return Task.FromResult(new ProcessResult(0, list.Replace("prfactory/PRF-1", HeadBranch, StringComparison.Ordinal), ""));
            }
            if (args.EndsWith("credential fill", StringComparison.Ordinal) && spec.Stdin!.Contains("host=dev.azure.com", StringComparison.Ordinal))
            {
                // A plain store helper keeps the credential without a path, so a path-aware query finds nothing.
                return Task.FromResult(AzPathless == spec.Stdin.Contains("path=", StringComparison.Ordinal)
                    ? new ProcessResult(128, "", "fatal: could not read Username for 'https://dev.azure.com': terminal prompts disabled\n")
                    : new ProcessResult(0, "protocol=https\nhost=dev.azure.com\nusername=mliljedahl\npassword=" + AzToken + "\n", ""));
            }
            if (args == "credential fill") { return Task.FromResult(new ProcessResult(0, "protocol=https\r\nhost=bitbucket.org\r\nusername=x-token-auth\r\npassword=" + BbToken + "\r\n", "")); }
            if (args.StartsWith("pr create", StringComparison.Ordinal)) { ExistingPr = true; return Task.FromResult(new ProcessResult(0, "https://github.com/x/y/pull/7\n", "")); }
            throw new InvalidOperationException(args);
        }

        public HttpResponseMessage Reply(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/worker/capabilities") { return Json("{\"capabilities\":[" + string.Join(',', Capabilities.Select(c => $"\"{c}\"")) + "]}"); }
            if (path.EndsWith("/poll", StringComparison.Ordinal))
            {
                PollQueries.Add(request.RequestUri.Query);
                var items = new[] { Ordinary, Item }.OfType<PRFactoryWorkItem>()
                    .Select(i => JsonSerializer.Serialize(i, PRFactoryWorkItemJson.Default.PRFactoryWorkItem));
                return Json("{\"workItems\":[" + string.Join(',', items) + "]" + Limits + "}");
            }
            if (path.Contains("/claim/", StringComparison.Ordinal))
            {
                if (Ordinary is not null && path.EndsWith(Ordinary.Id.ToString("D"), StringComparison.Ordinal)) { OrdinaryClaims++; }
                return Json("{\"workItem\":" + JsonSerializer.Serialize(Item, PRFactoryWorkItemJson.Default.PRFactoryWorkItem) + "}");
            }
            if (path.Contains("/complete/", StringComparison.Ordinal))
            {
                Completions.Add(JsonElement.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
                return Json(JsonSerializer.Serialize(new PRFactoryCompletionResponse(true, CleanupWipBranch),
                    PRFactoryWorkItemJson.Default.PRFactoryCompletionResponse));
            }
            if (path.Contains("/fail/", StringComparison.Ordinal))
            {
                Failures.Add(JsonElement.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
                return Json("{\"acknowledged\":true}");
            }
            throw new InvalidOperationException(path);
        }

        public List<(HttpMethod Method, string PathAndQuery, string? Auth, string Body)> BbRequests = [];
        public HttpStatusCode BbStatus = HttpStatusCode.OK;

        public static string BbEntry(int id, string hash, string repo = "ws/atf-demo") =>
            "{\"id\":" + id + ",\"links\":{\"html\":{\"href\":\"https://bitbucket.org/ws/atf-demo/pull-requests/" + id + "\"}},"
            + "\"source\":{\"branch\":{\"name\":\"prfactory/PRF-1\"},\"commit\":{\"hash\":\"" + hash + "\"},\"repository\":{\"full_name\":\"" + repo + "\"}},"
            + "\"destination\":{\"branch\":{\"name\":\"main\"}}}";

        public HttpResponseMessage Bitbucket(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            BbRequests.Add((request.Method, uri.PathAndQuery, request.Headers.Authorization?.ToString(),
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? ""));
            if (BbStatus != HttpStatusCode.OK)
            {
                return new(BbStatus) { Content = new StringContent("{\"error\":{\"message\":\"Token invalid: " + BbToken + "\"}}") };
            }
            Assert.Equal("api.bitbucket.org", uri.Host);
            if (uri.AbsolutePath == "/2.0/repositories/ws/atf-demo/refs/branches/prfactory/PRF-1") { return Json("{\"target\":{\"hash\":\"" + RemoteHead + "\"}}"); }
            if (uri.AbsolutePath == "/2.0/repositories/ws/atf-demo/pullrequests" && request.Method == HttpMethod.Get)
            {
                var list = ForkEntries.Concat(ExistingPr ? [BbEntry(7, PrHead[..12])] : []);
                return Json("{\"values\":[" + string.Join(',', list) + "]}");
            }
            if (uri.AbsolutePath == "/2.0/repositories/ws/atf-demo/pullrequests" && request.Method == HttpMethod.Post)
            {
                ExistingPr = true;
                return Json(BbEntry(7, Sha[..12]));
            }
            throw new InvalidOperationException(uri.ToString());
        }

        public bool AzPathless;
        public List<(HttpMethod Method, string PathAndQuery, string? Auth, string Body)> AzRequests = [];

        public static string AzEntry(int id, string sha, bool fork = false) =>
            "{\"pullRequestId\":" + id + ",\"url\":\"https://dev.azure.com/mliljedahl/_apis/git/repositories/guid/pullRequests/" + id + "\","
            + "\"sourceRefName\":\"refs/heads/prfactory/PRF-1\",\"targetRefName\":\"refs/heads/main\","
            + "\"lastMergeSourceCommit\":{\"commitId\":\"" + sha + "\"}" + (fork ? ",\"forkSource\":{\"name\":\"refs/heads/prfactory/PRF-1\"}" : "") + "}";

        public HttpResponseMessage Azure(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            AzRequests.Add((request.Method, uri.PathAndQuery, request.Headers.Authorization?.ToString(),
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? ""));
            if (BbStatus != HttpStatusCode.OK)
            {
                return new(BbStatus) { Content = new StringContent("{\"message\":\"TF400813: token " + AzToken + " is not authorized\"}") };
            }
            Assert.Equal("Suppress", request.Headers.GetValues("X-TFS-FedAuthRedirect").Single());
            Assert.Contains("api-version=7.1", uri.Query, StringComparison.Ordinal);
            const string repo = "/mliljedahl/Sandbox/_apis/git/repositories/GitSandbox/";
            if (uri.AbsolutePath == repo + "refs")
            {
                Assert.Contains("filter=heads%2Fprfactory%2FPRF-1", uri.Query, StringComparison.Ordinal);
                // The filter is a prefix match: a longer branch name comes back too.
                return Json("{\"value\":[{\"name\":\"refs/heads/prfactory/PRF-10\",\"objectId\":\"" + Sha + "\"},"
                    + "{\"name\":\"refs/heads/prfactory/PRF-1\",\"objectId\":\"" + RemoteHead + "\"}]}");
            }
            if (uri.AbsolutePath == repo + "pullrequests" && request.Method == HttpMethod.Get)
            {
                var list = ForkEntries.Concat(ExistingPr ? [AzEntry(7, PrHead)] : []);
                return Json("{\"value\":[" + string.Join(',', list) + "]}");
            }
            if (uri.AbsolutePath == repo + "pullrequests" && request.Method == HttpMethod.Post)
            {
                ExistingPr = true;
                return Json(AzEntry(7, Sha));
            }
            throw new InvalidOperationException(uri.ToString());
        }

        HttpResponseMessage Rest(HttpRequestMessage request) =>
            request.RequestUri!.Host == "dev.azure.com" ? Azure(request) : Bitbucket(request);

        public PRFactoryClient Client() => new(PRFactoryClient.CreateHttpClient(Url, "token", new Handler(Reply)));

        public PRFactoryPullRequests Executor(string? user = "mikaelliljedahl") =>
            new(Url, [new RepositoryMapping(Repo, dir.Path)], user, Client(), Publications, Logs.Add, Gh, new Handler(Rest));

        public Task Handle(string? user = "mikaelliljedahl") => Executor(user).HandleAsync(Item, CancellationToken.None);

        public void Dispose() => dir.Dispose();

        static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completion_preserves_the_PR_head_and_other_publication_branches(bool cleanupHead)
    {
        const string head = "wip/machine/PRF-1";
        const string other = "wip/machine/earlier-publication";
        using var f = new Fixture(head);
        var receipt = Assert.Single(f.Publications.VerifiedFor(Url, f.Source, f.Repo));
        f.Publications.Verify(receipt with { PublicationId = "pub-2", PublishBranch = other });
        f.CleanupWipBranch = cleanupHead ? head : other;

        await f.Handle();

        Assert.Single(f.Completions);
        Assert.Empty(f.Failures);
        Assert.Contains(f.Logs, line => line.Contains("protected publication or PR head branch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_gh_that_times_out_fails_the_work_item_visibly_instead_of_deferring()
    {
        using var f = new Fixture { TokenThrows = new TimeoutException("gh did not finish") };
        await f.Handle();

        var failure = Assert.Single(f.Failures);
        Assert.Contains("looping wrapper", failure.GetProperty("errorMessage").GetString());
        Assert.Empty(f.Completions);
    }

    [Fact]
    public async Task Creates_the_pull_request_with_the_named_accounts_token_and_never_leaks_it()
    {
        using var f = new Fixture();
        await f.Handle();

        var tokenCall = f.Calls[0];
        Assert.Equal(["auth", "token", "--user", "mikaelliljedahl"], tokenCall.Args);
        var create = Assert.Single(f.Calls, c => c.Args.Take(2).SequenceEqual(["pr", "create"]));
        Assert.Equal(Token, create.Env!["GH_TOKEN"]);
        Assert.Equal("Body text", create.Stdin);
        Assert.Equal("mikaelliljedahl/atf-demo", create.Args[Array.IndexOf(create.Args, "--repo") + 1]);
        Assert.DoesNotContain(f.Calls, c => c.Args.Any(a => a.Contains(Token, StringComparison.Ordinal)));
        Assert.DoesNotContain(f.Logs, l => l.Contains(Token, StringComparison.Ordinal));

        var completion = Assert.Single(f.Completions);
        Assert.Equal("prfactory/PRF-1", completion.GetProperty("resultBranch").GetString());
        Assert.Equal(Sha, completion.GetProperty("resultCommitSha").GetString());
        var result = JsonElement.Parse(completion.GetProperty("resultMarkdown").GetString()!);
        Assert.Equal(7, result.GetProperty("number").GetInt32());
        Assert.True(result.GetProperty("created").GetBoolean());
        Assert.Empty(f.Failures);
    }

    [Fact]
    public async Task Noise_on_gh_stderr_does_not_affect_the_token()
    {
        using var f = new Fixture { TokenStderr = "mise by @jdx - installing 1 tool\nmise gh@2.102.0 already installed\n" };
        await f.Handle();

        Assert.Equal(Token, Assert.Single(f.Calls, c => c.Args.Take(2).SequenceEqual(["pr", "create"])).Env!["GH_TOKEN"]);
        Assert.Empty(f.Failures);
    }

    [Fact]
    public async Task Noise_before_the_token_on_gh_stdout_is_skipped_and_the_last_line_is_the_token()
    {
        using var f = new Fixture { TokenStdoutPrefix = "mise by @jdx - installing 1 tool\nmise gh@2.102.0 already installed\n" };
        await f.Handle();

        Assert.Equal(Token, Assert.Single(f.Calls, c => c.Args.Take(2).SequenceEqual(["pr", "create"])).Env!["GH_TOKEN"]);
        Assert.Empty(f.Failures);
    }

    [Fact]
    public async Task A_garbage_last_stdout_line_fails_clearly_without_echoing_it()
    {
        using var f = new Fixture { TokenStdoutPrefix = Token + "\n", TokenOverride = "mise done installing" };
        await f.Handle();

        Assert.DoesNotContain(f.Calls, c => c.Args.Take(1).SequenceEqual(["pr"]) || c.Args.Take(1).SequenceEqual(["api"]));
        var message = Assert.Single(f.Failures).GetProperty("errorMessage").GetString()!;
        Assert.Contains("plain token", message);
        Assert.DoesNotContain("mise", message);
        Assert.DoesNotContain(Token, message);
    }

    [Fact]
    public async Task A_plain_word_as_the_last_stdout_line_is_not_accepted_as_a_token()
    {
        using var f = new Fixture { TokenOverride = "done" };
        await f.Handle();

        Assert.DoesNotContain(f.Calls, c => c.Args.Take(1).SequenceEqual(["pr"]) || c.Args.Take(1).SequenceEqual(["api"]));
        Assert.Contains("plain token", Assert.Single(f.Failures).GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task An_existing_open_pull_request_is_returned_without_creating_another()
    {
        using var f = new Fixture { ExistingPr = true };
        await f.Handle();

        Assert.DoesNotContain(f.Calls, c => c.Args.Take(2).SequenceEqual(["pr", "create"]));
        var result = JsonElement.Parse(Assert.Single(f.Completions).GetProperty("resultMarkdown").GetString()!);
        Assert.False(result.GetProperty("created").GetBoolean());
        Assert.Equal("https://github.com/mikaelliljedahl/atf-demo/pull/7", result.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Account_comes_from_the_remote_username_and_is_never_the_active_gh_account()
    {
        using var withUser = new Fixture(remote: "https://mikaelliljedahl@github.com/mikaelliljedahl/atf-demo.git");
        await withUser.Handle(user: null);
        Assert.Equal(["auth", "token", "--user", "mikaelliljedahl"], withUser.Calls[0].Args);
        Assert.Single(withUser.Completions);

        using var none = new Fixture();
        await none.Handle(user: null);
        Assert.Empty(none.Calls);
        Assert.Empty(none.Completions);
        var failure = Assert.Single(none.Failures);
        Assert.Contains("--github-user", failure.GetProperty("errorMessage").GetString());
        Assert.False(failure.GetProperty("shouldRetry").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_branch_this_machine_did_not_publish_fails_before_any_gh_call(bool wrongBranch)
    {
        using var f = wrongBranch ? new Fixture(headBranch: "prfactory/other", publish: true) : new Fixture(publish: false);
        if (wrongBranch)
        {
            f.Item.ContextJson = f.Item.ContextJson!.Replace("prfactory/other", "prfactory/PRF-1", StringComparison.Ordinal);
        }
        await f.Handle();

        Assert.Empty(f.Calls);
        Assert.Contains("did not publish", Assert.Single(f.Failures).GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task A_fork_pull_request_is_skipped_for_the_same_repository_one()
    {
        using var f = new Fixture { ExistingPr = true };
        f.ForkEntries = [Fixture.Entry(3, Sha, cross: true, owner: "someone"), Fixture.Entry(4, "ffffffffffffffffffffffffffffffffffffffff", cross: true, owner: "other")];
        await f.Handle();

        Assert.DoesNotContain(f.Calls, c => c.Args.Take(2).SequenceEqual(["pr", "create"]));
        var result = JsonElement.Parse(Assert.Single(f.Completions).GetProperty("resultMarkdown").GetString()!);
        Assert.Equal(7, result.GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task A_fork_only_pull_request_at_the_same_sha_is_never_reused()
    {
        using var f = new Fixture { CreateHidden = true };
        f.ForkEntries = [Fixture.Entry(3, Sha, cross: true, owner: "someone")];
        await f.Handle();

        Assert.Single(f.Calls, c => c.Args.Take(2).SequenceEqual(["pr", "create"]));
        Assert.Empty(f.Completions);
        Assert.Single(f.Failures);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"maxConcurrentWorkItems\":1,\"activeWorkItems\":1")]
    public async Task Pull_request_work_runs_when_teams_are_full_or_the_server_is_at_cap_without_a_team(string limits)
    {
        using var f = new Fixture { Limits = limits };
        f.Ordinary = new PRFactoryWorkItem { Id = Guid.NewGuid(), RepositoryId = f.Repo, ReadOnly = true, AgentType = PRFactoryAgentType.Codex, Prompt = "x" };
        var teams = new PRFactoryTeamStore(JobDatabase.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db"), TimeSpan.FromSeconds(2)));
        var client = f.Client();
        await client.SupportsBaseWipAsync(CancellationToken.None);
        var adapter = new PRFactoryWorkItems(Url, [new RepositoryMapping(f.Repo, Path.GetTempPath())], teams, client,
            _ => throw new InvalidOperationException("no team job expected"), _ => null, () => { },
            maxAcceptedTeams: limits.Length == 0 ? 0 : 10, pullRequests: f.Executor());
        await adapter.TickAsync(null, CancellationToken.None);

        Assert.Single(f.Completions);
        Assert.Equal(0, f.OrdinaryClaims);
        Assert.Null(teams.Get(Url, f.Item.Id));
        Assert.Null(teams.Get(Url, f.Ordinary.Id));
    }

    [Fact]
    public async Task A_moved_remote_branch_fails()
    {
        using var f = new Fixture { RemoteHead = "ffffffffffffffffffffffffffffffffffffffff" };
        await f.Handle();

        Assert.Contains("moved", Assert.Single(f.Failures).GetProperty("errorMessage").GetString());
        Assert.DoesNotContain(f.Calls, c => c.Args.Take(2).SequenceEqual(["pr", "create"]));
        Assert.Empty(f.Completions);
    }

    [Fact]
    public async Task Poll_advertises_the_capability_only_when_the_server_lists_it_and_the_tick_opens_the_pull_request()
    {
        using var f = new Fixture { Capabilities = [] };
        var client = f.Client();
        await client.SupportsBaseWipAsync(CancellationToken.None);
        await client.PollAsync([f.Repo], null, CancellationToken.None);
        Assert.DoesNotContain("capabilities=pull-request-v1", f.PollQueries[0], StringComparison.Ordinal);

        using var g = new Fixture();
        var teams = new PRFactoryTeamStore(JobDatabase.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db"), TimeSpan.FromSeconds(2)));
        var gClient = g.Client();
        await gClient.SupportsBaseWipAsync(CancellationToken.None);
        var adapter = new PRFactoryWorkItems(Url, [new RepositoryMapping(g.Repo, Path.GetTempPath())], teams, gClient,
            _ => throw new InvalidOperationException("no team job expected"), _ => null, () => { }, pullRequests: g.Executor());
        await adapter.TickAsync(null, CancellationToken.None);

        Assert.Contains("capabilities=pull-request-v1", g.PollQueries[0], StringComparison.Ordinal);
        Assert.Single(g.Completions);
        Assert.Null(teams.Get(Url, g.Item.Id));
    }

    [Fact]
    public async Task Registration_advertises_the_capability_only_when_the_server_lists_it()
    {
        foreach (var listed in new[] { true, false })
        {
            var registered = new List<string>();
            using var http = PRFactoryClient.CreateHttpClient(Url, "token", new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/worker/capabilities")
                {
                    return new(HttpStatusCode.OK) { Content = new StringContent(listed ? "{\"capabilities\":[\"pull-request-v1\"]}" : "{\"capabilities\":[]}") };
                }
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                registered.AddRange(body.RootElement.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!));
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"machineId\":\"8ad6f5c0-a4f0-42dc-8c29-59677ea37949\",\"heartbeatIntervalSeconds\":30}")
                };
            }));
            await new PRFactoryClient(http).RegisterMachineAsync(CancellationToken.None);
            Assert.Equal(listed, registered.Contains("pull-request-v1"));
        }
    }

    [Theory]
    [InlineData("https://github.com/o/r.git", "github.com", "o", "r", null)]
    [InlineData("git@github.com:o/r.git", "github.com", "o", "r", null)]
    [InlineData("https://bitbucket.org/ws/repo", "bitbucket.org", "ws", "repo", null)]
    [InlineData("https://x-token-auth:secret@bitbucket.org/ws/repo.git", "bitbucket.org", "ws", "repo", "x-token-auth")]
    [InlineData("git@bitbucket.org:ws/repo.git", "bitbucket.org", "ws", "repo", null)]
    [InlineData("ssh://git@bitbucket.org/ws/repo.git", "bitbucket.org", "ws", "repo", null)]
    [InlineData("https://Bitbucket.org/ws/repo.git", "bitbucket.org", "ws", "repo", null)]
    public void Remotes_parse_to_host_owner_and_repository(string remote, string host, string owner, string repo, string? user)
    {
        Assert.Equal(new PRFactoryPullRequests.RemoteRepository(host, owner, repo, user), PRFactoryPullRequests.ParseRemote(remote));
    }

    [Theory]
    [InlineData(AzRemote, "mliljedahl", "Sandbox", "GitSandbox", "mliljedahl")]
    [InlineData("https://dev.azure.com/org/My%20Project/_git/repo", "org", "My Project", "repo", null)]
    [InlineData("https://org.visualstudio.com/project/_git/repo", "org", "project", "repo", null)]
    [InlineData("https://org.visualstudio.com/DefaultCollection/project/_git/repo", "org", "project", "repo", null)]
    [InlineData("git@ssh.dev.azure.com:v3/org/project/repo", "org", "project", "repo", null)]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/org/My%20Project/repo", "org", "My Project", "repo", null)]
    public void Azure_DevOps_remotes_parse_to_organization_project_and_repository(string remote, string org, string project, string repo, string? user)
    {
        Assert.Equal(new PRFactoryPullRequests.RemoteRepository("dev.azure.com", org, repo, user, project), PRFactoryPullRequests.ParseRemote(remote));
    }

    [Theory]
    [InlineData("https://gitlab.com/o/r.git")]
    [InlineData("git@gitlab.com:o/r.git")]
    [InlineData("https://bitbucket.org/ws/repo/extra")]
    [InlineData("/home/me/repo")]
    [InlineData(@"C:\repos\atf-demo")]
    [InlineData("https://dev.azure.com/org/project/repo")]
    [InlineData("git@ssh.dev.azure.com:v3/org/repo")]
    public void Unsupported_remotes_are_refused(string remote)
    {
        Assert.Contains("bitbucket.org", Assert.ThrowsAny<Exception>(() => PRFactoryPullRequests.ParseRemote(remote)).Message);
    }

    [Fact]
    public async Task Bitbucket_creates_the_pull_request_with_a_bearer_token_from_git_credentials()
    {
        using var f = new Fixture(remote: BbRemote);
        await f.Handle(user: null);

        var fill = Assert.Single(f.Calls);
        Assert.Equal(["credential", "fill"], fill.Args);
        Assert.Equal("protocol=https\nhost=bitbucket.org\nusername=x-token-auth\n\n", fill.Stdin);
        Assert.Equal("0", fill.Env!["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", fill.Env["GCM_INTERACTIVE"]);
        Assert.All(f.BbRequests, r => Assert.Equal("Bearer " + BbToken, r.Auth));
        var (_, _, _, createBody) = Assert.Single(f.BbRequests, r => r.Method == HttpMethod.Post);
        using (var body = JsonDocument.Parse(createBody))
        {
            Assert.Equal("PRF-1: Title", body.RootElement.GetProperty("title").GetString());
            Assert.Equal("Body text", body.RootElement.GetProperty("description").GetString());
            Assert.Equal("prfactory/PRF-1", body.RootElement.GetProperty("source").GetProperty("branch").GetProperty("name").GetString());
            Assert.Equal("main", body.RootElement.GetProperty("destination").GetProperty("branch").GetProperty("name").GetString());
        }
        Assert.Contains(f.BbRequests, r => r.Method == HttpMethod.Get && r.PathAndQuery.Contains("state%3D%22OPEN%22", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Logs, l => l.Contains(BbToken, StringComparison.Ordinal));

        var completion = Assert.Single(f.Completions);
        var result = JsonElement.Parse(completion.GetProperty("resultMarkdown").GetString()!);
        Assert.Equal(7, result.GetProperty("number").GetInt32());
        Assert.Equal("https://bitbucket.org/ws/atf-demo/pull-requests/7", result.GetProperty("url").GetString());
        Assert.Equal(Sha, result.GetProperty("headSha").GetString());
        Assert.True(result.GetProperty("created").GetBoolean());
        Assert.Empty(f.Failures);
    }

    [Fact]
    public async Task Bitbucket_reuses_an_open_same_repository_pull_request()
    {
        using var f = new Fixture(remote: BbRemote) { ExistingPr = true };
        f.ForkEntries = [Fixture.BbEntry(3, Sha[..12], repo: "someone/atf-demo")];
        await f.Handle(user: null);

        Assert.DoesNotContain(f.BbRequests, r => r.Method == HttpMethod.Post);
        var result = JsonElement.Parse(Assert.Single(f.Completions).GetProperty("resultMarkdown").GetString()!);
        Assert.Equal(7, result.GetProperty("number").GetInt32());
        Assert.False(result.GetProperty("created").GetBoolean());
    }

    [Fact]
    public async Task Bitbucket_moved_branch_fails_without_creating()
    {
        using var f = new Fixture(remote: BbRemote) { RemoteHead = "ffffffffffffffffffffffffffffffffffffffff" };
        await f.Handle(user: null);

        Assert.Contains("moved", Assert.Single(f.Failures).GetProperty("errorMessage").GetString());
        Assert.DoesNotContain(f.BbRequests, r => r.Method == HttpMethod.Post);
        Assert.Empty(f.Completions);
    }

    [Fact]
    public async Task Bitbucket_401_fails_without_leaking_the_token()
    {
        using var f = new Fixture(remote: BbRemote) { BbStatus = HttpStatusCode.Unauthorized };
        await f.Handle(user: null);

        var failure = Assert.Single(f.Failures);
        Assert.Contains("HTTP 401", failure.GetProperty("errorMessage").GetString());
        Assert.False(failure.GetProperty("shouldRetry").GetBoolean());
        Assert.DoesNotContain(BbToken, failure.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("Token invalid", failure.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(f.Logs, l => l.Contains(BbToken, StringComparison.Ordinal));
        Assert.Empty(f.Completions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Azure_DevOps_creates_the_pull_request_with_a_basic_pat_from_git_credentials(bool pathless)
    {
        using var f = new Fixture(remote: AzRemote) { AzPathless = pathless };
        await f.Handle(user: null);

        var fill = f.Calls[0];
        Assert.Equal(["-c", "credential.useHttpPath=true", "credential", "fill"], fill.Args);
        Assert.Equal("protocol=https\nhost=dev.azure.com\npath=mliljedahl/Sandbox/_git/GitSandbox\nusername=mliljedahl\n\n", fill.Stdin);
        Assert.Equal("0", fill.Env!["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", fill.Env["GCM_INTERACTIVE"]);
        if (pathless)
        {
            Assert.Equal("protocol=https\nhost=dev.azure.com\nusername=mliljedahl\n\n", Assert.Single(f.Calls[1..]).Stdin);
        }
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + AzToken));
        Assert.All(f.AzRequests, r => Assert.Equal(basic, r.Auth));
        Assert.Contains(f.AzRequests, r => r.Method == HttpMethod.Get && r.PathAndQuery.Contains("searchCriteria.status=active", StringComparison.Ordinal)
            && r.PathAndQuery.Contains("searchCriteria.sourceRefName=refs%2Fheads%2Fprfactory%2FPRF-1", StringComparison.Ordinal));
        var (_, _, _, createBody) = Assert.Single(f.AzRequests, r => r.Method == HttpMethod.Post);
        using (var body = JsonDocument.Parse(createBody))
        {
            Assert.Equal("PRF-1: Title", body.RootElement.GetProperty("title").GetString());
            Assert.Equal("Body text", body.RootElement.GetProperty("description").GetString());
            Assert.Equal("refs/heads/prfactory/PRF-1", body.RootElement.GetProperty("sourceRefName").GetString());
            Assert.Equal("refs/heads/main", body.RootElement.GetProperty("targetRefName").GetString());
        }

        var result = JsonElement.Parse(Assert.Single(f.Completions).GetProperty("resultMarkdown").GetString()!);
        Assert.Equal(7, result.GetProperty("number").GetInt32());
        Assert.Equal("https://dev.azure.com/mliljedahl/Sandbox/_git/GitSandbox/pullrequest/7", result.GetProperty("url").GetString());
        Assert.True(result.GetProperty("created").GetBoolean());
        Assert.Empty(f.Failures);
    }

    [Fact]
    public async Task Azure_DevOps_reuses_an_open_same_repository_pull_request()
    {
        using var f = new Fixture(remote: AzRemote) { ExistingPr = true };
        f.ForkEntries = [Fixture.AzEntry(3, Sha, fork: true)];
        await f.Handle(user: null);

        Assert.DoesNotContain(f.AzRequests, r => r.Method == HttpMethod.Post);
        var result = JsonElement.Parse(Assert.Single(f.Completions).GetProperty("resultMarkdown").GetString()!);
        Assert.Equal(7, result.GetProperty("number").GetInt32());
        Assert.False(result.GetProperty("created").GetBoolean());
    }

    [Fact]
    public async Task Azure_DevOps_moved_branch_fails_without_creating()
    {
        using var f = new Fixture(remote: AzRemote) { RemoteHead = "ffffffffffffffffffffffffffffffffffffffff" };
        await f.Handle(user: null);

        Assert.Contains("moved", Assert.Single(f.Failures).GetProperty("errorMessage").GetString());
        Assert.DoesNotContain(f.AzRequests, r => r.Method == HttpMethod.Post);
        Assert.Empty(f.Completions);
    }

    [Fact]
    public async Task Azure_DevOps_401_fails_without_leaking_the_token()
    {
        using var f = new Fixture(remote: AzRemote) { BbStatus = HttpStatusCode.Unauthorized };
        await f.Handle(user: null);

        var failure = Assert.Single(f.Failures);
        Assert.Contains("Azure DevOps refused: HTTP 401", failure.GetProperty("errorMessage").GetString());
        Assert.Contains("TF400813", failure.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(AzToken, failure.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(f.Logs, l => l.Contains(AzToken, StringComparison.Ordinal));
        Assert.Empty(f.Completions);
    }

    /// <summary>
    /// Opt-in, live: opens (or reuses) a pull request in the Azure DevOps sandbox for a branch already pushed there.
    /// Set ATF_AZDO_E2E=1, ATF_AZDO_E2E_BRANCH and ATF_AZDO_E2E_SHA (its pushed head); optional ATF_AZDO_E2E_REMOTE
    /// and ATF_AZDO_E2E_BASE (main). The token comes from AZURE_DEVOPS_PAT or git credentials. Never merges or abandons.
    /// </summary>
    [Fact]
    public async Task AzureDevOps_sandbox_e2e()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ATF_AZDO_E2E") == "1", "set ATF_AZDO_E2E=1 to open a pull request in the Azure DevOps sandbox");
        var branch = Environment.GetEnvironmentVariable("ATF_AZDO_E2E_BRANCH") ?? throw new InvalidOperationException("set ATF_AZDO_E2E_BRANCH");
        var sha = Environment.GetEnvironmentVariable("ATF_AZDO_E2E_SHA") ?? throw new InvalidOperationException("set ATF_AZDO_E2E_SHA");
        var remote = Environment.GetEnvironmentVariable("ATF_AZDO_E2E_REMOTE") ?? AzRemote;
        var baseBranch = Environment.GetEnvironmentVariable("ATF_AZDO_E2E_BASE") ?? "main";

        using var f = new Fixture(headBranch: branch, remote: remote, sha: sha, baseBranch: baseBranch);
        PRFactoryPullRequests Live() => new(Url, [new RepositoryMapping(f.Repo, Path.GetTempPath())], null, f.Client(), f.Publications, f.Logs.Add);
        await Live().HandleAsync(f.Item, TestContext.Current.CancellationToken);
        Assert.Empty(f.Failures);
        var first = JsonElement.Parse(Assert.Single(f.Completions).GetProperty("resultMarkdown").GetString()!);

        // A second run must find the same open pull request instead of creating another.
        await Live().HandleAsync(f.Item, TestContext.Current.CancellationToken);
        Assert.Empty(f.Failures);
        var second = JsonElement.Parse(f.Completions[1].GetProperty("resultMarkdown").GetString()!);
        Assert.False(second.GetProperty("created").GetBoolean());
        Assert.Equal(first.GetProperty("number").GetInt32(), second.GetProperty("number").GetInt32());
        TestContext.Current.SendDiagnosticMessage("Azure DevOps sandbox pull request: " + second.GetProperty("url").GetString());
    }
}
