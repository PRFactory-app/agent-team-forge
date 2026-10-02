using System.Diagnostics;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task Moving_base_refreshes_clean_owned_lead_without_touching_mapped_checkout()
    {
        using var f = new WorkspaceFixture();
        var workspace = await f.Workspaces.PrepareAsync(f.Request);
        var moved = Commit(f.Repo, "later.txt", "later");
        Git(f.Repo, "push", "origin", "main");
        var result = await new PhaseBaseFreshness(new PRFactoryHandoverStore(f.Db), f.Workspaces).EnsureFreshAsync(workspace);
        Assert.Equal("Fetched", result.Action);
        Assert.Equal(moved, JobWorktree.Head(workspace.LeadPath));
        Assert.All(workspace.Members, member => Assert.Equal(moved, JobWorktree.Head(member.Path)));
        Assert.Equal(moved, JobWorktree.Head(f.Repo));
        Assert.Equal("later", File.ReadAllText(Path.Combine(workspace.LeadPath, "later.txt")));
    }

    [Fact]
    public async Task Rebased_continuation_survives_restart_and_integrates_children()
    {
        using var f = new WorkspaceFixture();
        Git(f.Repo, "checkout", "-b", "implementation");
        var prior = Commit(f.Repo, "code.txt", "prior");
        Git(f.Repo, "push", "origin", "implementation");
        Git(f.Repo, "checkout", "main");
        var request = f.Request with { PriorBranch = "implementation", PriorSha = prior };
        var workspace = await f.Workspaces.PrepareAsync(request);
        var moved = Commit(f.Repo, "later.txt", "later");
        Git(f.Repo, "push", "origin", "main");
        var handovers = new PRFactoryHandoverStore(f.Db);
        var result = await new PhaseBaseFreshness(handovers, f.Workspaces).EnsureFreshAsync(workspace);
        Assert.Equal("Rebased", result.Action);
        Assert.All(workspace.Members, m => Assert.Equal(result.HeadSha, JobWorktree.Head(m.Path)));
        // The next tick re-materializes the recorded workspace; the rebased tip must still be recoverable.
        var again = await f.Workspaces.PrepareAsync(request);
        Assert.Equal(result.HeadSha, again.StartingSha);
        Assert.Equal(moved, again.BaseSha);
        Assert.Equal("later", File.ReadAllText(Path.Combine(again.LeadPath, "later.txt")));
        Assert.Equal("prior", File.ReadAllText(Path.Combine(again.LeadPath, "code.txt")));
        Commit(again.Members[0].Path, "one.txt", "one");
        Assert.True((await f.Workspaces.IntegrateAsync(again.Key, 0)).Applied);
        Assert.Equal("Rebased", (await new PhaseBaseFreshness(handovers, f.Workspaces).EnsureFreshAsync(again)).Action);
    }

    [Fact]
    public async Task Crash_after_rebase_before_receipt_restores_lead_and_children_before_rematerializing()
    {
        using var f = new WorkspaceFixture();
        Git(f.Repo, "checkout", "-b", "implementation");
        var prior = Commit(f.Repo, "code.txt", "prior");
        Git(f.Repo, "push", "origin", "implementation");
        Git(f.Repo, "checkout", "main");
        var request = f.Request with { PriorBranch = "implementation", PriorSha = prior };
        var workspace = await f.Workspaces.PrepareAsync(request);
        var moved = Commit(f.Repo, "later.txt", "later");
        Git(f.Repo, "push", "origin", "main");
        var handovers = new PRFactoryHandoverStore(f.Db);
        handovers.BeginRefresh(new(workspace.Key, prior, moved, "Rebased", "pending"));
        Git(workspace.LeadPath, "-c", "user.name=Test", "-c", "user.email=test@localhost",
            "rebase", "--onto", moved, f.BaseSha, workspace.InternalBranch!);
        var rebased = JobWorktree.Head(workspace.LeadPath)!;
        Git(workspace.Members[0].Path, "reset", "--hard", rebased);
        var freshness = new PhaseBaseFreshness(handovers, f.Workspaces);
        Assert.True(await freshness.RecoverAsync(workspace.Key));
        Assert.Equal(prior, JobWorktree.Head(workspace.LeadPath));
        Assert.All(workspace.Members, m => Assert.Equal(prior, JobWorktree.Head(m.Path)));
        var again = await f.Workspaces.PrepareAsync(request);
        Assert.Equal(prior, again.StartingSha);
        Assert.Equal("Rebased", (await freshness.EnsureFreshAsync(again)).Action);
        Assert.Equal(moved, f.Workspaces.Get(workspace.Key)!.BaseSha);
    }

    [Fact]
    public async Task Moving_base_conflict_aborts_rebase_and_restores_lead_head()
    {
        using var f = new WorkspaceFixture();
        var workspace = await f.Workspaces.PrepareAsync(f.Request);
        var original = Commit(workspace.LeadPath, "base.txt", "lead edit");
        Commit(f.Repo, "base.txt", "remote edit");
        Git(f.Repo, "push", "origin", "main");
        var result = await new PhaseBaseFreshness(new PRFactoryHandoverStore(f.Db), f.Workspaces).EnsureFreshAsync(workspace);
        Assert.Equal("ConflictStopped", result.Action);
        Assert.Contains("base.txt", result.ConflictingPaths);
        Assert.Equal(original, JobWorktree.Head(workspace.LeadPath));
        Assert.Equal("lead edit", File.ReadAllText(Path.Combine(workspace.LeadPath, "base.txt")));
        Assert.False(Directory.Exists(Path.Combine(Git(workspace.LeadPath, "rev-parse", "--git-dir"), "rebase-merge")));
    }

    [Fact]
    public async Task Approved_plan_basis_drift_stops_before_mutating_checkout()
    {
        using var f = new WorkspaceFixture();
        var workspace = await f.Workspaces.PrepareAsync(f.Request);
        Commit(f.Repo, "later.txt", "later");
        Git(f.Repo, "push", "origin", "main");
        var result = await new PhaseBaseFreshness(new PRFactoryHandoverStore(f.Db), f.Workspaces)
            .EnsureFreshAsync(workspace, f.BaseSha);
        Assert.False(result.AgentMayRun);
        Assert.Equal(f.BaseSha, JobWorktree.Head(workspace.LeadPath));
    }

    [Fact]
    public async Task Pending_rebase_after_crash_is_aborted_and_original_commit_is_retained()
    {
        using var f = new WorkspaceFixture();
        var workspace = await f.Workspaces.PrepareAsync(f.Request);
        var original = Commit(workspace.LeadPath, "base.txt", "lead edit");
        var moved = Commit(f.Repo, "base.txt", "remote edit");
        Git(f.Repo, "push", "origin", "main");
        var handovers = new PRFactoryHandoverStore(f.Db);
        handovers.BeginRefresh(new(workspace.Key, original, moved, "Rebased", "pending"));
        await JobWorktree.GitAsync(workspace.LeadPath, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken,
            "rebase", "--onto", moved, f.BaseSha, workspace.InternalBranch!);
        var result = await new PhaseBaseFreshness(handovers, f.Workspaces).EnsureFreshAsync(workspace);
        Assert.Equal("ConflictStopped", result.Action);
        Assert.Equal(original, JobWorktree.Head(workspace.LeadPath));
        Assert.Equal("restored", handovers.Refresh(workspace.Key)!.State);
    }

    [Fact]
    public async Task Wip_tip_is_adopted_at_exact_sha_from_second_state_root()
    {
        using var f = new WorkspaceFixture();
        var first = await f.Workspaces.PrepareAsync(f.Request);
        var committed = Commit(first.LeadPath, "work.txt", "saved");
        var firstStore = new PRFactoryHandoverStore(f.Db);
        var publisher = new WipPublisher(firstStore, async (_, effect, _) => { await effect(); return true; });
        var branch = WipPublisher.BranchName("Machine One", "PRF-7");
        var published = await publisher.PublishAsync(Guid.NewGuid(), first, branch, (_, sha) => Task.FromResult("receipt:" + sha),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(committed, published.HeadSha);
        using var secondRoot = new TempStateDir();
        var secondRepo = Directory.CreateDirectory(secondRoot.File("repo")).FullName;
        Git(secondRepo, "init", "-b", "main");
        Git(secondRepo, "remote", "add", "origin", f.Remote);
        var secondDb = JobDatabase.Create(secondRoot.File("jobs.db"), TimeSpan.FromSeconds(2));
        using var second = new TeamWorkspace(new PRFactoryWorkspaceStore(secondDb));
        var adopted = await second.PrepareAsync(f.Request with
        {
            Key = "second:item",
            OwnedRoot = secondRoot.File("owned"),
            RepositoryPath = secondRepo,
            StartFromBranch = branch,
            StartCommitSha = committed,
            ExactWipTip = true
        });
        Assert.Equal(committed, adopted.StartingSha);
        Assert.Equal("saved", File.ReadAllText(Path.Combine(adopted.LeadPath, "work.txt")));
        var newer = Commit(first.LeadPath, "other.txt", "later");
        Git(first.LeadPath, "push", "--force", "origin", newer + ":refs/heads/" + branch);
        var moved = f.Request with
        {
            Key = "third:item",
            OwnedRoot = secondRoot.File("owned"),
            RepositoryPath = secondRepo,
            StartFromBranch = branch,
            StartCommitSha = committed,
            ExactWipTip = true
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.PrepareAsync(moved));
        // Without base-wip-v1 a moved wip/* branch still resumes the recorded SHA as before.
        var legacy = await second.PrepareAsync(moved with { Key = "legacy:item", ExactWipTip = false });
        Assert.Equal(committed, legacy.StartingSha);
    }

    [Fact]
    public async Task Retry_from_plan_base_replaces_stale_wip_under_lease()
    {
        using var f = new WorkspaceFixture();
        var store = new PRFactoryHandoverStore(f.Db);
        var publisher = new WipPublisher(store, async (_, effect, _) => { await effect(); return true; });
        var branch = WipPublisher.BranchName("Machine One", "PRF-7");
        var cancelled = await f.Workspaces.PrepareAsync(f.Request);
        var stale = Commit(cancelled.LeadPath, "work.txt", "attempt one");
        await publisher.PublishAsync(Guid.NewGuid(), cancelled, branch, (_, sha) => Task.FromResult("receipt:" + sha),
            ct: TestContext.Current.CancellationToken);
        // Retry: a new acceptance of the same ticket starts again from the plan base, not from the WIP.
        var retry = await f.Workspaces.PrepareAsync(f.Request with { Key = "server:retry" });
        Assert.Equal(f.BaseSha, retry.StartingSha);
        var fresh = Commit(retry.LeadPath, "work.txt", "attempt two");
        var published = await publisher.PublishAsync(Guid.NewGuid(), retry, branch, (_, sha) => Task.FromResult("receipt:" + sha),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(fresh, published.HeadSha);
        Assert.Equal(stale, published.RemoteOldSha);
        Assert.StartsWith(fresh, Git(f.Repo, "ls-remote", "origin", "refs/heads/" + branch));
        // The cancelled attempt keeps its commit locally but can no longer move the replaced ref.
        Assert.Equal(stale, JobWorktree.Head(cancelled.LeadPath));
        Commit(cancelled.LeadPath, "more.txt", "late");
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(Guid.NewGuid(), cancelled, branch,
            (_, sha) => Task.FromResult("receipt:" + sha), ct: TestContext.Current.CancellationToken));
        Assert.StartsWith(fresh, Git(f.Repo, "ls-remote", "origin", "refs/heads/" + branch));
    }

    [Fact]
    public async Task Dirty_release_is_refused_and_old_server_without_capability_is_not_called()
    {
        using var f = new WorkspaceFixture();
        var workspace = await f.Workspaces.PrepareAsync(f.Request);
        var calls = 0;
        using var http = PRFactoryClient.CreateHttpClient("https://example.test", "token", new StubHandler(request =>
        {
            calls++;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{\"capabilities\":[\"base-wip-v1\"]}")
            };
        }));
        var client = new PRFactoryClient(http);
        File.WriteAllText(Path.Combine(workspace.LeadPath, "dirty.txt"), "uncommitted");
        var item = new PRFactoryWorkItem { Id = Guid.NewGuid(), RepositoryId = Guid.NewGuid(), LeaseToken = Guid.NewGuid() };
        var handover = new PRFactoryHandover(client, new PRFactoryHandoverStore(f.Db), Permit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handover.ReleaseAsync(item, workspace, Guid.NewGuid(), "job", "move", () => true, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        using var oldHttp = PRFactoryClient.CreateHttpClient("https://example.test", "token", new StubHandler(_ =>
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound)));
        var oldHandover = new PRFactoryHandover(new PRFactoryClient(oldHttp), new PRFactoryHandoverStore(f.Db), Permit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldHandover.ReleaseAsync(item, workspace, Guid.NewGuid(), "job", "move", () => true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lost_release_reply_retries_same_identity_and_cleanup_waits_for_receipt()
    {
        using var f = new WorkspaceFixture();
        var workspace = await f.Workspaces.PrepareAsync(f.Request);
        var handoverStore = new PRFactoryHandoverStore(f.Db);
        var item = new PRFactoryWorkItem { Id = Guid.NewGuid(), RepositoryId = Guid.NewGuid(), LeaseToken = Guid.NewGuid() };
        var publisher = new WipPublisher(handoverStore, async (_, effect, _) => { await effect(); return true; });
        var branch = WipPublisher.BranchName("Machine One", "PRF-7");
        var head = JobWorktree.Head(workspace.LeadPath)!;
        await Assert.ThrowsAsync<HttpRequestException>(() => publisher.PublishAsync(item.Id, workspace, branch,
            (_, _) => throw new HttpRequestException("lost report reply"), ct: TestContext.Current.CancellationToken));
        Assert.Equal(head, Git(f.Repo, "ls-remote", "--heads", "origin", "refs/heads/" + branch).Split('\t')[0]);
        // The lead keeps committing while the receipt is unconfirmed; the stale intent is superseded.
        head = Commit(workspace.LeadPath, "more.txt", "more");
        var wip = await publisher.PublishAsync(item.Id, workspace, branch, (_, _) => Task.FromResult("server-receipt"),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal("reported", wip.State);
        Assert.Equal(head, wip.HeadSha);
        Assert.Equal(head, Git(f.Repo, "ls-remote", "--heads", "origin", "refs/heads/" + branch).Split('\t')[0]);
        var releaseId = $"{item.Id:D}:{item.LeaseToken:D}:{head}";
        var releaseCalls = 0;
        using var http = PRFactoryClient.CreateHttpClient("https://example.test", "token", new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("capabilities", StringComparison.Ordinal))
            {
                return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("{\"capabilities\":[\"base-wip-v1\"]}") };
            }

            releaseCalls++;
            if (releaseCalls == 1)
            {
                throw new HttpRequestException("lost release reply");
            }

            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{\"released\":true,\"releaseId\":\"" + releaseId
                    + "\",\"verifiedWipSha\":\"" + head + "\"}")
            };
        }));
        var handover = new PRFactoryHandover(new PRFactoryClient(http), handoverStore, Permit);
        var machine = Guid.NewGuid();
        await Assert.ThrowsAsync<HttpRequestException>(() => handover.ReleaseAsync(item, workspace, machine, "job", "move", () => true, TestContext.Current.CancellationToken));
        Assert.Null(handoverStore.Release(workspace.Key));
        var acknowledged = await handover.ReleaseAsync(item, workspace, machine, "job", "move", () => true, TestContext.Current.CancellationToken);
        Assert.Equal(releaseId, acknowledged.ReleaseId);
        Assert.Equal(2, releaseCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handover.CleanupReleasedAsync(workspace, () => false, TimeSpan.Zero));
        await handover.CleanupReleasedAsync(workspace, () => true, TimeSpan.Zero);
        Assert.False(Directory.Exists(workspace.Root));
        Assert.True(Directory.Exists(f.Repo));
    }

    sealed class StubHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> send) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    static async Task<bool> Permit(Guid _, Func<Task> effect, CancellationToken __)
    {
        await effect();
        return true;
    }
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
        var localOnly = Commit(f.Repo, "local.txt", "never pushed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workspaces.PrepareAsync(request with { Key = "bad", PriorSha = localOnly }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workspaces.PrepareAsync(request with { Key = "missing", PriorSha = null }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workspaces.PrepareAsync(request with { Key = "missing-handover", PriorBranch = null, StartCommitSha = null }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workspaces.PrepareAsync(f.Request with { Key = "remote", Remote = f.Remote + "-wrong" }));
        Assert.Null(f.Store.Get("remote"));
    }

    [Fact]
    public async Task Exact_continuation_sha_is_fetched_after_force_push_or_branch_deletion()
    {
        using var f = new WorkspaceFixture();
        Git(f.Repo, "checkout", "-b", "implementation");
        var accepted = Commit(f.Repo, "code.txt", "accepted");
        Git(f.Repo, "push", "origin", "implementation");
        Git(f.Repo, "tag", "keep-remote-object", accepted);
        Git(f.Repo, "push", "origin", "keep-remote-object");
        Git(f.Repo, "reset", "--hard", f.BaseSha);
        Commit(f.Repo, "rewritten.txt", "force-pushed");
        Git(f.Repo, "push", "--force", "origin", "implementation");
        // The user's checkout no longer has the commit; only the remote does.
        Git(f.Repo, "tag", "-d", "keep-remote-object");
        Git(f.Repo, "reflog", "expire", "--expire=now", "--all");
        Git(f.Repo, "gc", "--prune=now", "--quiet");
        var request = f.Request with { PriorBranch = "implementation", PriorSha = accepted };
        var forced = await f.Workspaces.PrepareAsync(request with { Key = "forced" });
        Assert.Equal(accepted, JobWorktree.Head(forced.LeadPath));
        Git(f.Repo, "push", "origin", "--delete", "implementation");
        var deleted = await f.Workspaces.PrepareAsync(request with { Key = "deleted" });
        Assert.Equal(accepted, JobWorktree.Head(deleted.LeadPath));
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
