using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs.Publication;

/// <summary>
/// Projection of slice 5's persisted canonical workspace, not a workspace creator.
/// Caller must quiesce all writers and finish integration/finalization before calling.
/// </summary>
public sealed record PublicationRequest(string PublicationId, string Server, Guid WorkItemId, Guid LeaseToken,
    string MachineId, string JobId, string RepositoryId, string WorkspaceKey, string LeadPath,
    string Remote, string InternalBranch, string PublishBranch, string BaseSha,
    string Type, bool ReadOnly = false, bool ProjectInit = false, string? ArtefactFolder = null);

/// <summary>Return false without invoking the effect if current server authority is unconfirmed/fenced.</summary>
public delegate Task<bool> PublicationAuthority(Guid workItemId, Func<Task> effect, CancellationToken cancellationToken);

public sealed class BranchPublisher(PRFactoryPublicationStore store, PublicationAuthority authority,
    DurabilityCheckpoints? checkpoints = null) : IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    public void Dispose() => gate.Dispose();

    public static bool ShouldPublish(PublicationRequest request) => !request.ReadOnly &&
        (request.ProjectInit || request.Type is "Implementation" or "CodeReview" or "CustomStep");

    public async Task<PublicationReceipt?> PublishAsync(PublicationRequest request, CancellationToken ct = default)
    {
        if (!ShouldPublish(request)) { return null; }
        await gate.WaitAsync(ct);
        try
        {
            var cwd = request.LeadPath;
            await Git(cwd, ct, "check-ref-format", "refs/heads/" + request.PublishBranch);
            await Git(cwd, ct, "check-ref-format", "refs/heads/" + request.InternalBranch);
            await ValidateRemote(request, ct);
            if (await Git(cwd, ct, "symbolic-ref", "HEAD") != "refs/heads/" + request.InternalBranch)
            {
                throw new InvalidOperationException("Canonical lead branch changed.");
            }
            var dirty = string.Join('\n', (await Git(cwd, ct, "status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=none"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !IsStagedArtefact(line, request.ArtefactFolder)));
            if (dirty.Length != 0) { throw new InvalidOperationException("Publication requires committed output. Dirty files:\n" + dirty); }
            var head = await Git(cwd, ct, "rev-parse", "--verify", "HEAD^{commit}");
            var prior = store.Get(request.PublicationId);
            var remoteHead = await RemoteHead(request, ct);
            var intent = new PublicationIntent(request.PublicationId, request.Server, request.WorkItemId, request.LeaseToken,
                request.MachineId, request.JobId, request.RepositoryId, request.WorkspaceKey, cwd, request.Remote,
                request.InternalBranch, request.PublishBranch, request.BaseSha, head, prior is null ? remoteHead : prior.Intent.ExpectedRemoteSha);
            var saved = store.SaveIntent(intent); // FULL SQLite durability before any push.
            checkpoints?.Hit("publication.after-intent");
            if (remoteHead != head)
            {
                // An acknowledged receipt is historical evidence, not permission to overwrite later work.
                if (saved.VerifiedAt is not null) { throw new InvalidOperationException("Published remote SHA changed; reconciliation required."); }
                await ValidateRemote(request, ct);
                var allowed = await authority(request.WorkItemId, async () =>
                {
                    // Pin the source SHA: a moved local branch cannot silently change this push's payload.
                    await Git(cwd, ct, "push", "--no-follow-tags", "--", request.Remote,
                        head + ":refs/heads/" + request.PublishBranch);
                }, ct);
                if (!allowed) { throw new InvalidOperationException("Publication authority is not confirmed."); }
                checkpoints?.Hit("publication.after-push");
            }
            // Record evidence even if authority was fenced while an already admitted push ran.
            // Completion/upload still requires a separate fresh authority check.
            if (await RemoteHead(request, CancellationToken.None) != head)
            {
                throw new InvalidOperationException("Remote SHA does not match frozen publication head; reconciliation required.");
            }
            checkpoints?.Hit("publication.before-receipt");
            return store.Verify(intent);
        }
        finally { gate.Release(); }
    }

    // Untracked phase documents and allowlisted attachment outputs are uploaded separately.
    // Tracked changes there still count as dirty.
    internal static bool IsStagedArtefact(string porcelain, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !porcelain.StartsWith("?? ", StringComparison.Ordinal)) { return false; }
        var prefix = folder.Replace('\\', '/').Trim('/') + "/";
        var path = porcelain[3..].Trim('"');
        // Only the exact collector locations qualify. Never hide source files or arbitrary
        // nested directories merely because the claim names their ancestor as the folder.
        if (prefix == "/" || Path.IsPathRooted(folder) || prefix.Contains("..", StringComparison.Ordinal)
            || !path.StartsWith(prefix, StringComparison.Ordinal)) { return false; }
        var relative = path[prefix.Length..];
        return !relative.Contains('/') && Path.GetExtension(path).ToLowerInvariant() is ".md" or ".html"
            || relative.StartsWith("attachments/", StringComparison.Ordinal)
            && !relative["attachments/".Length..].Contains('/') && AttachmentFiles.MediaType(path) is not null;
    }

    static async Task ValidateRemote(PublicationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Remote) || request.Remote.StartsWith('-') ||
            await Git(request.LeadPath, ct, "remote", "get-url", "--all", "origin") != request.Remote ||
            await Git(request.LeadPath, ct, "remote", "get-url", "--push", "--all", "origin") != request.Remote)
        {
            throw new InvalidOperationException("Publication remote identity mismatch.");
        }
        // Match worker's invariant: local destinations must be bare, never another working checkout.
        var local = request.Remote.StartsWith("file://", StringComparison.Ordinal) ? new Uri(request.Remote).LocalPath : request.Remote;
        if (!local.Contains(':') && !local.Contains("://", StringComparison.Ordinal))
        {
            var path = Path.GetFullPath(local, request.LeadPath);
            if (await Git(path, ct, "rev-parse", "--is-bare-repository") != "true")
            {
                throw new InvalidOperationException("Local publication remote must be bare.");
            }
        }
    }

    static async Task<string?> RemoteHead(PublicationRequest request, CancellationToken ct)
    {
        var reference = "refs/heads/" + request.PublishBranch;
        var output = await Git(request.LeadPath, ct, "ls-remote", "--refs", "--", request.Remote, reference);
        if (output.Length == 0) { return null; }
        var parts = output.Split('\t');
        if (parts.Length != 2 || parts[1] != reference) { throw new InvalidOperationException("Ambiguous remote publication ref."); }
        return parts[0];
    }

    static async Task<string> Git(string cwd, CancellationToken ct, params string[] args) =>
        await JobWorktree.GitAsync(cwd, TimeSpan.FromMinutes(2), ct, args) ??
        throw new InvalidOperationException($"Git {args[0]} failed; publication retained for retry.");
}
