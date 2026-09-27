using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

public sealed record BaseFreshnessResult(string RecordedBaseSha, string CurrentBaseSha, int CommitsBehind,
    string Action, string? HeadSha, string[] ConflictingPaths, bool AgentMayRun);

/// <summary>Single-repository phase-start refresh of an owned, quiescent lead checkout.</summary>
public sealed class PhaseBaseFreshness(PRFactoryHandoverStore store, DurabilityCheckpoints? checkpoints = null)
{
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    static async Task<string> Git(string cwd, params string[] args) =>
        await JobWorktree.GitAsync(cwd, Timeout, CancellationToken.None, args)
        ?? throw new InvalidOperationException($"Git {args[0]} failed; workspace retained.");

    public async Task<BaseFreshnessResult> EnsureFreshAsync(WorkspaceSnapshot workspace, string? planBasisSha = null)
    {
        if (workspace.RepositoryPath is null || workspace.BaseSha is null || workspace.BaseBranch is null)
        {
            throw new InvalidOperationException("Base freshness requires a repository workspace.");
        }

        var cwd = workspace.LeadPath;
        var pending = store.Refresh(workspace.Key);
        if (pending?.State == "pending")
        {
            // Covers a crash anywhere after the intent, including mid-rebase. Preserve the original
            // committed head and never allow an unfinished rebase to reach a new agent turn.
            await AbortAndRestore(cwd, pending.OriginalSha);
            store.FinishRefresh(workspace.Key, "restored");
        }
        else if (pending is { State: "done", Action: not "None" })
        {
            var head = await Git(cwd, "rev-parse", "HEAD");
            return new(workspace.BaseSha, pending.CurrentBaseSha, 0, pending.Action, head, [], true);
        }
        if ((await Git(cwd, "status", "--porcelain", "--untracked-files=all")).Length != 0)
        {
            throw new InvalidOperationException("Base refresh requires a clean committed lead checkout.");
        }

        if (await Git(cwd, "symbolic-ref", "HEAD") != "refs/heads/" + workspace.InternalBranch)
        {
            throw new InvalidOperationException("Lead workspace branch changed.");
        }

        await Git(cwd, "check-ref-format", "refs/heads/" + workspace.BaseBranch);
        var baseRef = "refs/remotes/origin/" + workspace.BaseBranch;
        await Git(cwd, "fetch", "--no-tags", "origin", "refs/heads/" + workspace.BaseBranch + ":" + baseRef);
        var current = await Git(cwd, "rev-parse", baseRef);
        var original = await Git(cwd, "rev-parse", "HEAD");
        var behindText = await Git(cwd, "rev-list", "--count", workspace.BaseSha + ".." + current);
        var behind = int.Parse(behindText, System.Globalization.CultureInfo.InvariantCulture);
        if (planBasisSha is not null && (!JobWorktree.IsCommitSha(planBasisSha)
            || !planBasisSha.Equals(workspace.BaseSha, StringComparison.OrdinalIgnoreCase)
            || !planBasisSha.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            store.BeginRefresh(new(workspace.Key, original, current, "None", "pending"));
            store.FinishRefresh(workspace.Key, "checkpoint");
            return new(workspace.BaseSha, current, behind, "None", original, [], false);
        }
        if (current == workspace.BaseSha)
        {
            store.BeginRefresh(new(workspace.Key, original, current, "None", "pending"));
            store.FinishRefresh(workspace.Key, "done");
            return new(workspace.BaseSha, current, 0, "None", original, [], true);
        }

        var action = original == workspace.BaseSha ? "Fetched" : "Rebased";
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
                await Git(cwd, "rebase", "--onto", current, workspace.BaseSha, workspace.InternalBranch!);
            }

            checkpoints?.Hit("base-refresh.after-mutation");
            var head = await Git(cwd, "rev-parse", "HEAD");
            store.FinishRefresh(workspace.Key, "done");
            return new(workspace.BaseSha, current, behind, action, head, [], true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var conflicts = (await JobWorktree.GitAsync(cwd, Timeout, CancellationToken.None,
                "diff", "--name-only", "--diff-filter=U") ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await AbortAndRestore(cwd, original);
            store.FinishRefresh(workspace.Key, "restored");
            if (conflicts.Length == 0)
            {
                throw;
            }

            return new(workspace.BaseSha, current, behind, "ConflictStopped", null, conflicts, false);
        }
    }

    static async Task AbortAndRestore(string cwd, string original)
    {
        await JobWorktree.GitAsync(cwd, Timeout, CancellationToken.None, "rebase", "--abort");
        await Git(cwd, "reset", "--hard", original);
    }
}
