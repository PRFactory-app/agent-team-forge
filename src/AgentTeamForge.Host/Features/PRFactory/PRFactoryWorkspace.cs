using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>Connector seam: authority must be checked before calling; dispatch only after preparation succeeds.</summary>
public sealed class PRFactoryWorkspace(TeamWorkspace workspaces)
{
    public Task<WorkspaceSnapshot> PrepareAsync(WorkspaceRequest request) => workspaces.PrepareAsync(request);

    public WorkspaceSnapshot? Get(string key) => workspaces.Get(key);

    public Task AlignChildrenAsync(WorkspaceSnapshot workspace, string head) => workspaces.AlignChildrenAsync(workspace, head);

    public async Task IntegrateChildrenAsync(WorkspaceSnapshot workspace, string? artefactFolder = null)
    {
        foreach (var member in workspace.Members.OrderBy(m => m.Order))
        {
            await workspaces.IntegrateAsync(workspace.Key, member.Order, artefactFolder);
        }
    }

    public void GatherDocuments(WorkspaceSnapshot workspace, int memberOrder, IEnumerable<string> paths) =>
        workspaces.GatherDocuments(workspace.Key, memberOrder, paths);
}
