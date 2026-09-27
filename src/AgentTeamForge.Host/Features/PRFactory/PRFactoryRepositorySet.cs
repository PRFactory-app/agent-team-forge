using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

public sealed record RepositoryManifestMember(string Name, string Path);
public sealed record RepositoryManifestEntry(string Id, string Name, string BaseBranch, bool ReadOnly,
    string LeadPath, RepositoryManifestMember[] ChildPaths);

[JsonSerializable(typeof(RepositoryManifestEntry[]))]
internal sealed partial class RepositoryManifestJson : JsonSerializerContext;

/// <summary>Validated accepted repository metadata. Server authorization remains authoritative.</summary>
public sealed class PRFactoryRepositorySet(PRFactoryRepositorySetStore store, PRFactoryWorkspace workspaces,
    PRFactoryWorkspaceStore workspaceStore, PRFactoryHandoverStore handovers)
{
    public RepositorySetSnapshot? Get(string key) => store.Get(key);

    public async Task RecoverRefreshAsync(string key)
    {
        if (store.Refresh(key) is not { State: "pending" } batch) { return; }
        foreach (var original in batch.Originals)
        {
            var workspace = workspaces.Get(original.WorkspaceKey) ?? throw new InvalidOperationException("Repository workspace missing.");
            if (Directory.Exists(workspace.LeadPath))
            {
                try { await TeamWorkspace.Git(workspace.LeadPath, "rebase", "--abort"); }
                catch (InvalidOperationException) { /* No rebase was in progress. */ }
                // Never move a branch ATF does not own: a switched lead is retained for reconciliation.
                await RequireBranch(workspace.LeadPath, workspace.InternalBranch!);
                await TeamWorkspace.Git(workspace.LeadPath, "reset", "--hard", original.HeadSha);
            }
            // Before the first turn children only hold ATF's own alignment to the refreshed tip.
            foreach (var member in workspace.Members.Where(m => Directory.Exists(m.Path)))
            {
                var head = await TeamWorkspace.Git(member.Path, "rev-parse", "HEAD");
                if (head == original.StartingSha) { continue; }
                await RequireBranch(member.Path, member.Branch!);
                if (original.CurrentBaseSha is null || !await IsAncestor(member.Path, original.CurrentBaseSha, head)
                    || (await TeamWorkspace.Git(member.Path, "status", "--porcelain", "--untracked-files=all")).Length != 0)
                {
                    throw new InvalidOperationException("Child workspace changed during base refresh; retained for reconciliation.");
                }
                await TeamWorkspace.Git(member.Path, "reset", "--hard", original.StartingSha);
            }
            workspaceStore.RestoreBase(original.WorkspaceKey, original.BaseSha, original.StartingSha);
            handovers.FinishRefresh(original.WorkspaceKey, "restored");
        }
        store.FinishRefresh(key, "restored");
    }

    static async Task<bool> IsAncestor(string cwd, string ancestor, string head)
    {
        try { await TeamWorkspace.Git(cwd, "merge-base", "--is-ancestor", ancestor, head); return true; }
        catch (InvalidOperationException) { return false; }
    }

    static async Task RequireBranch(string cwd, string branch)
    {
        if (await TeamWorkspace.Git(cwd, "symbolic-ref", "HEAD") != "refs/heads/" + branch)
        {
            throw new InvalidOperationException("Workspace branch changed; retained for reconciliation.");
        }
    }

    public async Task<IReadOnlyList<(RepositorySetMember Entry, BaseFreshnessResult Result)>> RefreshAsync(
        RepositorySetSnapshot set, string? primaryPlanBasisSha)
    {
        await RecoverRefreshAsync(set.WorkspaceKey);
        if (store.Refresh(set.WorkspaceKey) is { State: "done" })
        {
            var stable = true;
            foreach (var entry in set.Members)
            {
                var workspace = workspaces.Get(entry.WorkspaceKey)!;
                var remote = await TeamWorkspace.Git(workspace.LeadPath, "ls-remote", "--heads", "origin", "refs/heads/" + workspace.BaseBranch);
                stable &= remote.StartsWith(workspace.BaseSha + "\t", StringComparison.OrdinalIgnoreCase);
            }
            if (stable)
            {
                return [.. set.Members.Select(entry => (entry,
                    new BaseFreshnessResult(workspaces.Get(entry.WorkspaceKey)!.BaseSha!, workspaces.Get(entry.WorkspaceKey)!.BaseSha!,
                        0, handovers.Refresh(entry.WorkspaceKey)?.Action ?? "None",
                        JobWorktree.Head(workspaces.Get(entry.WorkspaceKey)!.LeadPath), [], true)))];
            }
            store.FinishRefresh(set.WorkspaceKey, "restored");
            foreach (var entry in set.Members) { handovers.FinishRefresh(entry.WorkspaceKey, "restored"); }
        }
        var originals = new List<RepositoryRefreshOriginal>();
        var drift = new List<(RepositorySetMember, BaseFreshnessResult)>();
        var observed = new Dictionary<string, string>(StringComparer.Ordinal);
        // Observe every base and checkout before changing any local branch.
        foreach (var entry in set.Members)
        {
            var workspace = workspaces.Get(entry.WorkspaceKey)!;
            foreach (var path in workspace.Members.Select(m => m.Path).Prepend(workspace.LeadPath))
            {
                if ((await TeamWorkspace.Git(path, "status", "--porcelain", "--untracked-files=all")).Length != 0)
                {
                    throw new InvalidOperationException("Base refresh requires clean repository checkouts.");
                }
            }
            var current = await TeamWorkspace.Git(workspace.LeadPath, "ls-remote", "--heads", "origin", "refs/heads/" + workspace.BaseBranch);
            if (current.Split('\t').Length != 2) { throw new InvalidOperationException("Repository base branch is unavailable."); }
            observed.Add(entry.WorkspaceKey, current.Split('\t')[0]);
            var planBasisSha = entry.PlanBasisCommitSha ?? (entry.WorkspaceKey == set.WorkspaceKey ? primaryPlanBasisSha : null);
            if (planBasisSha is not null && !current.StartsWith(planBasisSha + "\t", StringComparison.OrdinalIgnoreCase))
            {
                drift.Add((entry, new BaseFreshnessResult(workspace.BaseSha!, current.Split('\t')[0], 0,
                    "None", JobWorktree.Head(workspace.LeadPath), [], false)));
            }
            originals.Add(new(entry.WorkspaceKey, workspace.BaseSha!, workspace.StartingSha!,
                JobWorktree.Head(workspace.LeadPath)!, observed[entry.WorkspaceKey]));
        }
        if (drift.Count > 0) { return drift; }
        store.BeginRefresh(set.WorkspaceKey, [.. originals]);
        var results = new List<(RepositorySetMember, BaseFreshnessResult)>();
        try
        {
            foreach (var entry in set.Members)
            {
                var result = await workspaces.Freshness(handovers).EnsureFreshAsync(workspaces.Get(entry.WorkspaceKey)!,
                    expectedCurrentSha: observed[entry.WorkspaceKey]);
                results.Add((entry, result));
                if (!result.AgentMayRun)
                {
                    await RecoverRefreshAsync(set.WorkspaceKey);
                    // Earlier repositories were rolled back to their recorded base; never report their refresh.
                    return [.. results.Select(r => r.Item2.AgentMayRun
                        ? (r.Item1, r.Item2 with { Action = "None",
                            HeadSha = originals.Single(o => o.WorkspaceKey == r.Item1.WorkspaceKey).HeadSha, AgentMayRun = false })
                        : r)];
                }
            }
            store.FinishRefresh(set.WorkspaceKey, "done");
            return results;
        }
        catch
        {
            await RecoverRefreshAsync(set.WorkspaceKey);
            throw;
        }
    }

    public static bool HasSecondaries(PRFactoryWorkItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ContextJson)) { return false; }
        try
        {
            using var json = JsonDocument.Parse(item.ContextJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object) { return true; }
            if (!json.RootElement.TryGetProperty("repositories", out var repositories)) { return false; }
            if (repositories.ValueKind != JsonValueKind.Object) { return true; }
            if (!repositories.TryGetProperty("secondary", out var secondary)) { return false; }
            return secondary.ValueKind != JsonValueKind.Array || secondary.GetArrayLength() > 0;
        }
        catch (JsonException) { return true; }
    }

    public static async Task<RepositorySetMember[]> ParseAsync(PRFactoryWorkItem item,
        IReadOnlyList<RepositoryMapping> mappings, string primaryKey)
    {
        if (item.RepositoryId is not Guid primary || primary == Guid.Empty)
        {
            throw new InvalidOperationException("Multi-repository work requires a primary repository.");
        }
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(item.ContextJson ?? throw new InvalidOperationException("Repository set missing.")); }
        catch (JsonException ex) { throw new InvalidOperationException("Malformed repository set in accepted claim.", ex); }
        using var json = parsed;
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || !json.RootElement.TryGetProperty("repositories", out var repositories)
            || repositories.ValueKind != JsonValueKind.Object
            || !repositories.TryGetProperty("secondary", out var entries))
        {
            throw new InvalidOperationException("Accepted repository set is missing its secondary list.");
        }
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Secondary repository set missing.");
        }
        var specs = new List<(Guid Id, string? Name, string? CloneUrl, string? DefaultBranch, bool ReadOnly,
            string? BaseSha, string? StartBranch, string? StartSha, string? PublishBranch, string? PlanBasisSha)>
            { (primary, null, null, item.BaseSnapshot?.Branch, item.ReadOnly, item.BaseSnapshot?.CommitSha,
                item.Continuation?.Branch ?? item.StartFromBranch, item.Continuation?.CommitSha ?? item.StartCommitSha,
                item.PublishBranch, item.PlanBasisCommitSha) };
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var idValue)
                || !Guid.TryParse(idValue.GetString(), out var id) || id == Guid.Empty)
            {
                throw new InvalidOperationException("Invalid secondary repository identity.");
            }
            string? Read(string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            specs.Add((id, Read("name"), Read("cloneUrl"), Read("defaultBranch"),
                item.ReadOnly || entry.TryGetProperty("readOnly", out var readOnly) && readOnly.ValueKind == JsonValueKind.True,
                Read("baseCommitSha"), Read("startFromBranch"), Read("startCommitSha"), Read("publishBranch"),
                Read("planBasisCommitSha")));
        }
        if (specs.Select(s => s.Id).Distinct().Count() != specs.Count)
        {
            throw new InvalidOperationException("Duplicate repository identity in accepted set.");
        }
        var usedPaths = new HashSet<string>(StringComparer.Ordinal);
        var usedRemotes = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RepositorySetMember>();
        foreach (var (id, name, cloneUrl, defaultBranch, readOnly, baseSha, startBranch, startSha, publishBranch, planBasisSha) in specs)
        {
            var mapping = mappings.SingleOrDefault(m => m.Id == id)
                ?? throw new InvalidOperationException($"Repository {id:D} has no approved local mapping.");
            var path = Path.GetFullPath(mapping.Directory);
            if (!Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null
                || usedPaths.Any(existing => path == existing || path.StartsWith(existing + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || existing.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Repository mapping paths collide or are unavailable.");
            }
            usedPaths.Add(path);
            var actualRemote = await TeamWorkspace.OriginAsync(path);
            if (mapping.Remote is not null && mapping.Remote != actualRemote)
            {
                throw new InvalidOperationException("Approved mapping remote differs from checkout origin.");
            }
            var remote = mapping.Remote ?? actualRemote;
            if (cloneUrl is { Length: > 0 } && cloneUrl != remote)
            {
                throw new InvalidOperationException("Secondary clone URL differs from approved remote.");
            }
            var remoteIdentity = remote.StartsWith("file://", StringComparison.Ordinal) ? new Uri(remote).LocalPath : remote;
            if (Path.IsPathRooted(remoteIdentity)) { remoteIdentity = Path.GetFullPath(remoteIdentity); }
            if (!usedRemotes.Add(remoteIdentity))
            {
                throw new InvalidOperationException("Repository mappings alias the same remote.");
            }
            var branch = (id == primary ? item.BaseSnapshot?.Branch : null)
                ?? mapping.BaseBranch ?? defaultBranch ?? await TeamWorkspace.DefaultBranchAsync(path);
            _ = await TeamWorkspace.Git(path, "check-ref-format", "refs/heads/" + branch);
            result.Add(new(id.ToString("D"), name is { Length: > 0 } ? name : id.ToString("D"),
                remote, branch, path, id == primary ? primaryKey : primaryKey + "|" + id.ToString("D"), readOnly,
                baseSha, startBranch, startSha, publishBranch, planBasisSha));
        }
        return [.. result];
    }

    public async Task<RepositorySetSnapshot> PrepareAsync(string key, PRFactoryWorkItem item,
        IReadOnlyList<RepositoryMapping> mappings, string[] members)
    {
        var entries = await ParseAsync(item, mappings, key);
        var primary = workspaces.Get(key) ?? throw new InvalidOperationException("Primary workspace must be prepared first.");
        var snapshot = new RepositorySetSnapshot(key, entries, Path.Combine(primary.Root, "repositories.json"));
        store.Save(snapshot);
        foreach (var entry in entries.Skip(1))
        {
            var saved = workspaces.Get(entry.WorkspaceKey);
            var publish = entry.PublishBranch ?? primary.PublishBranch;
            var request = saved is null
                ? new WorkspaceRequest(entry.WorkspaceKey, primary.Root, entry.Id, entry.MappingPath, entry.Remote,
                    entry.BaseBranch, publish, entry.ReadOnly, members,
                    StartFromBranch: entry.StartFromBranch, StartCommitSha: entry.StartCommitSha,
                    ProjectInit: string.Equals(item.TicketSource, "ProjectInit", StringComparison.OrdinalIgnoreCase),
                    ExpectedBaseSha: entry.BaseCommitSha)
                : new WorkspaceRequest(entry.WorkspaceKey, primary.Root, saved.RepositoryId, saved.RepositoryPath,
                    saved.Remote, saved.BaseBranch, saved.PublishBranch, saved.ReadOnly, members);
            await workspaces.PrepareAsync(request);
        }
        RepositoryManifestEntry[] manifest = [.. entries.Select(entry => new RepositoryManifestEntry(entry.Id, entry.Name, entry.BaseBranch,
            entry.ReadOnly, workspaces.Get(entry.WorkspaceKey)!.LeadPath,
            [.. workspaces.Get(entry.WorkspaceKey)!.Members.Select(m => new RepositoryManifestMember(m.Name, m.Path))]))];
        var content = JsonSerializer.Serialize(manifest, RepositoryManifestJson.Default.RepositoryManifestEntryArray);
        if (File.Exists(snapshot.ManifestPath) && File.ReadAllText(snapshot.ManifestPath) != content)
        {
            throw new InvalidOperationException("Repository manifest changed after acceptance.");
        }
        if (!File.Exists(snapshot.ManifestPath))
        {
            PRFactoryConnection.WritePrivate(snapshot.ManifestPath, System.Text.Encoding.UTF8.GetBytes(content));
        }
        return snapshot;
    }
}
