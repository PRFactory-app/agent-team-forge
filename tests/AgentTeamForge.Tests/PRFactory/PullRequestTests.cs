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

        public Fixture(string headBranch = "prfactory/PRF-1", string remote = Remote, bool publish = true)
        {
            var db = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
            Publications = new PRFactoryPublicationStore(db);
            if (publish)
            {
                Publications.Verify(new PublicationIntent("pub-1", Url, Source, Guid.NewGuid(), "machine", "job", Repo.ToString("D"),
                    "key", "/lead", remote, "internal", headBranch, Sha, Sha, null));
            }
            Item = new PRFactoryWorkItem
            {
                Id = Guid.NewGuid(),
                Type = "pullRequestCreate",
                RepositoryId = Repo,
                LeaseToken = Guid.NewGuid(),
                ContextJson = JsonSerializer.Serialize(new PRFactoryPullRequestRequest("pull-request-create", 1, Source, Repo,
                    headBranch, Sha, "main", "PRF-1: Title", "Body text"), PRFactoryWorkItemJson.Default.PRFactoryPullRequestRequest)
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
                return Task.FromResult(new ProcessResult(0, list, ""));
            }
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
                return Json("{\"accepted\":true}");
            }
            if (path.Contains("/fail/", StringComparison.Ordinal))
            {
                Failures.Add(JsonElement.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
                return Json("{\"acknowledged\":true}");
            }
            throw new InvalidOperationException(path);
        }

        public PRFactoryClient Client() => new(PRFactoryClient.CreateHttpClient(Url, "token", new Handler(Reply)));

        public PRFactoryPullRequests Executor(string? user = "mikaelliljedahl") =>
            new(Url, [new RepositoryMapping(Repo, dir.Path)], user, Client(), Publications, Logs.Add, Gh);

        public Task Handle(string? user = "mikaelliljedahl") => Executor(user).HandleAsync(Item, CancellationToken.None);

        public void Dispose() => dir.Dispose();

        static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
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
    public async Task The_resolver_prefers_the_mise_gh_over_a_script_on_path()
    {
        using var dir = new TempStateDir();
        var bin = Path.Combine(dir.Path, "bin");
        Directory.CreateDirectory(bin);
        var wrapper = Path.Combine(bin, "gh");
        File.WriteAllText(wrapper, "#!/bin/bash\nexec mise x gh -- gh \"$@\"\n");
        var real = dir.File("real-gh");
        File.WriteAllBytes(real, [0x7f, (byte)'E', (byte)'L', (byte)'F', 0]);

        var chosen = await GhExecutable.ResolveAsync(bin, (spec, _) => Task.FromResult(
            spec.File == "mise" ? new ProcessResult(0, real + "\n", "") : throw new InvalidOperationException()), CancellationToken.None);
        Assert.Equal(real, chosen);

        // mise pointing back at the wrapper, or failing, falls back to PATH.
        Assert.Equal(wrapper, await GhExecutable.ResolveAsync(bin, (_, _) => Task.FromResult(new ProcessResult(0, wrapper, "")), CancellationToken.None));
        Assert.Equal(wrapper, await GhExecutable.ResolveAsync(bin, (_, _) => Task.FromResult(new ProcessResult(1, "", "no")), CancellationToken.None));
        // A real binary on PATH is used without asking mise.
        File.WriteAllBytes(wrapper, [0x7f, (byte)'E', (byte)'L', (byte)'F', 0]);
        Assert.Equal(wrapper, await GhExecutable.ResolveAsync(bin, (_, _) => throw new InvalidOperationException(), CancellationToken.None));
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
}
