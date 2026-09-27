using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Jobs.Publication;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class BranchPublisherTests
{
    [Theory]
    [InlineData("leftover.cs", false)]
    [InlineData("nested/report.md", false)]
    [InlineData("report.md", true)]
    public async Task Artefact_exclusion_is_limited_to_top_level_documents(string file, bool allowed)
    {
        using var rig = await Rig.Create();
        var path = Path.Combine(rig.Request.LeadPath, "ticket", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "untracked");
        using var publisher = rig.Publisher();
        var request = rig.Request with { ArtefactFolder = "ticket" };
        if (allowed) { Assert.NotNull(await publisher.PublishAsync(request, TestContext.Current.CancellationToken)); }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(request, TestContext.Current.CancellationToken));
            Assert.Equal("", await rig.RemoteHead());
        }
    }

    [Theory]
    [InlineData("Implementation", false)]
    [InlineData("CodeReview", false)]
    [InlineData("CustomStep", false)]
    [InlineData("Planning", true)]
    public async Task Clean_output_publishes_requested_ref(string type, bool init)
    {
        using var rig = await Rig.Create();
        using var publisher = rig.Publisher();
        var receipt = await publisher.PublishAsync(rig.Request with { Type = type, ProjectInit = init }, TestContext.Current.CancellationToken);
        Assert.Equal(rig.Head, receipt!.Intent.HeadSha);
        Assert.NotNull(receipt.VerifiedAt);
        Assert.Equal(rig.Head, await rig.RemoteHead());
        Assert.Equal(receipt, await publisher.PublishAsync(rig.Request with { Type = type, ProjectInit = init }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dirty_output_fails_with_paths_without_push_or_auto_commit()
    {
        using var rig = await Rig.Create();
        Directory.CreateDirectory(Path.Combine(rig.Request.LeadPath, "__pycache__"));
        File.WriteAllText(Path.Combine(rig.Request.LeadPath, "__pycache__", "app.cpython-314.pyc"), "generated");
        File.WriteAllText(Path.Combine(rig.Request.LeadPath, "leftover.cs"), "dirty");
        using var publisher = rig.Publisher();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken));
        Assert.Contains("leftover.cs", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("app.cpython-314.pyc", error.Message, StringComparison.Ordinal);
        Assert.Null(rig.Store.Get(rig.Request.PublicationId));
        Assert.Equal("", await rig.RemoteHead());
        Assert.Equal(rig.Head, await Git(rig.Request.LeadPath, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Generated_untracked_files_do_not_block_publication_or_get_committed()
    {
        using var rig = await Rig.Create();
        var cwd = rig.Request.LeadPath;
        Directory.CreateDirectory(Path.Combine(cwd, "__pycache__"));
        File.WriteAllText(Path.Combine(cwd, "__pycache__", "app.cpython-314.pyc"), "generated");
        File.WriteAllText(Path.Combine(cwd, "__pycache__", "test_app.cpython-314.pyc"), "generated");
        Directory.CreateDirectory(Path.Combine(cwd, ".pytest_cache"));
        File.WriteAllText(Path.Combine(cwd, ".pytest_cache", "cache"), "generated");
        using var publisher = rig.Publisher();

        var receipt = await publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken);

        Assert.Equal(rig.Head, receipt!.Intent.HeadSha);
        Assert.Equal(rig.Head, await rig.RemoteHead());
        Assert.Contains("__pycache__/app.cpython-314.pyc", await Git(cwd, "status", "--porcelain", "--untracked-files=all"));
        Assert.Equal("", await Git(cwd, "ls-tree", "-r", "--name-only", "HEAD"));
    }

    [Fact]
    public async Task Tracked_generated_file_changes_still_block_publication()
    {
        using var rig = await Rig.Create();
        var cwd = rig.Request.LeadPath;
        Directory.CreateDirectory(Path.Combine(cwd, "__pycache__"));
        var path = Path.Combine(cwd, "__pycache__", "app.pyc");
        File.WriteAllText(path, "initial");
        await Git(cwd, "add", "__pycache__/app.pyc");
        await Git(cwd, "commit", "-m", "tracked output");
        File.WriteAllText(path, "changed");
        using var publisher = rig.Publisher();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken));

        Assert.Contains("__pycache__/app.pyc", error.Message, StringComparison.Ordinal);
        Assert.Equal("", await rig.RemoteHead());
    }

    [Fact]
    public async Task Untracked_source_under_build_or_bin_still_blocks_publication()
    {
        using var rig = await Rig.Create();
        var cwd = rig.Request.LeadPath;
        Directory.CreateDirectory(Path.Combine(cwd, "build"));
        File.WriteAllText(Path.Combine(cwd, "build", "Build.cs"), "source");
        Directory.CreateDirectory(Path.Combine(cwd, "bin"));
        File.WriteAllText(Path.Combine(cwd, "bin", "deploy.sh"), "source");
        using var publisher = rig.Publisher();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken));

        Assert.Contains("build/Build.cs", error.Message, StringComparison.Ordinal);
        Assert.Contains("bin/deploy.sh", error.Message, StringComparison.Ordinal);
        Assert.Equal("", await rig.RemoteHead());
    }

    [Theory]
    [InlineData("gitignore")]
    [InlineData("info-exclude")]
    [InlineData("core-excludes-file")]
    public async Task Git_ignore_sources_are_honoured(string source)
    {
        using var rig = await Rig.Create();
        var cwd = rig.Request.LeadPath;
        const string pattern = "ignored-output/";
        switch (source)
        {
            case "gitignore":
                File.WriteAllText(Path.Combine(cwd, ".gitignore"), pattern);
                await Git(cwd, "add", ".gitignore");
                await Git(cwd, "commit", "-m", "ignore output");
                break;
            case "info-exclude":
                File.AppendAllText(Path.Combine(cwd, ".git", "info", "exclude"), "\n" + pattern);
                break;
            case "core-excludes-file":
                var excludes = Path.Combine(Path.GetDirectoryName(cwd)!, "global-excludes");
                File.WriteAllText(excludes, pattern);
                await Git(cwd, "config", "core.excludesFile", excludes);
                break;
        }
        Directory.CreateDirectory(Path.Combine(cwd, "ignored-output"));
        File.WriteAllText(Path.Combine(cwd, "ignored-output", "result.cs"), "generated");
        using var publisher = rig.Publisher();

        var receipt = await publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken);

        Assert.Equal(await Git(cwd, "rev-parse", "HEAD"), receipt!.Intent.HeadSha);
        Assert.Equal(receipt.Intent.HeadSha, await rig.RemoteHead());
        Assert.Equal("", await Git(cwd, "status", "--porcelain", "--untracked-files=all"));
    }

    [Fact]
    public async Task Crash_after_push_recovers_receipt_without_another_push()
    {
        using var rig = await Rig.Create();
        using (var publisher = rig.Publisher(new(name =>
        {
            if (name == "publication.after-push") { throw new IOException("simulated crash"); }
        })))
        {
            await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken));
        }
        Assert.Null(rig.Store.Get(rig.Request.PublicationId)!.VerifiedAt);
        Assert.Equal(rig.Head, await rig.RemoteHead());
        var calls = 0;
        using var recovered = new BranchPublisher(new PRFactoryPublicationStore(JobDatabase.Open(rig.Database.Path, TimeSpan.FromSeconds(5))),
            (_, _, _) => { calls++; return Task.FromResult(false); });
        Assert.NotNull((await recovered.PublishAsync(rig.Request, TestContext.Current.CancellationToken))!.VerifiedAt);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Remote_changed_after_push_cannot_get_success_receipt()
    {
        using var rig = await Rig.Create();
        using var publisher = rig.Publisher(new(name =>
        {
            if (name == "publication.after-push")
            {
                Git(rig.Request.Remote, "update-ref", "refs/heads/published", rig.Request.BaseSha).GetAwaiter().GetResult();
            }
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken));
        Assert.Null(rig.Store.Get(rig.Request.PublicationId)!.VerifiedAt);
    }

    [Fact]
    public async Task Non_fast_forward_is_rejected_without_force()
    {
        using var rig = await Rig.Create();
        var cwd = rig.Request.LeadPath;
        await Git(cwd, "checkout", "-b", "other", rig.Request.BaseSha);
        File.WriteAllText(Path.Combine(cwd, "other"), "other");
        await Git(cwd, "add", "other");
        await Git(cwd, "commit", "-m", "other");
        var other = await Git(cwd, "rev-parse", "HEAD");
        await Git(cwd, "push", "origin", "HEAD:refs/heads/published");
        await Git(cwd, "checkout", "internal");
        using var publisher = rig.Publisher();
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken));
        Assert.Equal(other, await rig.RemoteHead());
        Assert.Null(rig.Store.Get(rig.Request.PublicationId)!.VerifiedAt);
    }

    [Fact]
    public async Task Fenced_authority_and_read_only_never_push()
    {
        using var rig = await Rig.Create();
        using var publisher = new BranchPublisher(rig.Store, (_, _, _) => Task.FromResult(false));
        Assert.Null(await publisher.PublishAsync(rig.Request with { ReadOnly = true }, TestContext.Current.CancellationToken));
        Assert.Null(await publisher.PublishAsync(rig.Request with { Type = "Planning" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken));
        Assert.Equal("", await rig.RemoteHead());
    }

    [Fact]
    public async Task Changed_push_remote_and_changed_frozen_output_are_rejected()
    {
        using var rig = await Rig.Create();
        using var publisher = rig.Publisher();
        await Git(rig.Request.LeadPath, "remote", "set-url", "--push", "origin", rig.Request.Remote + "-wrong");
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken));
        await Git(rig.Request.LeadPath, "remote", "set-url", "--push", "origin", rig.Request.Remote);
        await publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken);
        await Git(rig.Request.LeadPath, "commit", "--allow-empty", "-m", "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken));
        Assert.Equal(rig.Head, await rig.RemoteHead());
    }

    static async Task<string> Git(string cwd, params string[] args) =>
        await JobWorktree.GitAsync(cwd, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken, args)
        ?? throw new InvalidOperationException("Test git failed: " + args[0]);

    sealed class Rig(TempStateDir temp, JobDatabase database, PublicationRequest request, string head) : IDisposable
    {
        public JobDatabase Database { get; } = database;
        public PublicationRequest Request { get; } = request;
        public string Head { get; } = head;
        public PRFactoryPublicationStore Store { get; } = new(database);
        public void Dispose() => temp.Dispose();
        public BranchPublisher Publisher(DurabilityCheckpoints? checkpoints = null) => new(Store,
            async (_, effect, _) => { await effect(); return true; }, checkpoints);
        public async Task<string> RemoteHead()
        {
            var output = await Git(Request.LeadPath, "ls-remote", "origin", "refs/heads/published");
            return output.Split('\t')[0];
        }
        public static async Task<Rig> Create()
        {
            var temp = new TempStateDir();
            var remote = temp.File("remote.git");
            var cwd = temp.File("lead");
            Directory.CreateDirectory(cwd);
            await Git(temp.Path, "init", "--bare", remote);
            await Git(cwd, "init", "-b", "internal");
            await Git(cwd, "config", "user.name", "Publication Test");
            await Git(cwd, "config", "user.email", "publication@example.test");
            await Git(cwd, "remote", "add", "origin", remote);
            await Git(cwd, "commit", "--allow-empty", "-m", "base");
            var basis = await Git(cwd, "rev-parse", "HEAD");
            await Git(cwd, "commit", "--allow-empty", "-m", "output");
            var head = await Git(cwd, "rev-parse", "HEAD");
            var database = JobDatabase.Create(temp.File("jobs.db"), TimeSpan.FromSeconds(5));
            return new(temp, database, new("publication", "https://server.invalid", Guid.NewGuid(), Guid.NewGuid(),
                "machine", "job", "repository", "workspace", cwd, remote, "internal", "published", basis, "Implementation"), head);
        }
    }
}
