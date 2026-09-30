using System.Text.RegularExpressions;
using AgentTeamForge.Business.Features.Jobs.Publication;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

public sealed class WipPushException(string branch, string headSha, string message)
    : InvalidOperationException($"WIP push {branch}@{headSha} failed: {message}")
{
    public string Branch { get; } = branch;
    public string HeadSha { get; } = headSha;
}

/// <summary>The server refused the WIP report for this head (a 4xx other than lease/token); retrying cannot help.</summary>
public sealed class PRFactoryWipRejectedException(int status, string? error)
    : InvalidOperationException($"WIP publication rejected ({status}: {error})")
{
    public int Status { get; } = status;
    public string? Error { get; } = error;
}

public sealed class WipPublisher(PRFactoryHandoverStore store, PublicationAuthority authority)
{
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    static async Task<string> Git(string cwd, params string[] args) =>
        await JobWorktree.GitAsync(cwd, Timeout, CancellationToken.None, args)
        ?? throw new InvalidOperationException($"Git {args[0]} failed; WIP workspace retained.");

    public static string BranchName(string machineName, string ticketKey)
    {
        var slug = Regex.Replace(machineName.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0 || string.IsNullOrWhiteSpace(ticketKey))
        {
            throw new ArgumentException("Machine and ticket key are required for WIP publication.");
        }

        var branch = $"wip/{slug}/{ticketKey.Trim()}";
        if (branch.StartsWith('-') || branch.Contains(':') || branch.Contains(' '))
        {
            throw new ArgumentException("Invalid ticket key for WIP branch.");
        }

        return branch;
    }

    /// <summary>Push a committed lead tip and save the server's verified receipt. Never includes dirty buffers.</summary>
    public async Task<WipRecord> PublishAsync(Guid workItemId, WorkspaceSnapshot workspace, string branch,
        Func<string, string, Task<string>> report, bool allowRewrite = false, bool forceReport = false,
        CancellationToken ct = default)
    {
        if (workspace.RepositoryPath is null || workspace.Remote is null || workspace.InternalBranch is null)
        {
            throw new InvalidOperationException("WIP requires a Git workspace.");
        }

        var cwd = workspace.LeadPath;
        await Git(cwd, "check-ref-format", "refs/heads/" + branch);
        if (await Git(cwd, "symbolic-ref", "HEAD") != "refs/heads/" + workspace.InternalBranch)
        {
            throw new InvalidOperationException("Canonical lead branch changed.");
        }

        if (await Git(cwd, "remote", "get-url", "origin") != workspace.Remote)
        {
            throw new InvalidOperationException("WIP remote identity changed.");
        }

        var head = await Git(cwd, "rev-parse", "HEAD");
        var prior = store.Wip(workspace.Key);
        if (!forceReport && prior?.HeadSha == head && prior.State is "reported" or "rejected")
        {
            return prior;
        }

        var remoteRef = "refs/heads/" + branch;
        var remote = await RemoteHead(cwd, workspace.Remote, remoteRef);
        // An unconfirmed intent for an older head (failed push or lost report) is superseded only while
        // the remote still shows its lease value or our own push of it.
        if (prior is { State: "pending" } && prior.HeadSha != head && remote != prior.RemoteOldSha && remote != prior.HeadSha)
        {
            throw new InvalidOperationException("WIP remote changed during pending publication; reconciliation required.");
        }

        if (prior is { State: "reported" } && remote != prior.HeadSha && remote != head)
        {
            throw new InvalidOperationException("WIP remote changed since its receipt; reconciliation required.");
        }

        var intent = prior is { State: "pending" } && prior.HeadSha == head ? prior : new WipRecord(workspace.Key, branch, head, remote, "pending", null);
        store.SaveWip(intent);
        if (remote != head)
        {
            var allowed = await authority(workItemId, async () =>
            {
                var args = new List<string> { "push", "--no-follow-tags" };
                if (allowRewrite)
                {
                    args.Add("--force-with-lease=" + remoteRef + ":" + (intent.RemoteOldSha ?? ""));
                }

                args.Add("--"); args.Add(workspace.Remote); args.Add(head + ":" + remoteRef);
                try
                {
                    await Git(cwd, [.. args]);
                }
                catch (InvalidOperationException ex)
                {
                    throw new WipPushException(branch, head, ex.Message);
                }
            }, ct);
            if (!allowed)
            {
                throw new InvalidOperationException("WIP publication authority is not confirmed.");
            }
        }
        if (await RemoteHead(cwd, workspace.Remote, remoteRef) != head)
        {
            throw new InvalidOperationException("WIP remote SHA differs from frozen head.");
        }

        string receipt;
        try
        {
            receipt = await report(branch, head);
        }
        catch (PRFactoryWipRejectedException)
        {
            store.SaveWip(intent with { State = "rejected" });
            throw;
        }
        var done = intent with { State = "reported", Receipt = receipt };
        store.SaveWip(done);
        return done;
    }

    static async Task<string?> RemoteHead(string cwd, string remote, string reference)
    {
        var result = await Git(cwd, "ls-remote", "--refs", "--", remote, reference);
        if (result.Length == 0)
        {
            return null;
        }

        var fields = result.Split('\t');
        if (fields.Length != 2 || fields[1] != reference || !JobWorktree.IsCommitSha(fields[0]))
        {
            throw new InvalidOperationException("Ambiguous WIP remote ref.");
        }

        return fields[0];
    }
}
