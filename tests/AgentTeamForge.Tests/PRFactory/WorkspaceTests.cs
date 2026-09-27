using System.Diagnostics;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task Default_is_fetched_not_dirty_checkout_and_restart_uses_recorded_sha()
    {
        using var f = new WorkspaceFixture();
        Git(f.Repo, "checkout", "-b", "incidental");
        File.WriteAllText(Path.Combine(f.Repo, "base.txt"), "user dirt");
        var snapshot = await f.Workspaces.PrepareAsync(f.Request with { ReadOnly = true });
        Assert.Equal(f.BaseSha, JobWorktree.Head(snapshot.LeadPath));
        Assert.Equal("base", File.ReadAllText(Path.Combine(snapshot.LeadPath, "base.txt")));
        Assert.Equal("user dirt", File.ReadAllText(Path.Combine(f.Repo, "base.txt")));
        Assert.NotEqual(f.Repo, snapshot.LeadPath);
        File.WriteAllText(Path.Combine(snapshot.LeadPath, "notes.md"), "retained");
        Commit(f.Repo, "later.txt", "remote has moved");
        Git(f.Repo, "push", "origin", "HEAD:main");
        using var restarted = new TeamWorkspace(new PRFactoryWorkspaceStore(JobDatabase.Open(f.Db.Path, TimeSpan.FromSeconds(2))));
        var resumed = await restarted.PrepareAsync(f.Request with { ReadOnly = true });
        Assert.Equal(snapshot.StartingSha, resumed.StartingSha);
        Assert.Equal(snapshot.LeadPath, resumed.LeadPath);
        Assert.Equal("retained", File.ReadAllText(Path.Combine(resumed.LeadPath, "notes.md")));
    }

    [Fact]
    public async Task Review_handover_init_precedence_and_expected_sha_validation()
    {
        using var f = new WorkspaceFixture();
        Git(f.Repo, "checkout", "-b", "implementation");
        var implementation = Commit(f.Repo, "code.txt", "implementation");
        Git(f.Repo, "push", "origin", "implementation");
        Git(f.Repo, "checkout", "-b", "handover");
        var handover = Commit(f.Repo, "handover.txt", "handover");
        Git(f.Repo, "push", "origin", "handover");
        var request = f.Request with
        {
            PriorBranch = "implementation",
            PriorSha = implementation,
            StartFromBranch = "handover",
            StartCommitSha = handover,
            ProjectInit = true,
            PublishBranch = "handover"
        };
        var review = await f.Workspaces.PrepareAsync(request);
        Assert.Equal(implementation, JobWorktree.Head(review.LeadPath));
        Assert.Equal(f.BaseSha, review.BaseSha);
        Assert.All(review.Members, m => Assert.Equal(implementation, JobWorktree.Head(m.Path)));
        var adopted = await f.Workspaces.PrepareAsync(request with { Key = "handover", PriorBranch = null, PriorSha = null });
        Assert.Equal(handover, adopted.StartingSha);
        var init = await f.Workspaces.PrepareAsync(request with { Key = "init", PriorBranch = null, StartFromBranch = null });
        Assert.Equal(handover, init.StartingSha);
        var firstInit = await f.Workspaces.PrepareAsync(f.Request with { Key = "new-init", ProjectInit = true, PublishBranch = "new-ticket" });
        Assert.Equal(f.BaseSha, firstInit.StartingSha);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workspaces.PrepareAsync(request with { Key = "bad", PriorSha = f.BaseSha }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workspaces.PrepareAsync(request with { Key = "missing", PriorSha = null }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workspaces.PrepareAsync(f.Request with { Key = "remote", Remote = f.Remote + "-wrong" }));
        Assert.Null(f.Store.Get("remote"));
    }

    [Fact]
    public async Task Children_integrate_in_order_and_recover_after_git_before_receipt()
    {
        using var f = new WorkspaceFixture();
        var workspace = await f.Workspaces.PrepareAsync(f.Request);
        var first = Commit(workspace.Members[0].Path, "one.txt", "one");
        var second = Commit(workspace.Members[1].Path, "two.txt", "two");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workspaces.IntegrateAsync(workspace.Key, 1));
        var result = await f.Workspaces.IntegrateAsync(workspace.Key, 0);
        Assert.Equal(f.BaseSha, result.BeforeSha);
        Assert.Equal(first, result.ChildHead);
        // Simulate loss of the final SQL receipt after Git advanced the branch.
        Sql(f.Db, "UPDATE prfactory_workspace_integrations SET applied=0");
        using var restarted = new TeamWorkspace(new PRFactoryWorkspaceStore(JobDatabase.Open(f.Db.Path, TimeSpan.FromSeconds(2))));
        var replay = await restarted.IntegrateAsync(workspace.Key, 0);
        Assert.Equal(result.AfterSha, replay.AfterSha);
        Assert.True(replay.Applied);
        // A durable intent before the Git effect follows the same recovery path.
        Sql(f.Db, "UPDATE prfactory_workspace_integrations SET applied=0");
        Git(workspace.LeadPath, "reset", "--hard", result.BeforeSha);
        Assert.Equal(result.AfterSha, (await restarted.IntegrateAsync(workspace.Key, 0)).AfterSha);
        var next = await restarted.IntegrateAsync(workspace.Key, 1);
        Assert.Equal(result.AfterSha, next.BeforeSha);
        Assert.Equal(second, next.ChildHead);
        Assert.Equal("one", File.ReadAllText(Path.Combine(workspace.LeadPath, "one.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(workspace.LeadPath, "two.txt")));
        Assert.Equal("", Git(workspace.LeadPath, "status", "--porcelain"));
        Assert.Equal("", Git(workspace.LeadPath, "merge-base", "--is-ancestor", first, "HEAD"));
        Assert.Equal("", Git(workspace.LeadPath, "merge-base", "--is-ancestor", second, "HEAD"));
    }

    [Fact]
    public async Task Conflicts_report_files_without_mutating_lead_and_dirty_children_are_retained()
    {
        using var f = new WorkspaceFixture();
        var workspace = await f.Workspaces.PrepareAsync(f.Request);
        Commit(workspace.Members[0].Path, "base.txt", "first");
        Commit(workspace.Members[1].Path, "base.txt", "second");
        File.WriteAllText(Path.Combine(workspace.Members[0].Path, "leftover.txt"), "do not stage");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workspaces.IntegrateAsync(workspace.Key, 0));
        Assert.Equal(f.BaseSha, JobWorktree.Head(workspace.LeadPath));
        Assert.True(File.Exists(Path.Combine(workspace.Members[0].Path, "leftover.txt")));
        File.Delete(Path.Combine(workspace.Members[0].Path, "leftover.txt"));
        var first = await f.Workspaces.IntegrateAsync(workspace.Key, 0);
        var conflict = await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Workspaces.IntegrateAsync(workspace.Key, 1));
        Assert.Contains("base.txt", conflict.Files);
        Assert.Equal(first.AfterSha, JobWorktree.Head(workspace.LeadPath));
        Assert.Equal("", Git(workspace.LeadPath, "status", "--porcelain"));
        Assert.Equal("second", File.ReadAllText(Path.Combine(workspace.Members[1].Path, "base.txt")));
    }

    [Fact]
    public async Task Scratch_is_private_persistent_and_documents_are_namespaced_and_contained()
    {
        using var f = new WorkspaceFixture();
        var request = f.Request with { RepositoryId = null, RepositoryPath = null, Remote = null, BaseBranch = null };
        var workspace = await f.Workspaces.PrepareAsync(request);
        Assert.Null(workspace.StartingSha);
        Assert.False(Directory.Exists(Path.Combine(workspace.LeadPath, ".git")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(workspace.Root));
        foreach (var child in workspace.Members)
        {
            File.WriteAllText(Path.Combine(child.Path, "plan.md"), child.Name);
            f.Workspaces.GatherDocuments(workspace.Key, child.Order, ["plan.md"]);
            f.Workspaces.GatherDocuments(workspace.Key, child.Order, ["plan.md"]);
            Assert.Equal(child.Name, File.ReadAllText(Path.Combine(workspace.StagingPath, "child-" + child.Order, "plan.md")));
        }
        Assert.Throws<InvalidOperationException>(() => f.Workspaces.GatherDocuments(workspace.Key, 0, ["../lead/secret.md"]));
        File.CreateSymbolicLink(Path.Combine(workspace.Members[0].Path, "link.md"), Path.Combine(workspace.Members[1].Path, "plan.md"));
        Assert.Throws<InvalidOperationException>(() => f.Workspaces.GatherDocuments(workspace.Key, 0, ["link.md"]));
        using var restarted = new TeamWorkspace(f.Store);
        Assert.Equal(workspace.LeadPath, (await restarted.PrepareAsync(request)).LeadPath);
    }

    [Fact]
    public void Explicit_job_start_must_be_sha_and_survives_reconstruction()
    {
        using var f = new WorkspaceFixture();
        var path = Path.Combine(f.Root.Path, "job");
        Assert.False(JobWorktree.Prepare(f.Repo, path, "job", "main"));
        Assert.True(JobWorktree.Prepare(f.Repo, path, "job", f.BaseSha));
        Commit(f.Repo, "later.txt", "later");
        var job = new JobRecord("job", "p", "t", "a", "key", "i", "{}", "queued", null, null, 0, "fake", f.Repo, null, null)
        { WorktreePath = path, WorktreeBranch = "job", WorktreeBase = f.BaseSha };
        Assert.True(JobWorktree.Prepare(job));
        Assert.Equal(f.BaseSha, JobWorktree.Head(path));
    }

    static string Commit(string repo, string file, string value)
    {
        File.WriteAllText(Path.Combine(repo, file), value);
        Git(repo, "add", "--", file);
        Git(repo, "-c", "user.name=Test", "-c", "user.email=test@localhost", "commit", "-m", file);
        return Git(repo, "rev-parse", "HEAD");
    }

    static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) { start.ArgumentList.Add(arg); }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout.Trim();
    }

    static void Sql(JobDatabase db, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={db.Path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    sealed class WorkspaceFixture : IDisposable
    {
        public TempStateDir Root { get; } = new();
        public string Repo { get; }
        public string Remote { get; }
        public string BaseSha { get; }
        public JobDatabase Db { get; }
        public PRFactoryWorkspaceStore Store { get; }
        public TeamWorkspace Workspaces { get; }
        public WorkspaceRequest Request { get; }

        public WorkspaceFixture()
        {
            Repo = Directory.CreateDirectory(Root.File("repo")).FullName;
            Remote = Root.File("remote.git");
            Git(Root.Path, "init", "--bare", Remote);
            Git(Repo, "init", "-b", "main");
            BaseSha = Commit(Repo, "base.txt", "base");
            Git(Repo, "remote", "add", "origin", Remote);
            Git(Repo, "push", "origin", "main");
            Db = JobDatabase.Create(Root.File("jobs.db"), TimeSpan.FromSeconds(2));
            Store = new(Db);
            Workspaces = new(Store);
            Request = new("server:item", Root.File("owned"), "repo-id", Repo, Remote, "main", "ticket", false, ["writer", "tester"]);
        }

        public void Dispose()
        {
            Workspaces.Dispose();
            Root.Dispose();
        }
    }
}
