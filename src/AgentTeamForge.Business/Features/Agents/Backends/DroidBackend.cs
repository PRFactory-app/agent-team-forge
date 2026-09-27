namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Droid exec JSON mode; piped stdin keeps delivery behind the dispatcher checkpoint.</summary>
public sealed class DroidBackend(string executable = "droid") : IJobBackend
{
    public IBackendRun Start(BackendRequest request) =>
        JsonCliBackend.Start(executable, "droid", request, BuildArguments(request));

    internal static List<string> BuildArguments(BackendRequest request)
    {
        List<string> args = ["exec", "--output-format", "json", "--skip-permissions-unsafe"];
        if (request.WorkingDirectory is { } cwd) { args.AddRange(["--cwd", cwd]); }
        if (request.ResumeSessionId is { } id) { args.AddRange(["--session-id", id]); }
        foreach (var part in request.Options.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Split('=', 2) is ["model", { Length: > 0 } model]) { args.AddRange(["--model", model]); }
            if (part.Split('=', 2) is ["effort", { Length: > 0 } effort])
            {
                args.AddRange(["--reasoning-effort", effort == "ultra" ? "max" : effort]);
            }
        }
        return args;
    }
}
