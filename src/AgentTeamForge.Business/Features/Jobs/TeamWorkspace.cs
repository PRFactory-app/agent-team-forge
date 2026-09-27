using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Files;

namespace AgentTeamForge.Business.Features.Jobs;

public sealed record WorkspaceRequest(string Key, string OwnedRoot, string? RepositoryId, string? RepositoryPath,
    string? Remote, string? BaseBranch, string? PublishBranch, bool ReadOnly, string[] Members,
    string? PriorBranch = null, string? PriorSha = null, string? StartFromBranch = null,
    string? StartCommitSha = null, bool ProjectInit = false, string? ExpectedBaseSha = null);

/// <summary>One daemon owns these operations. Call only while lead and children are quiescent.</summary>
public sealed class TeamWorkspace(PRFactoryWorkspaceStore store) : IDisposable
{
    readonly SemaphoreSlim gate = new(1);
    static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(10);
    public void Dispose() => gate.Dispose();

    internal static async Task<string> Git(string cwd, params string[] args) =>
        await JobWorktree.GitAsync(cwd, GitTimeout, CancellationToken.None, args) ??
        throw new InvalidOperationException($"Git {args[0]} failed; workspace retained.");

    public WorkspaceSnapshot? Get(string key) => store.Get(key);

    /// <summary>The mapped checkout's configured origin; recorded once, then required to stay identical.</summary>
    public static Task<string> OriginAsync(string repository) => Git(repository, "remote", "get-url", "origin");

    /// <summary>The remote's default branch (its HEAD symref), used when the mapping names no base branch.</summary>
    public static async Task<string> DefaultBranchAsync(string repository)
    {
        var symref = await Git(repository, "ls-remote", "--symref", "origin", "HEAD");
        var line = symref.Split('\n').FirstOrDefault(l => l.StartsWith("ref: refs/heads/", StringComparison.Ordinal));
        return line?["ref: refs/heads/".Length..].Split('\t')[0]
            ?? throw new InvalidOperationException("Remote default branch unknown; configure the mapping's base branch.");
    }

    public async Task<WorkspaceSnapshot> PrepareAsync(WorkspaceRequest request)
    {
        await gate.WaitAsync();
        try
        {
            var saved = store.Get(request.Key);
            if (saved is not null)
            {
                if (saved.RepositoryId != request.RepositoryId || saved.Remote != request.Remote ||
                    saved.RepositoryPath != request.RepositoryPath || saved.ReadOnly != request.ReadOnly ||
                    !saved.Members.Select(m => m.Name).SequenceEqual(request.Members))
                {
                    throw new InvalidOperationException("Workspace request changed after acceptance.");
                }
                await Materialize(saved);
                return saved;
            }
            if (request.Members.Distinct(StringComparer.Ordinal).Count() != request.Members.Length)
            {
                throw new InvalidOperationException("Duplicate workspace member.");
            }
            var token = Guid.NewGuid().ToString("N");
            var root = Path.Combine(Path.GetFullPath(request.OwnedRoot), token);
            var branch = "atf/team/" + token;
            string? baseSha = null, start = null, selected = null;
            if (request.RepositoryId is not null)
            {
                var repo = request.RepositoryPath ?? throw new InvalidOperationException("Repository mapping missing.");
                await ValidateRemote(repo, request.Remote);
                selected = request.BaseBranch ?? throw new InvalidOperationException("Base branch missing.");
                baseSha = await Fetch(repo, selected);
                VerifySha(request.ExpectedBaseSha, baseSha);
                start = baseSha;
                if (request.PriorBranch is not null)
                {
                    start = await Fetch(repo, request.PriorBranch);
                    VerifyRequiredSha(request.PriorSha, start);
                }
                else if (request.StartFromBranch is not null)
                {
                    start = await Fetch(repo, request.StartFromBranch);
                    VerifyRequiredSha(request.StartCommitSha, start);
                }
                else if (request.ProjectInit && request.PublishBranch is not null)
                {
                    await CheckBranch(repo, request.PublishBranch);
                    var remote = await Git(repo, "ls-remote", "--heads", "origin", "refs/heads/" + request.PublishBranch);
                    if (remote.Length > 0) { start = await Fetch(repo, request.PublishBranch); }
                    VerifySha(request.StartCommitSha, start);
                }
                else { VerifySha(request.StartCommitSha, start); }
            }
            else if (request.RepositoryPath is not null || request.Remote is not null)
            {
                throw new InvalidOperationException("Scratch workspace cannot carry a repository mapping.");
            }
            var snapshot = new WorkspaceSnapshot(request.Key, request.RepositoryId, request.Remote, request.RepositoryPath,
                selected, baseSha, start, start is null ? null : branch, request.PublishBranch,
                root, Path.Combine(root, "lead"), Path.Combine(root, "documents"), request.ReadOnly,
                [.. request.Members.Select((name, order) => new WorkspaceMember(name, order,
                    Path.Combine(root, "child-" + order), start is null ? null : branch + "-child-" + order))]);
            store.Save(snapshot); // All ownership and immutable Git choices precede checkout/dispatch.
            await Materialize(snapshot);
            return snapshot;
        }
        finally { gate.Release(); }
    }

    static async Task Materialize(WorkspaceSnapshot snapshot)
    {
        PrivateFiles.CreateDirectory(snapshot.Root);
        PrivateFiles.CreateDirectory(snapshot.StagingPath);
        if (snapshot.RepositoryPath is null)
        {
            PrivateFiles.CreateDirectory(snapshot.LeadPath);
            foreach (var member in snapshot.Members) { PrivateFiles.CreateDirectory(member.Path); }
            return;
        }
        await ValidateRemote(snapshot.RepositoryPath, snapshot.Remote);
        if (!JobWorktree.Prepare(snapshot.RepositoryPath, snapshot.LeadPath, snapshot.InternalBranch!, snapshot.StartingSha!))
        {
            throw new InvalidOperationException("Lead workspace could not be recovered.");
        }
        foreach (var member in snapshot.Members)
        {
            if (!JobWorktree.Prepare(snapshot.RepositoryPath, member.Path, member.Branch!, snapshot.StartingSha!))
            {
                throw new InvalidOperationException("Child workspace could not be recovered.");
            }
        }
    }

    static async Task ValidateRemote(string repo, string? expected)
    {
        // Strict identity is intentional: aliases must be approved in the mapping, not guessed.
        if (string.IsNullOrWhiteSpace(expected) || await Git(repo, "remote", "get-url", "origin") != expected)
        {
            throw new InvalidOperationException("Mapped repository remote identity mismatch.");
        }
    }

    static async Task CheckBranch(string repo, string branch) =>
        _ = await Git(repo, "check-ref-format", "refs/heads/" + branch);

    static async Task<string> Fetch(string repo, string branch)
    {
        await CheckBranch(repo, branch);
        // FETCH_HEAD is shared with the user's checkout and other fetches.
        // Keep an owned ref as both an unambiguous result and a recovery object pin.
        var fetchedRef = "refs/atf/workspace-start/" + Guid.NewGuid().ToString("N");
        await Git(repo, "fetch", "--no-tags", "--no-write-fetch-head", "origin", "refs/heads/" + branch + ":" + fetchedRef);
        return await Git(repo, "rev-parse", "--verify", fetchedRef + "^{commit}");
    }

    static void VerifyRequiredSha(string? expected, string actual)
    {
        if (expected is null) { throw new InvalidOperationException("Continuation requires an authoritative starting SHA."); }
        VerifySha(expected, actual);
    }

    static void VerifySha(string? expected, string actual)
    {
        if (expected is not null && (!JobWorktree.IsCommitSha(expected) || !expected.Equals(actual, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Fetched head does not match the expected SHA.");
        }
    }

    public async Task<WorkspaceIntegration> IntegrateAsync(string key, int memberOrder, string? artefactFolder = null)
    {
        await gate.WaitAsync();
        try
        {
            var workspace = store.Get(key) ?? throw new InvalidOperationException("Workspace missing.");
            if (workspace.RepositoryPath is null || workspace.ReadOnly) { throw new InvalidOperationException("Workspace is not writable Git output."); }
            var member = workspace.Members.Single(m => m.Order == memberOrder);
            foreach (var previous in workspace.Members.Where(m => m.Order < memberOrder))
            {
                if (store.Integration(key, previous.Order)?.Applied != true) { throw new InvalidOperationException("Children must integrate in declared order."); }
            }
            await ValidateRemote(workspace.RepositoryPath, workspace.Remote);
            await Clean(workspace.LeadPath, artefactFolder);
            await Clean(member.Path, artefactFolder);
            if (JobWorktree.Branch(workspace.LeadPath) != workspace.InternalBranch || JobWorktree.Branch(member.Path) != member.Branch)
            {
                throw new InvalidOperationException("Workspace branch changed.");
            }
            var child = await Git(member.Path, "rev-parse", "HEAD");
            var head = await Git(workspace.LeadPath, "rev-parse", "HEAD");
            var intent = store.Integration(key, memberOrder);
            if (intent is null)
            {
                await Git(member.Path, "merge-base", "--is-ancestor", workspace.StartingSha!, child);
                // Compute in Git's object database. A conflict never dirties the canonical checkout.
                var merged = await JobWorktree.GitOutputAsync(workspace.LeadPath, GitTimeout, true, CancellationToken.None,
                    "merge-tree", "--write-tree", "--name-only", "--no-messages", head, child) ?? "";
                var lines = merged.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length != 1 || !JobWorktree.IsCommitSha(lines[0]))
                {
                    throw new WorkspaceConflictException([.. lines.Skip(1)]);
                }
                var after = head == child ? head : await Git(workspace.LeadPath, "-c", "user.name=ATF", "-c", "user.email=atf@localhost",
                    "commit-tree", lines[0], "-p", head, "-p", child, "-m", $"Integrate child {memberOrder}");
                intent = new(memberOrder, child, head, after, false);
                store.SaveIntent(key, intent);
            }
            if (intent.ChildHead != child) { throw new InvalidOperationException("Child changed after integration intent."); }
            if (intent.Applied) { return intent; }
            if (head == intent.BeforeSha)
            {
                await Git(workspace.LeadPath, "merge", "--ff-only", intent.AfterSha);
            }
            else if (head != intent.AfterSha) { throw new InvalidOperationException("Lead changed during integration; reconciliation required."); }
            store.MarkApplied(key, memberOrder);
            return intent with { Applied = true };
        }
        finally { gate.Release(); }
    }

    static async Task Clean(string path, string? artefactFolder)
    {
        if ((await Git(path, "status", "--porcelain", "--untracked-files=all"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line => !Publication.BranchPublisher.IsStagedArtefact(line, artefactFolder)))
        {
            throw new InvalidOperationException("Integration requires clean committed work; files retained.");
        }
    }

    /// <summary>Explicit document paths only; child namespaces never overwrite lead documents.</summary>
    public void GatherDocuments(string key, int order, IEnumerable<string> relativePaths)
    {
        var workspace = store.Get(key) ?? throw new InvalidOperationException("Workspace missing.");
        var member = workspace.Members.Single(m => m.Order == order);
        foreach (var relative in relativePaths)
        {
            var source = SafePath(member.Path, relative);
            if (Path.GetExtension(source).ToLowerInvariant() is not (".md" or ".html" or ".json"))
            {
                throw new InvalidOperationException("Unsupported text document.");
            }
            var destination = SafePath(workspace.StagingPath, Path.Combine("child-" + order, relative));
            PrivateFiles.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Frozen snapshots are immutable; identical retry is harmless.
            if (new FileInfo(source).Length > 4 * 1024 * 1024) { throw new InvalidOperationException("Document too large."); }
            var bytes = File.ReadAllBytes(source);
            if (File.Exists(destination))
            {
                if (!File.ReadAllBytes(destination).SequenceEqual(bytes)) { throw new InvalidOperationException("Staged document changed."); }
                continue;
            }
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = new FileStream(temporary, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write)))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            File.Move(temporary, destination);
        }
    }

    static string SafePath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Document escapes owned directory.");
        }
        for (var path = full; path is not null; path = Path.GetDirectoryName(path))
        {
            if (new FileInfo(path).LinkTarget is not null) { throw new InvalidOperationException("Symlink document path refused."); }
            if (path == Path.GetFullPath(root)) { break; }
        }
        return full;
    }
}

public sealed class WorkspaceConflictException(string[] files) : InvalidOperationException("Child integration conflict: " + string.Join(", ", files))
{
    public IReadOnlyList<string> Files { get; } = files;
}
