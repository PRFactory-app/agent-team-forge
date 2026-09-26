using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Closes only an idle interactive session held by this daemon's owned backend.</summary>
public sealed class StopAgent(JobStore store, BoundPrincipal principal, BackendCatalog backends)
{
    public JobResult Execute(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64)
        {
            return JobResult.Fail(JobErrors.InvalidRequest);
        }

        try
        {
            var job = store.GetJob(jobId);
            if (job is null || job.Principal != principal.Principal || job.Team != principal.Team)
            {
                return JobResult.Fail(JobErrors.NotFound);
            }
            if (job.Status is JobStatus.Queued or JobStatus.Running)
            {
                return JobResult.Fail(JobErrors.InvalidRequest);
            }
            var stopped = job.SessionId is { } sessionId
                && backends.Resolve(job.Backend) is IInteractiveSessionStop interactive
                && interactive.StopIdleSession(sessionId);
            return JobResult.Ok(GetJob.ToView(job), stopped ? "agent_stopped" : "agent_not_running");
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex));
        }
        catch (Exception ex) when (ex is HerdrLaunchException or IOException or UnauthorizedAccessException)
        {
            return JobResult.Fail(JobErrors.BackendUnavailable);
        }
    }
}
