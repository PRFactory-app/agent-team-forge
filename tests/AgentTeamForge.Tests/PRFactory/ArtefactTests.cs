using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class ArtefactTests
{
    // Wire names/types from WorkerWorkItemDto and WorkItemService's claim response, PRFactory PR #254.
    // Built-ins intentionally have null expectedOutput; repositories/default base are context fields.
    const string Id = "11111111-1111-1111-1111-111111111111";
    const string Repo = "22222222-2222-2222-2222-222222222222";
    static string Claim(string type, string? expected = null, string context = "{}") => $$"""
        {"id":"{{Id}}","ticketId":"33333333-3333-3333-3333-333333333333","ticketKey":"PRF-42",
        "ticketSource":"Manual","repositoryId":"{{Repo}}","type":"{{char.ToLowerInvariant(type[0]) + type[1..]}}","agentType":"Codex",
        "status":"Claimed","prompt":"Write the phase deliverable in docs/tickets/PRF-42",
        "ticketArtefactFolder":"docs/tickets/PRF-42","expectedOutput":{{JsonSerializer.Serialize(expected, PRFactoryWorkItemJson.Default.String)}},
        "stepKey":null,"readOnly":true,"startFromBranch":"handover/PRF-42","publishBranch":"ticket/PRF-42",
        "expiresAt":"2026-10-01T12:00:00Z","leaseToken":"44444444-4444-4444-4444-444444444444",
        "contextJson":{{JsonSerializer.Serialize(context, PRFactoryWorkItemJson.Default.String)}},"repositoryResults":[{"repositoryId":"{{Repo}}",
        "baseCommitSha":"abc","baseBranchName":"main","pushState":"NotAttempted"}],"teamPlan":null}
        """;

    [Theory]
    [InlineData("TicketRefinement", "qa.md", "qa-po")]
    [InlineData("ClarifyingQuestions", "questions.html", "qa-dev")]
    [InlineData("Planning", "plan.md", "plan")]
    [InlineData("PlanReview", "plan-review-2.md", "plan-review")]
    [InlineData("TestPlan", "testplan.md", "testplan")]
    [InlineData("CodeReview", "code-review-1.html", "code-review")]
    [InlineData("VisualQa", "visual-qa.md", "visual-qa")]
    [InlineData("Decomposition", "decomposition-proposal.json", "decomposition-proposal")]
    [InlineData("Discovery", "idea.md", "idea")]
    [InlineData("CustomStep", "plan.md", "custom-step")]
    [InlineData("Implementation", null, null)]
    [InlineData("HostingNeedsDerivation", null, null)]
    [InlineData("HostingResearch", null, null)]
    public async Task Real_phase_claim_uploads_expected_files_and_kinds(string type, string? file, string? kind)
    {
        using var run = new Run(Claim(type, type == "CustomStep" ? file : null));
        if (file is not null) { run.Write(file, file.EndsWith(".json", StringComparison.Ordinal) ? "{\"slices\":[]}" : "Phase output"); }
        if (file is not null) { run.Write("notes.html", "Supplemental"); }
        run.Write("scratch.json", "{}");
        await run.Tick();
        Assert.Equal(1, run.Completed);
        Assert.Equal(type == "CustomStep" ? "Phase output" : type is "Implementation" or "HostingNeedsDerivation" or "HostingResearch" ? "Done" : null, run.ResultMarkdown);
        Assert.Empty(run.Failures);
        using var upload = JsonDocument.Parse(Assert.Single(run.Payloads));
        var artefacts = upload.RootElement.GetProperty("artefacts").EnumerateArray().ToArray();
        if (file is not null)
        {
            Assert.Contains(artefacts, a => a.GetProperty("fileName").GetString() == file && a.GetProperty("kind").GetString() == kind);
            Assert.Equal(type != "CustomStep", artefacts.Any(a => a.GetProperty("fileName").GetString() == "notes.html"));
        }
        Assert.Equal(type == "Decomposition", artefacts.Any(a => a.GetProperty("fileName").GetString() == "scratch.json"));
        if (type == "Planning")
        {
            var basis = Assert.Single(artefacts, a => a.GetProperty("kind").GetString() == "plan-basis");
            using var manifest = JsonDocument.Parse(basis.GetProperty("content").GetString()!);
            var repository = Assert.Single(manifest.RootElement.GetProperty("repositories").EnumerateArray());
            Assert.Equal(Repo, repository.GetProperty("repositoryId").GetString());
            Assert.Equal("main", repository.GetProperty("branch").GetString());
            Assert.Equal(40, repository.GetProperty("headSha").GetString()!.Length);
            Assert.Contains(repository.GetProperty("paths").EnumerateArray(), p => p.GetString() == "source.txt");
        }
        var saved = JsonSerializer.Deserialize(run.Teams.Get("https://example.test", Guid.Parse(Id))!.ClaimedJson, PRFactoryWorkItemJson.Default.PRFactoryWorkItem)!;
        Assert.Equal(type, saved.Type);
        Assert.Equal("handover/PRF-42", saved.StartFromBranch);
        Assert.Equal("ticket/PRF-42", saved.PublishBranch);
        Assert.NotNull(saved.ExpiresAt);
        Assert.Equal("main", saved.RepositoryResults!.Value[0].GetProperty("baseBranchName").GetString());
    }

    [Theory]
    [InlineData("ClarifyingQuestions", "{}")]
    [InlineData("Planning", "{}")]
    [InlineData("Decomposition", "{}")]
    [InlineData("CustomStep", "{}")]
    [InlineData("TicketRefinement", "{\"step\":\"classify\"}")]
    [InlineData("Discovery", "{\"step\":\"Research\"}")]
    public async Task Missing_phase_output_fails_without_an_upload(string type, string context)
    {
        using var run = new Run(Claim(type, type == "CustomStep" ? "expected.md" : null, context));
        run.Write("idea.md", "Old intake output");
        await run.Tick();
        Assert.Empty(run.Payloads);
        Assert.Equal(0, run.Completed);
        Assert.Contains("requires", Assert.Single(run.Failures));
        await run.Tick();
        Assert.Single(run.Failures);
    }

    [Fact]
    public async Task Failed_job_reports_failure_without_uploading()
    {
        using var run = new Run(Claim("Planning")) { Status = JobStatus.Failed };
        await run.Tick();
        Assert.Empty(run.Payloads);
        Assert.Single(run.Failures);
    }

    [Fact]
    public async Task Missing_output_failure_survives_a_lost_failure_response_and_late_file()
    {
        using var run = new Run(Claim("Planning")) { LoseFailureResponse = true };
        await run.Tick();
        Assert.Single(run.Failures);
        run.Write("plan.md", "Arrived after failure was frozen");
        run.LoseFailureResponse = false;
        await run.Tick();
        Assert.Equal(2, run.Failures.Count);
        Assert.Equal(run.Failures[0], run.Failures[1]);
        Assert.Empty(run.Payloads);
        Assert.Equal(0, run.Completed);
    }

    [Theory]
    [InlineData(0)] // Server committed the upload, then its response was lost.
    [InlineData(429)]
    [InlineData(503)]
    public async Task Restart_retries_frozen_bytes_even_after_files_change(int response)
    {
        using var run = new Run(Claim("Planning")) { UploadResponse = response };
        run.Write("plan.md", "Original");
        await run.Tick();
        Assert.Single(run.Payloads);
        Assert.Equal(0, run.Completed);
        run.Write("plan.md", "Changed after server committed");
        run.Write("new.md", "New");
        run.UploadResponse = 200;
        await run.Tick(); // New adapter and new SQLite store each time.
        Assert.Equal(2, run.Payloads.Count);
        Assert.Equal(run.Payloads[0], run.Payloads[1]);
        Assert.Equal(1, run.Completed);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [InlineData(422)]
    public async Task Terminal_upload_rejection_reports_fail(int status)
    {
        using var run = new Run(Claim("ClarifyingQuestions")) { UploadResponse = status };
        run.Write("qa.md", "Question");
        await run.Tick();
        Assert.Contains($"HTTP {status}", Assert.Single(run.Failures));
        await run.Tick();
        Assert.Single(run.Payloads);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/tmp")]
    [InlineData("linked/nested")]
    [InlineData("docs/tickets/PRF-42")]
    public async Task Traversal_and_intermediate_or_file_symlinks_fail(string folder)
    {
        using var run = new Run(Claim("ClarifyingQuestions").Replace("docs/tickets/PRF-42", folder, StringComparison.Ordinal));
        var outside = Directory.CreateDirectory(run.Dir.File("outside/nested")).FullName;
        File.WriteAllText(Path.Combine(outside, "qa.md"), "secret");
        Directory.CreateSymbolicLink(Path.Combine(run.RepoPath, "linked"), Path.GetDirectoryName(outside)!);
        if (folder == "docs/tickets/PRF-42")
        {
            run.Write("placeholder.md", "");
            File.CreateSymbolicLink(Path.Combine(run.RepoPath, folder, "qa.md"), Path.Combine(outside, "qa.md"));
        }
        await run.Tick();
        Assert.Empty(run.Payloads);
        Assert.Single(run.Failures);
    }

    sealed class Run : IDisposable
    {
        public TempStateDir Dir { get; } = new();
        public string RepoPath { get; }
        public PRFactoryTeamStore Teams => new(JobDatabase.Open(Dir.File("jobs.db"), TimeSpan.FromSeconds(2)));
        readonly string claim;
        public List<string> Payloads { get; } = [];
        public List<string> Failures { get; } = [];
        public int Completed { get; private set; }
        public string? ResultMarkdown { get; private set; }
        public int UploadResponse { get; set; } = 200;
        public bool LoseFailureResponse { get; set; }
        public string Status { get; set; } = JobStatus.Completed;
        public Run(string claim)
        {
            this.claim = claim;
            JobDatabase.Create(Dir.File("jobs.db"), TimeSpan.FromSeconds(2));
            RepoPath = Directory.CreateDirectory(Dir.File("lead-worktree")).FullName;
            Git("init", "-b", "main");
            File.WriteAllText(Path.Combine(RepoPath, "source.txt"), "tracked source");
            Git("add", "source.txt");
            Git("-c", "user.name=ATF Test", "-c", "user.email=test@example.test", "commit", "-m", "initial");
        }
        void Git(params string[] args)
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = RepoPath, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) { info.ArgumentList.Add(arg); }
            using var process = Process.Start(info)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        public void Write(string file, string content)
        {
            var folder = Directory.CreateDirectory(Path.Combine(RepoPath, "docs/tickets/PRF-42")).FullName;
            File.WriteAllText(Path.Combine(folder, file), content);
        }
        public async Task Tick()
        {
            using var http = PRFactoryClient.CreateHttpClient("https://example.test", "token", new Handler(Reply));
            var job = new JobRecord("lead-job", "prfactory", "connector", "lead", "key", "prompt", "", Status,
                null, null, 0, "codex", null, null, null) with
            { Cwd = Dir.Path, WorktreePath = RepoPath, ResultText = "Done" };
            var adapter = new PRFactoryWorkItems("https://example.test", [new(Guid.Parse(Repo), Dir.Path)], Teams,
                new PRFactoryClient(http), _ => JobResult.Ok(new JobView("lead-job", Status, null, null, 0), "accepted"), _ => job, () => { });
            await adapter.TickAsync(null, CancellationToken.None);
        }
        HttpResponseMessage Reply(HttpRequestMessage request)
        {
            if (ManagedWire.Reply(request) is { } managed) { return managed; }
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/poll", StringComparison.Ordinal)) { return Json("{\"workItems\":[" + claim + "]}"); }
            if (path.Contains("/claim/", StringComparison.Ordinal)) { return Json("{\"workItem\":" + claim + "}"); }
            if (path.Contains("/artefacts/", StringComparison.Ordinal))
            {
                Payloads.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                if (UploadResponse == 0) { throw new HttpRequestException("Response lost"); }
                return Json("{\"accepted\":true}", UploadResponse);
            }
            if (path.Contains("/complete/", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                ResultMarkdown = body.RootElement.GetProperty("resultMarkdown").GetString();
                Completed++;
                return Json("{\"accepted\":true}");
            }
            if (path.Contains("/fail/", StringComparison.Ordinal))
            {
                Failures.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                if (LoseFailureResponse) { throw new HttpRequestException("Failure response lost"); }
                return Json("{\"acknowledged\":true}");
            }
            throw new InvalidOperationException(path);
        }
        static HttpResponseMessage Json(string content, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        public void Dispose() => Dir.Dispose();
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
}
