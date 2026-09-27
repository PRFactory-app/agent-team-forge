using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

public sealed record BaseFreshnessResult(string RecordedBaseSha, string CurrentBaseSha, int CommitsBehind,
    string Action, string? HeadSha, string[] ConflictingPaths, bool AgentMayRun);

/// <summary>Single-repository phase-start refresh of an owned, quiescent lead checkout.</summary>
public sealed class PhaseBaseFreshness(PRFactoryHandoverStore store, TeamWorkspace workspaces, DurabilityCheckpoints? checkpoints = null)
{
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    static async Task<string> Git(string cwd, params string[] args) =>
        await JobWorktree.GitAsync(cwd, Timeout, CancellationToken.None, args)
        ?? throw new InvalidOperationException($"Git {args[0]} failed; workspace retained.");

    /// <summary>
    /// Rolls an interrupted refresh back to the recorded start. Run before the workspace is
    /// re-materialized: a half-rebased lead is not recognisable as the recorded checkout.
    /// </summary>
    public async Task<bool> RecoverAsync(string key)
    {
        if (store.Refresh(key) is not { State: "pending" } pending)
        {
            return false;
        }
        var workspace = workspaces.Get(key) ?? throw new InvalidOperationException("Workspace missing.");
        if (Directory.Exists(workspace.LeadPath))
        {
            await JobWorktree.GitAsync(workspace.LeadPath, Timeout, CancellationToken.None, "rebase", "--abort");
            await RequireBranch(workspace.LeadPath, workspace.InternalBranch!);
            await Git(workspace.LeadPath, "reset", "--hard", pending.OriginalSha);
        }
        // Before the first turn children only hold ATF's own alignment to the refreshed tip.
        foreach (var member in workspace.Members.Where(m => Directory.Exists(m.Path)))
        {
            var head = await Git(member.Path, "rev-parse", "HEAD");
            if (head == workspace.StartingSha) { continue; }
            await RequireBranch(member.Path, member.Branch!);
            if (await JobWorktree.GitAsync(member.Path, Timeout, CancellationToken.None,
                    "merge-base", "--is-ancestor", pending.CurrentBaseSha, head) is null
                || (await Git(member.Path, "status", "--porcelain", "--untracked-files=all")).Length != 0)
            {
                throw new InvalidOperationException("Child workspace changed during base refresh; retained for reconciliation.");
            }
            await Git(member.Path, "reset", "--hard", workspace.StartingSha!);
        }
        store.FinishRefresh(key, "restored");
        return true;
    }

    public async Task<BaseFreshnessResult> EnsureFreshAsync(WorkspaceSnapshot workspace, string? planBasisSha = null)
    {
        if (workspace.RepositoryPath is null || workspace.BaseSha is null || workspace.BaseBranch is null)
        {
            throw new InvalidOperationException("Base freshness requires a repository workspace.");
        }

        var cwd = workspace.LeadPath;
        await RecoverAsync(workspace.Key);
        // A completed refresh already moved the recorded base/start; re-read it.
        workspace = workspaces.Get(workspace.Key) ?? workspace;
        var recorded = workspace.BaseSha ?? throw new InvalidOperationException("Recorded base SHA missing.");
        if (store.Refresh(workspace.Key) is { State: "done", Action: not "None" } done)
        {
            var head = await Git(cwd, "rev-parse", "HEAD");
            return new(recorded, done.CurrentBaseSha, 0, done.Action, head, [], true);
        }
        foreach (var path in workspace.Members.Select(m => m.Path).Prepend(cwd))
        {
            if ((await Git(path, "status", "--porcelain", "--untracked-files=all")).Length != 0)
            {
                throw new InvalidOperationException("Base refresh requires clean committed workspaces.");
            }
        }

        await RequireBranch(cwd, workspace.InternalBranch!);
        await Git(cwd, "check-ref-format", "refs/heads/" + workspace.BaseBranch);
        // Owned ref: the mapped checkout's remote-tracking refs are shared and never touched.
        var baseRef = "refs/atf/base-refresh/" + Guid.NewGuid().ToString("N");
        await Git(cwd, "fetch", "--no-tags", "--no-write-fetch-head", "origin", "+refs/heads/" + workspace.BaseBranch + ":" + baseRef);
        var current = await Git(cwd, "rev-parse", "--verify", baseRef + "^{commit}");
        var original = await Git(cwd, "rev-parse", "HEAD");
        var behindText = await Git(cwd, "rev-list", "--count", recorded + ".." + current);
        var behind = int.Parse(behindText, System.Globalization.CultureInfo.InvariantCulture);
        if (planBasisSha is not null && (!JobWorktree.IsCommitSha(planBasisSha)
            || !planBasisSha.Equals(recorded, StringComparison.OrdinalIgnoreCase)
            || !planBasisSha.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            store.BeginRefresh(new(workspace.Key, original, current, "None", "pending"));
            store.FinishRefresh(workspace.Key, "checkpoint");
            return new(recorded, current, behind, "None", original, [], false);
        }
        if (current == recorded)
        {
            store.BeginRefresh(new(workspace.Key, original, current, "None", "pending"));
            store.FinishRefresh(workspace.Key, "done");
            return new(recorded, current, 0, "None", original, [], true);
        }

        var action = original == recorded ? "Fetched" : "Rebased";
        store.BeginRefresh(new(workspace.Key, original, current, action, "pending"));
        checkpoints?.Hit("base-refresh.after-intent");
        try
        {
            if (action == "Fetched")
            {
                await Git(cwd, "reset", "--hard", current);
            }
            else
            {
                await Git(cwd, "-c", "user.name=ATF", "-c", "user.email=atf@localhost",
                    "rebase", "--onto", current, recorded, workspace.InternalBranch!);
            }

            checkpoints?.Hit("base-refresh.after-mutation");
            var head = await Git(cwd, "rev-parse", "HEAD");
            await workspaces.AlignChildrenAsync(workspace, head);
            store.CompleteRefresh(workspace.Key, current, head);
            return new(recorded, current, behind, action, head, [], true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var conflicts = (await JobWorktree.GitAsync(cwd, Timeout, CancellationToken.None,
                "diff", "--name-only", "--diff-filter=U") ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await RecoverAsync(workspace.Key);
            if (conflicts.Length == 0)
            {
                throw;
            }

            return new(recorded, current, behind, "ConflictStopped", null, conflicts, false);
        }
    }

    static async Task RequireBranch(string cwd, string branch)
    {
        if (await Git(cwd, "symbolic-ref", "HEAD") != "refs/heads/" + branch)
        {
            throw new InvalidOperationException("Workspace branch changed; retained for reconciliation.");
        }
    }
}
