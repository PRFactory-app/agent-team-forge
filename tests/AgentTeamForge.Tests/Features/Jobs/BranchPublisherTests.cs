using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Jobs.Publication;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Migrations;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class BranchPublisherTests
{
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
        File.WriteAllText(Path.Combine(rig.Request.LeadPath, "leftover.cs"), "dirty");
        using var publisher = rig.Publisher();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(rig.Request, TestContext.Current.CancellationToken));
        Assert.Contains("leftover.cs", error.Message, StringComparison.Ordinal);
        Assert.Null(rig.Store.Get(rig.Request.PublicationId));
        Assert.Equal("", await rig.RemoteHead());
        Assert.Equal(rig.Head, await Git(rig.Request.LeadPath, "rev-parse", "HEAD"));
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
            using (var db = database.OpenConnection())
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = PRFactoryPublicationMigration.Sql;
                cmd.ExecuteNonQuery();
            }
            return new(temp, database, new("publication", "https://server.invalid", Guid.NewGuid(), Guid.NewGuid(),
                "machine", "job", "repository", "workspace", cwd, remote, "internal", "published", basis, "Implementation"), head);
        }
    }
}
